using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Discord;
using Ember.Config;
using Ember.Discord;
using Ember.Observability;
using Ember.Sessions;
using Microsoft.Extensions.Options;

namespace Ember.Build;

/// <summary>
/// Runs a build: cuts a worktree, drops <c>.ember/PLAN.md</c>, spawns headless Claude Code
/// (<c>claude -p --output-format stream-json</c>), digests the JSONL event stream into one
/// throttled Discord status message. On success it hands off to <see cref="PullRequest"/>
/// (push + draft PR) and removes the worktree, leaving the session in <c>PrOpen</c>.
/// </summary>
public sealed class BuilderRunner
{
    private const string KickoffPrompt =
        """
        You are ember's autonomous build agent, running headless in a dedicated git worktree.

        The file `.ember/PLAN.md` in this repository is an implementation plan that was
        reviewed and approved before this build started. Your job is to implement it.

        Steps:
        1. Read `.ember/PLAN.md` in full before doing anything else.
        2. Implement the plan in this repository. Make all the code changes it calls for.
        3. Verify the result builds. If the project has a test suite, run it.
        4. Commit your work on the current git branch with clear commit messages. Multiple
           commits are fine.

        Constraints:
        - Do NOT push, open a pull request, merge, or switch branches — handoff happens
          outside this session.
        - Work only inside this repository directory.
        - If the plan is ambiguous, make a reasonable decision and note it in your commit
          message and final summary.

        When you are done, give a short summary of what you changed and any decisions made.
        """;

    private readonly SessionStore _sessions;
    private readonly ThreadGateway _threads;
    private readonly PullRequest _pullRequest;
    private readonly EmberOptions _options;
    private readonly ILogger<BuilderRunner> _logger;

    public BuilderRunner(
        SessionStore sessions,
        ThreadGateway threads,
        PullRequest pullRequest,
        IOptions<EmberOptions> options,
        ILogger<BuilderRunner> logger)
    {
        _sessions = sessions;
        _threads = threads;
        _pullRequest = pullRequest;
        _options = options.Value;
        _logger = logger;
    }

    private enum Outcome { Success, Failed, Aborted, TimedOut }

    /// <summary>
    /// Runs the build for a session to a terminal state. Never throws — every failure path
    /// records the session state and reports to the thread.
    /// </summary>
    public async Task RunAsync(Session session, CancellationToken ct)
    {
        using var activity = Telemetry.Activity.StartActivity("build.run");
        activity?.SetTag("ember.repo", session.Repo);
        activity?.SetTag("ember.thread_id", session.ThreadId);

        var stopwatch = Stopwatch.StartNew();
        var outcome = Outcome.Failed;
        try
        {
            outcome = await ExecuteAsync(session, activity, ct);
        }
        catch (Exception ex)
        {
            // ExecuteAsync handles its own failures; this is a last-resort backstop.
            _logger.LogError(ex, "Build crashed unexpectedly for session {ThreadId}.", session.ThreadId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try
            {
                session.State = SessionState.Failed;
                session.LastError = ex.Message;
                _sessions.Update(session);
            }
            catch (Exception storeEx)
            {
                _logger.LogError(storeEx, "Could not record FAILED state for session {ThreadId}.", session.ThreadId);
            }
        }
        finally
        {
            var tag = new KeyValuePair<string, object?>("outcome", outcome.ToString().ToLowerInvariant());
            Telemetry.BuildsCompleted.Add(1, tag);
            Telemetry.BuildDuration.Record(stopwatch.Elapsed.TotalSeconds, tag);
            activity?.SetTag("ember.build.outcome", outcome.ToString().ToLowerInvariant());
        }
    }

    private async Task<Outcome> ExecuteAsync(Session session, Activity? activity, CancellationToken ct)
    {
        var repoPath = _options.Repos.TryGetValue(session.Repo, out var entry) ? entry.Path : session.Repo;
        var status = new StatusMessage(_threads, session.ThreadId,
            await _threads.CreateMessageAsync(session.ThreadId, "🔨 Build starting — preparing the worktree…"));

        // ── Worktree ──────────────────────────────────────────────────────────────────
        WorktreeInfo worktree;
        try
        {
            var slug = Worktree.MakeSlug(session.Brief, session.ThreadId);
            worktree = await Worktree.CreateAsync(repoPath, Path.GetFullPath(_options.WorktreeRoot), slug, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await AbortedEarlyAsync(session, status);
        }
        catch (Exception ex)
        {
            return await FailAsync(session, status, activity,
                $"could not create the build worktree: {ex.Message}", worktreePath: null);
        }

        session.BranchName = worktree.Branch;
        session.WorktreePath = worktree.Path;
        _sessions.Update(session);
        activity?.SetTag("ember.branch", worktree.Branch);

        // ── Plan artifact ─────────────────────────────────────────────────────────────
        try
        {
            await PlanArtifact.WriteAsync(
                repoPath, worktree.Path,
                session.PlanSnapshot ?? "(no plan snapshot was recorded for this session)", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await AbortAsync(session, status, worktree);
        }
        catch (Exception ex)
        {
            return await FailAsync(session, status, activity,
                $"could not write `.ember/PLAN.md`: {ex.Message}", worktree.Path);
        }

        // ── Run the builder ───────────────────────────────────────────────────────────
        return await RunBuilderAsync(session, repoPath, worktree, status, activity, ct);
    }

    private async Task<Outcome> RunBuilderAsync(
        Session session, string repoPath, WorktreeInfo worktree,
        StatusMessage status, Activity? activity, CancellationToken ct)
    {
        var resolved = ResolveExecutable(_options.Builder.Command);
        if (resolved is null)
            return await FailAsync(session, status, activity,
                $"the builder executable `{_options.Builder.Command}` was not found. Install Claude Code, "
                + "or set `Ember:Builder:Command` to its full path.", worktree.Path);

        var (fileName, args) = BuildArguments(resolved.Value);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = worktree.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.Builder.TimeoutMinutes));
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var digest = new BuildDigest(worktree.Branch);
        var startedAt = DateTimeOffset.UtcNow;
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderr)
                stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return await FailAsync(session, status, activity,
                $"could not launch the builder (`{fileName}`): {ex.Message}", worktree.Path);
        }

