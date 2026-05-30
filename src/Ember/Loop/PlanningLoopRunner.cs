using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Ember.Config;
using Ember.Discord;
using Ember.Manifest;
using Ember.Observability;
using Ember.Sessions;
using Microsoft.Extensions.Options;

namespace Ember.Loop;

/// <summary>Runs the Claude/GPT planning loop for a session and hands the result to the gate.</summary>
public sealed class PlanningLoopRunner
{
    private readonly Planner _planner;
    private readonly Critic _critic;
    private readonly SessionStore _sessions;
    private readonly ThreadGateway _threads;
    private readonly ManifestLoader _manifest;
    private readonly EmberOptions _options;
    private readonly ILogger<PlanningLoopRunner> _logger;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public PlanningLoopRunner(
        Planner planner,
        Critic critic,
        SessionStore sessions,
        ThreadGateway threads,
        ManifestLoader manifest,
        IOptions<EmberOptions> options,
        ILogger<PlanningLoopRunner> logger)
    {
        _planner = planner;
        _critic = critic;
        _sessions = sessions;
        _threads = threads;
        _manifest = manifest;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Launches the planning loop for a session as a background task.</summary>
    public void Start(Session session)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(session.ThreadId, cts))
        {
            cts.Dispose();
            return;
        }
        _ = Task.Run(() => RunAsync(session, cts.Token));
    }

    /// <summary>Requests cancellation of a running loop. Returns false if none is running.</summary>
    public bool Cancel(string threadId)
    {
        if (!_running.TryGetValue(threadId, out var cts))
            return false;
        cts.Cancel();
        return true;
    }

    private async Task RunAsync(Session session, CancellationToken ct)
    {
        using var activity = Telemetry.Activity.StartActivity("plan.session");
        activity?.SetTag("ember.repo", session.Repo);

        try
        {
            var entry = _options.Repos.TryGetValue(session.Repo, out var e) ? e : null;
            var repoPath = entry?.Path ?? session.Repo;
            var repoContext = RepoContext.Gather(repoPath);
            if (entry?.Constellation is { } constellation)
            {
                var summary = await _manifest.LoadSummaryAsync(constellation, session.Repo, ct);
                if (summary is not null)
                    repoContext = summary + "\n\n" + repoContext;
            }

            await _threads.PostAsync(session.ThreadId, $"**Planning loop started** — drafting against `{session.Repo}`.");

            var plan = await _planner.DraftAsync(session.Brief, repoContext, ct);
            session.CurrentRound = 1;
            _sessions.Update(session);
            await _threads.PostAsync(session.ThreadId, $"**Plan — round 1 (Claude)**\n\n{plan}");

            var since = DateTimeOffset.UtcNow;
            var previousOpen = int.MaxValue;
            CriticVerdict verdict;
            GateReason gateReason;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                using (var round = Telemetry.Activity.StartActivity("plan.round"))
                {
                    round?.SetTag("ember.round", session.CurrentRound);
                    verdict = await _critic.ReviewAsync(session.Brief, plan, repoContext, ct);
                }
                await _threads.PostAsync(session.ThreadId, FormatVerdict(session.CurrentRound, verdict));

                if (verdict.Approved)
                {
                    gateReason = GateReason.Approved;
                    break;
                }
                if (session.CurrentRound >= _options.MaxPlanRounds)
                {
                    gateReason = GateReason.RoundCap;
                    break;
                }
                if (verdict.OpenIssues.Count >= previousOpen)
                {
                    gateReason = GateReason.Stalled;
                    break;
                }
                previousOpen = verdict.OpenIssues.Count;

                ct.ThrowIfCancellationRequested();

                var steering = await CollectSteeringAsync(session.ThreadId, since);
                since = DateTimeOffset.UtcNow;

                plan = await _planner.ReviseAsync(session.Brief, plan, verdict, steering, ct);
                session.CurrentRound++;
                _sessions.Update(session);
                await _threads.PostAsync(session.ThreadId, $"**Plan — round {session.CurrentRound} (Claude)**\n\n{plan}");
            }

            await EnterGateAsync(session, plan, gateReason, verdict);
        }
        catch (OperationCanceledException)
        {
            session.State = SessionState.Aborted;
            _sessions.Update(session);
            await SafePostAsync(session.ThreadId, "**Aborted** — the planning loop was cancelled.");
            _logger.LogInformation("Session {ThreadId} loop aborted.", session.ThreadId);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            session.State = SessionState.Failed;
            session.LastError = ex.Message;
            _sessions.Update(session);
            await SafePostAsync(session.ThreadId, $"**Failed** — {ex.Message}");
            _logger.LogError(ex, "Session {ThreadId} loop failed.", session.ThreadId);
        }
        finally
        {
            if (_running.TryRemove(session.ThreadId, out var cts))
                cts.Dispose();
        }
    }

    private async Task EnterGateAsync(Session session, string plan, GateReason reason, CriticVerdict verdict)
    {
        session.State = SessionState.AwaitingGate;
        session.GateReason = reason;
        session.PlanSnapshot = plan;
        session.OpenIssues = reason == GateReason.Approved
            ? null
            : JsonSerializer.Serialize(verdict.OpenIssues);
        session.GateExpiresAt = DateTimeOffset.UtcNow
            .AddSeconds(_options.GateCountdownSeconds)
            .ToUnixTimeMilliseconds();
        _sessions.Update(session);

        var headline = reason switch
        {
            GateReason.Approved => "the critic approved this plan.",
            GateReason.RoundCap => $"forced forward after {_options.MaxPlanRounds} rounds — {verdict.OpenIssues.Count} open issue(s).",
            GateReason.Stalled => $"the loop stalled — {verdict.OpenIssues.Count} open issue(s) remain.",
            _ => "plan converged.",
        };
        var minutes = Math.Max(1, _options.GateCountdownSeconds / 60);

        await _threads.PostAsync(session.ThreadId,
            $"**Gate** — {headline}\n\nReact 🛑 (or run `/abort`) within ~{minutes} min to stop it. "
            + "Otherwise it proceeds automatically. The plan above is now frozen as the snapshot.");
        _logger.LogInformation("Session {ThreadId} reached the gate ({Reason}).", session.ThreadId, reason);
    }

    private async Task<string> CollectSteeringAsync(string threadId, DateTimeOffset since)
    {
        try
        {
            return await _threads.CollectOperatorMessagesAsync(threadId, since);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not collect steering for {ThreadId}.", threadId);
            return "";
        }
    }

    private async Task SafePostAsync(string threadId, string text)
    {
        try
        {
            await _threads.PostAsync(threadId, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not post to thread {ThreadId}.", threadId);
        }
    }

    private static string FormatVerdict(int round, CriticVerdict verdict)
    {
        var lines = new List<string> { $"**Critic — round {round} (GPT)**", "", verdict.Assessment };
        if (verdict.Issues.Count > 0)
        {
            lines.Add("");
            foreach (var issue in verdict.Issues)
                lines.Add($"- **[{issue.Severity}]** {issue.Summary}");
        }
        lines.Add("");
        lines.Add(verdict.Approved
            ? "_No blocking or major issues._"
            : $"_{verdict.OpenIssues.Count} open issue(s)._");
        return string.Join("\n", lines);
    }
}
