using System.Text.RegularExpressions;
using Ember.Observability;
using Ember.Sessions;

namespace Ember.Build;

/// <summary>
/// Hands a finished build off to GitHub: pushes the build branch and opens a draft PR with
/// the frozen plan snapshot as the body. ember owns the remote — the builder never gets push
/// or PR credentials (see PLAN.md "Security").
/// </summary>
public sealed class PullRequest
{
    private const string PrBodyFooter =
        "\n\n---\n_Opened by ember from the approved plan snapshot. Builder: headless Claude Code._";

    private static readonly Regex PrUrlPattern =
        new(@"https?://\S+/pull/\d+", RegexOptions.Compiled);

    private readonly ILogger<PullRequest> _logger;

    public PullRequest(ILogger<PullRequest> logger) => _logger = logger;

    /// <summary>Outcome of a PR handoff — a URL on success, or a reportable reason on failure.</summary>
    public sealed record Result(bool Ok, string? Url, string? Error)
    {
        public static Result Success(string url) => new(true, url, null);
        public static Result Fail(string error) => new(false, null, error);
    }

    /// <summary>
    /// Pushes the worktree's branch and opens a draft PR. Never throws — every failure path
    /// returns <see cref="Result.Fail"/> with a message safe to show the operator.
    /// </summary>
    public async Task<Result> OpenAsync(Session session, WorktreeInfo worktree, CancellationToken ct)
    {
        using var activity = Telemetry.Activity.StartActivity("pr.open");
        activity?.SetTag("ember.branch", worktree.Branch);

        try
        {
            var ensured = await EnsureCommitsAsync(worktree, ct);
            if (ensured is not null)
                return Result.Fail(ensured);

            var push = await ProcessRunner.RunAsync(
                "git", new[] { "-C", worktree.Path, "push", "-u", "origin", worktree.Branch }, null, ct);
            if (!push.Ok)
                return Result.Fail($"`git push` failed: {OneLine(push.StdErr, push.StdOut)}");

            var url = await CreateDraftPrAsync(session, worktree, ct);
            if (!url.Ok)
                return url;

            activity?.SetTag("ember.pr_url", url.Url);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Fail("the PR handoff was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PR handoff crashed for session {ThreadId}.", session.ThreadId);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            return Result.Fail($"the PR handoff failed unexpectedly: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures the branch has commits to PR. The builder is told to commit its work; if it
    /// left changes uncommitted, ember commits them so the work is not lost. Returns null on
    /// success, or an error string if the build produced nothing at all.
    /// </summary>
    private static async Task<string?> EnsureCommitsAsync(WorktreeInfo worktree, CancellationToken ct)
    {
        var count = await ProcessRunner.RunAsync(
            "git", new[] { "-C", worktree.Path, "rev-list", "--count", $"{worktree.BaseRef}..HEAD" }, null, ct);
        if (count.Ok && int.TryParse(count.StdOut.Trim(), out var commits) && commits > 0)
            return null;

        var status = await ProcessRunner.RunAsync(
            "git", new[] { "-C", worktree.Path, "status", "--porcelain" }, null, ct);
        if (status.StdOut.Trim().Length == 0)
            return "the builder produced no commits and no file changes — nothing to open a PR for.";

        // The builder changed files but did not commit — capture the work on its behalf.
        await ProcessRunner.RunAsync("git", new[] { "-C", worktree.Path, "add", "-A" }, null, ct);
        var commit = await ProcessRunner.RunAsync(
            "git",
            new[] { "-C", worktree.Path, "commit", "-m", "ember: capture builder changes left uncommitted" },
            null, ct);
        if (!commit.Ok)
            return $"the builder left changes uncommitted and ember could not commit them: "
                + $"{OneLine(commit.StdErr, commit.StdOut)}";

        return null;
    }

    private async Task<Result> CreateDraftPrAsync(Session session, WorktreeInfo worktree, CancellationToken ct)
    {
        var bodyFile = Path.GetTempFileName();
        try
        {
            var body = (string.IsNullOrWhiteSpace(session.PlanSnapshot) ? session.Brief : session.PlanSnapshot)
                + PrBodyFooter;
            await File.WriteAllTextAsync(bodyFile, body, ct);

            var args = new List<string>
            {
                "pr", "create", "--draft",
                "--title", PrTitle(session.Brief),
                "--body-file", bodyFile,
                "--head", worktree.Branch,
            };
            if (BaseBranch(worktree.BaseRef) is { } baseBranch)
            {
                args.Add("--base");
                args.Add(baseBranch);
            }

            ProcessRunner.Result create;
            try
            {
                create = await ProcessRunner.RunAsync("gh", args, worktree.Path, ct);
            }
            catch (Exception ex)
            {
                return Result.Fail(
                    $"could not launch the GitHub CLI (`gh`): {ex.Message}. The branch "
                    + $"`{worktree.Branch}` was pushed — install/authenticate `gh`, or open the PR by hand.");
            }

            if (!create.Ok)
                return Result.Fail(
                    $"`gh pr create` failed: {OneLine(create.StdErr, create.StdOut)}. "
                    + $"The branch `{worktree.Branch}` was pushed — the PR can be opened by hand.");

            var match = PrUrlPattern.Match(create.StdOut);
            if (!match.Success)
                return Result.Fail(
                    $"`gh pr create` did not return a PR URL. The branch `{worktree.Branch}` was pushed.");

            return Result.Success(match.Value);
        }
        finally
        {
            try
            {
                File.Delete(bodyFile);
            }
            catch
            {
                // A leftover temp file is harmless.
            }
        }
    }

    /// <summary>Strips an <c>origin/</c> prefix; returns null for a bare <c>HEAD</c> so `gh` infers the default.</summary>
    private static string? BaseBranch(string baseRef)
    {
        if (baseRef == "HEAD")
            return null;
        return baseRef.StartsWith("origin/", StringComparison.Ordinal) ? baseRef["origin/".Length..] : baseRef;
    }

    private static string PrTitle(string brief)
    {
        var title = brief.ReplaceLineEndings(" ").Trim();
        return title.Length <= 72 ? title : title[..72].TrimEnd() + "…";
    }

    private static string OneLine(string primary, string fallback)
    {
        var text = primary.Trim();
        if (text.Length == 0)
            text = fallback.Trim();
        text = text.ReplaceLineEndings(" ").Trim();
        if (text.Length == 0)
            return "(no output)";
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