        process.BeginErrorReadLine();

        // Feed the kickoff prompt over stdin — avoids every command-line quoting concern.
        try
        {
            await process.StandardInput.WriteAsync(KickoffPrompt.AsMemory(), ct);
            process.StandardInput.Close();
        }
        catch
        {
            // The process may have exited already; the result is handled below.
        }

        await status.UpdateAsync(digest.RenderProgress(startedAt), force: true);

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(linked.Token)) is not null)
            {
                digest.Ingest(line);
                await status.UpdateAsync(digest.RenderProgress(startedAt), force: false);
            }
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested)
                return await AbortAsync(session, status, worktree);

            return await FailAsync(session, status, activity,
                $"the builder exceeded its {timeout.TotalMinutes:0}-minute time limit and was stopped.",
                worktree.Path, Outcome.TimedOut);
        }

        var exitCode = process.ExitCode;
        var result = digest.Result;
        var failed = result is { IsError: true } || (result is null && exitCode != 0);

        if (failed)
        {
            var reason = result is { IsError: true }
                ? $"the builder reported an error ({result.Subtype ?? "unknown"})."
                : $"the builder exited with code {exitCode}.";
            var tail = LogTail(digest, stderr.ToString());
            if (tail is not null)
                reason += $"\n```\n{tail}\n```";
            return await FailAsync(session, status, activity, reason, worktree.Path);
        }

        try
        {
            var verifyFailure = await VerifyBuildAsync(worktree, status, ct);
            if (verifyFailure is not null)
                return await FailAsync(session, status, activity, verifyFailure, worktree.Path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await AbortAsync(session, status, worktree);
        }

        return await SucceedAsync(session, repoPath, status, worktree, digest, activity, ct);
    }

    /// <summary>
    /// Runs the configured verify command in the build worktree — an external check that does
    /// not trust the builder's self-reported success. Returns <c>null</c> when verification
    /// passes (or is disabled by an empty command), or a failure reason when it does not.
    /// </summary>
    private async Task<string?> VerifyBuildAsync(WorktreeInfo worktree, StatusMessage status, CancellationToken ct)
    {
        var command = _options.Builder.VerifyCommand?.Trim();
        if (string.IsNullOrEmpty(command))
            return null;   // the gate is disabled

        await status.UpdateAsync($"🔍 Build finished — verifying with `{command}`…", force: true);

        var (shell, flag) = OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.Builder.VerifyTimeoutMinutes));
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        ProcessRunner.Result result;
        try
        {
            result = await ProcessRunner.RunAsync(shell, new[] { flag, command }, worktree.Path, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return $"the verify command `{command}` exceeded its {timeout.TotalMinutes:0}-minute limit.";
        }

        if (result.Ok)
            return null;

        var reason = $"verification failed — `{command}` exited with code {result.ExitCode}.";
        var tail = VerifyTail(result);
        if (tail is not null)
            reason += $"\n```\n{tail}\n```";
        return reason;
    }

    /// <summary>The tail of a failed verify command's output, clipped for a Discord message.</summary>
    private static string? VerifyTail(ProcessRunner.Result result)
    {
        var text = result.StdOut.Trim();
        if (text.Length == 0)
            text = result.StdErr.Trim();
        if (text.Length == 0)
            return null;

        const int max = 1200;
        text = text.Replace("```", "ʼʼʼ");
        return text.Length <= max ? text : "…" + text[^max..];
    }

    private async Task<Outcome> SucceedAsync(
        Session session, string repoPath, StatusMessage status, WorktreeInfo worktree,
        BuildDigest digest, Activity? activity, CancellationToken ct)
    {
        string diff;
        try
        {
            diff = await Worktree.DiffStatAsync(worktree.Path, worktree.BaseRef, CancellationToken.None);
        }
        catch (Exception ex)
        {
            diff = $"(diff stat unavailable: {ex.Message})";
        }

        await status.UpdateAsync(
            $"🔨 Build finished — pushing the branch and opening a draft PR…\n{digest.RenderSummary()}",
            force: true);

        var pr = await _pullRequest.OpenAsync(session, worktree, ct);
        if (!pr.Ok)
        {
            // The build itself succeeded — keep the worktree so the operator can finish the
            // handoff (push / PR) by hand.
            session.State = SessionState.Failed;
            session.LastError = $"build succeeded but the PR handoff failed: {pr.Error}";
            _sessions.Update(session);
            await status.FinalizeAsync(
                $"⚠️ **Build succeeded, but the PR handoff failed** — {pr.Error}\n"
                + $"{digest.RenderSummary()} · changes vs `{worktree.BaseRef}`: {diff}\n"
                + $"Worktree kept: `{worktree.Path}` (branch `{worktree.Branch}`).");
            activity?.SetStatus(ActivityStatusCode.Error, pr.Error);
            _logger.LogError("PR handoff failed for session {ThreadId}: {Error}", session.ThreadId, pr.Error);
            return Outcome.Failed;
        }

        session.PrUrl = pr.Url;
        session.State = SessionState.PrOpen;
        session.LastError = null;
        _sessions.Update(session);

        // The PR is open and the branch is on the remote — the worktree is no longer needed.
        // The branch and PR are kept; only the working directory is removed.
        try
        {
            await Worktree.RemoveAsync(repoPath, worktree.Path, CancellationToken.None);
            session.WorktreePath = null;
            _sessions.Update(session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove worktree for session {ThreadId}.", session.ThreadId);
        }

        await status.FinalizeAsync(
            "✅ **Build complete — draft PR opened**\n"
            + $"{digest.RenderSummary()} · changes vs `{worktree.BaseRef}`: {diff}\n"
            + $"Branch `{worktree.Branch}`\n{pr.Url}");

        activity?.SetStatus(ActivityStatusCode.Ok);
        _logger.LogInformation("Session {ThreadId} reached PR_OPEN: {Url}", session.ThreadId, pr.Url);
        return Outcome.Success;
    }

    private async Task<Outcome> FailAsync(
        Session session, StatusMessage status, Activity? activity,
        string reason, string? worktreePath, Outcome outcome = Outcome.Failed)
    {
        session.State = SessionState.Failed;
        session.LastError = reason;
        _sessions.Update(session);

        var text = $"❌ **Build failed** — {reason}";
        if (worktreePath is not null)
            text += $"\nWorktree kept for inspection: `{worktreePath}`";
        await status.FinalizeAsync(text);

        activity?.SetStatus(ActivityStatusCode.Error, reason);
        _logger.LogError("Build failed for session {ThreadId}: {Reason}", session.ThreadId, reason);
        return outcome;
    }

    private async Task<Outcome> AbortAsync(Session session, StatusMessage status, WorktreeInfo worktree)
    {
        // /abort during a build keeps the worktree so the operator can inspect partial work.
        session.State = SessionState.Aborted;
        _sessions.Update(session);
        await status.FinalizeAsync(
            "🛑 **Build aborted** — the builder was stopped.\n"
            + $"Worktree kept: `{worktree.Path}` (branch `{worktree.Branch}`).");
        _logger.LogInformation("Build aborted for session {ThreadId}.", session.ThreadId);
        return Outcome.Aborted;
    }

    private async Task<Outcome> AbortedEarlyAsync(Session session, StatusMessage status)
    {
        session.State = SessionState.Aborted;
        _sessions.Update(session);
        await status.FinalizeAsync("🛑 **Build aborted** before it started — no worktree was created.");
        _logger.LogInformation("Build aborted before start for session {ThreadId}.", session.ThreadId);
        return Outcome.Aborted;
    }

    private (string FileName, IReadOnlyList<string> Args) BuildArguments((string FileName, string[] Prefix) resolved)
    {
        var builder = _options.Builder;
        var args = new List<string>(resolved.Prefix)
        {
            "-p", "--output-format", "stream-json", "--verbose",
        };
        if (builder.Bare)
            args.Add("--bare");
        if (builder.SkipPermissions)
            args.Add("--dangerously-skip-permissions");
        if (builder.MaxBudgetUsd > 0)
        {
            args.Add("--max-budget-usd");
            args.Add(builder.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture));
        }
        args.AddRange(builder.ExtraArgs);
        return (resolved.FileName, args);
    }

    /// <summary>
    /// Resolves the builder command to a runnable (file, prefix-args) pair. A bare name is
    /// searched on PATH (and PATHEXT on Windows, so the npm <c>claude.cmd</c> shim resolves);
    /// a <c>.cmd</c>/<c>.bat</c> is wrapped in <c>cmd.exe /c</c> so it can be launched
    /// without a shell.
    /// </summary>
    private static (string FileName, string[] Prefix)? ResolveExecutable(string command)
    {
        var resolved = FindOnPath(command);
        if (resolved is null)
            return null;

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (OperatingSystem.IsWindows() && ext is ".cmd" or ".bat")
            return ("cmd.exe", new[] { "/c", resolved });

        return (resolved, Array.Empty<string>());
    }

    private static string? FindOnPath(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var extensions = new List<string> { "" };
        if (OperatingSystem.IsWindows())
        {
            var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            extensions.AddRange(pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var dir in dirs)
        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(dir, command + ext);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited between the check and the kill.
        }
    }

    private static string? LogTail(BuildDigest digest, string stderr)
    {
        var tail = stderr.Trim();
        if (tail.Length == 0)
            tail = digest.LastText ?? "";
        if (tail.Length == 0)
            return null;

        const int max = 1200;
        tail = tail.Replace("```", "ʼʼʼ");
        return tail.Length <= max ? tail : "…" + tail[^max..];
    }

    /// <summary>One throttled Discord message that the build keeps editing in place.</summary>
    private sealed class StatusMessage
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(4);
        private const int MaxLength = 1950;

        private readonly ThreadGateway _threads;
        private readonly string _threadId;
        private readonly IUserMessage? _message;
        private DateTimeOffset _lastEdit = DateTimeOffset.MinValue;

        public StatusMessage(ThreadGateway threads, string threadId, IUserMessage? message)
        {
            _threads = threads;
            _threadId = threadId;
            _message = message;
        }

        /// <summary>Edits the status message, skipping edits that arrive faster than the throttle.</summary>
        public Task UpdateAsync(string text, bool force)
        {
            if (_message is null)
                return Task.CompletedTask;
            if (!force && DateTimeOffset.UtcNow - _lastEdit < MinInterval)
                return Task.CompletedTask;
            _lastEdit = DateTimeOffset.UtcNow;
            return EditAsync(text);
        }

        /// <summary>Posts the terminal result — editing the status message, or a fresh post if that fails.</summary>
        public async Task FinalizeAsync(string text)
        {
            text = Clip(text);
            if (_message is not null)
            {
                try
                {
                    await _message.ModifyAsync(m => m.Content = text);
                    return;
                }
                catch
                {
                    // Fall through — the message may have been deleted or the thread archived.
                }
            }
            await _threads.PostAsync(_threadId, text);
        }

        private async Task EditAsync(string text)
        {
            try
            {
                await _message!.ModifyAsync(m => m.Content = Clip(text));
            }
            catch
            {
                // Best-effort: thread archived, message deleted, or rate-limited.
            }
        }

        private static string Clip(string text) =>
            text.Length <= MaxLength ? text : text[..MaxLength] + "…";
    }

    /// <summary>Accumulates a human-readable picture of the build from the stream-json events.</summary>
    private sealed class BuildDigest
    {
        private readonly string _branch;

        public BuildDigest(string branch) => _branch = branch;

        public int ToolActions { get; private set; }
        public string? LastActivity { get; private set; }
        public string? LastText { get; private set; }
        public ResultInfo? Result { get; private set; }

        public sealed record ResultInfo(bool IsError, string? Subtype, double? CostUsd, int? NumTurns);

        /// <summary>Parses one JSONL line from the builder's stdout. Non-JSON lines are ignored.</summary>
        public void Ingest(string line)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] != '{')
                return;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
                    return;

                switch (type.GetString())
                {
                    case "assistant":
                        IngestAssistant(root);
                        break;
                    case "result":
                        IngestResult(root);
                        break;
                    case "system":
                        LastActivity ??= "starting the builder…";
                        break;
                }
            }
        }

        private void IngestAssistant(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return;

            foreach (var block in content.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (blockType == "text" && block.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        LastText = Clip(text.Trim(), 280);
                }
                else if (blockType == "tool_use")
                {
                    ToolActions++;
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() : null;
                    block.TryGetProperty("input", out var input);
                    LastActivity = DescribeTool(name, input);
                }
            }
        }

        private void IngestResult(JsonElement root)
        {
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var subtype = root.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            double? cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cd) ? cd : null;
            int? turns = root.TryGetProperty("num_turns", out var t) && t.TryGetInt32(out var ti) ? ti : null;
            Result = new ResultInfo(isError, subtype, cost, turns);
        }

        public string RenderProgress(DateTimeOffset startedAt)
        {
            var elapsed = FormatDuration(DateTimeOffset.UtcNow - startedAt);
            var text = $"🔨 **Building** — `{_branch}`\nElapsed {elapsed} · {ToolActions} tool action(s)";
            if (LastActivity is not null)
                text += $"\nNow: {LastActivity}";
            return text;
        }

        public string RenderSummary()
        {
            var parts = new List<string>();
            if (Result?.NumTurns is { } turns)
                parts.Add($"{turns} turn(s)");
            parts.Add($"{ToolActions} tool action(s)");
            if (Result?.CostUsd is { } cost)
                parts.Add($"${cost:0.00}");
            return string.Join(" · ", parts);
        }

        private static string DescribeTool(string? name, JsonElement input)
        {
            string? Arg(string key) =>
                input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v)
                    ? v.GetString()
                    : null;

            return name switch
            {
                "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => $"editing {FileName(Arg("file_path"))}",
                "Read" => $"reading {FileName(Arg("file_path"))}",
                "Bash" => $"running `{Clip(Arg("command") ?? "a command", 80)}`",
                "Glob" or "Grep" => "searching the codebase",
                "Task" => "delegating to a subagent",
                "TodoWrite" => "updating its task list",
                "WebFetch" or "WebSearch" => "looking something up",
                null => "thinking…",
                _ => name,
            };
        }

        private static string FileName(string? path) =>
            string.IsNullOrEmpty(path) ? "a file" : $"`{Path.GetFileName(path)}`";

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text[..max] + "…";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h{span.Minutes:00}m";
        if (span.TotalMinutes >= 1)
            return $"{span.Minutes}m{span.Seconds:00}s";
        return $"{span.Seconds}s";
    }
}
