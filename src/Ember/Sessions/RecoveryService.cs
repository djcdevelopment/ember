using Discord.WebSocket;
using Ember.Build;
using Ember.Config;
using Ember.Discord;
using Microsoft.Extensions.Options;

namespace Ember.Sessions;

/// <summary>
/// Boot recovery. A process restart kills the in-memory planning loops and the build queue,
/// so any session left in an <em>active</em> state is stale. This service marks interrupted
/// <c>PLANNING</c> / <c>BUILDING</c> sessions <c>FAILED</c>, cleans their orphaned worktrees,
/// and applies the worktree retention policy.
/// </summary>
/// <remarks>
/// Registered as the first hosted service so its synchronous <see cref="StartAsync"/> pass
/// completes before <c>GateService</c> or <c>BuildQueue</c> start — a session can therefore
/// never be both "stale at boot" and "freshly transitioned to BUILDING" at the same time.
/// </remarks>
public sealed class RecoveryService : IHostedService
{
    private static readonly TimeSpan DiscordReadyWait = TimeSpan.FromSeconds(30);

    private readonly SessionStore _sessions;
    private readonly ThreadGateway _threads;
    private readonly DiscordSocketClient _client;
    private readonly EmberOptions _options;
    private readonly ILogger<RecoveryService> _logger;

    private readonly TaskCompletionSource _discordReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecoveryService(
        SessionStore sessions,
        ThreadGateway threads,
        DiscordSocketClient client,
        IOptions<EmberOptions> options,
        ILogger<RecoveryService> logger)
    {
        _sessions = sessions;
        _threads = threads;
        _client = client;
        _options = options.Value;
        _logger = logger;

        // Subscribe before the gateway connects so the Ready signal is never missed.
        _client.Ready += OnDiscordReady;
    }

    private Task OnDiscordReady()
    {
        _discordReady.TrySetResult();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Critical, ordered, and fast: flip stale active sessions to FAILED in the database
        // before any other hosted service starts. Worktree cleanup and thread notifications
        // are not ordering-sensitive, so they run in the background.
        var recovered = RecoverInterruptedSessions();
        _ = Task.Run(() => FinishRecoveryAsync(recovered), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Ready -= OnDiscordReady;
        return Task.CompletedTask;
    }

    private List<Recovered> RecoverInterruptedSessions()
    {
        var recovered = new List<Recovered>();

        foreach (var session in _sessions.GetByState(SessionState.Planning))
        {
            session.State = SessionState.Failed;
            session.LastError = "ember restarted while this session was planning.";
            _sessions.Update(session);
            recovered.Add(new Recovered(session,
                "**ember restarted during planning.** This session was interrupted and is "
                + "marked failed — run `/plan` again to retry."));
            _logger.LogInformation("Recovery: session {ThreadId} PLANNING -> FAILED.", session.ThreadId);
        }

        foreach (var session in _sessions.GetByState(SessionState.Building))
        {
            var branch = session.BranchName;
            var worktree = session.WorktreePath;
            session.State = SessionState.Failed;
            session.LastError = "ember restarted while this session was building.";
            _sessions.Update(session);

            var branchNote = branch is null
                ? ""
                : $" Any commits the builder made are kept on branch `{branch}`; its worktree was cleaned up.";
            recovered.Add(new Recovered(session,
                $"**ember restarted during the build.** This session is marked failed — run "
                + $"`/plan` again to retry.{branchNote}",
                worktree));
            _logger.LogInformation("Recovery: session {ThreadId} BUILDING -> FAILED.", session.ThreadId);
        }

        return recovered;
    }

    private async Task FinishRecoveryAsync(List<Recovered> recovered)
    {
        try
        {
            foreach (var item in recovered)
            {
                if (item.OrphanedWorktree is { } path)
                    await RemoveWorktreeAsync(item.Session, path);
            }

            await CleanupExpiredWorktreesAsync();

            // Best-effort: wait for the gateway so the restart notices actually land.
            await Task.WhenAny(_discordReady.Task, Task.Delay(DiscordReadyWait));
            foreach (var item in recovered)
                await SafePostAsync(item.Session.ThreadId, item.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background recovery pass failed.");
        }
    }

    /// <summary>Removes worktrees of long-finished FAILED / ABORTED sessions per the retention policy.</summary>
    private async Task CleanupExpiredWorktreesAsync()
    {
        if (_options.WorktreeRetentionDays <= 0)
            return;

        var cutoff = DateTimeOffset.UtcNow
            .AddDays(-_options.WorktreeRetentionDays)
            .ToUnixTimeMilliseconds();

        foreach (var state in new[] { SessionState.Failed, SessionState.Aborted })
        {
            foreach (var session in _sessions.GetByState(state))
            {
                if (session.WorktreePath is { } path && session.UpdatedAt < cutoff)
                {
                    _logger.LogInformation(
                        "Retention cleanup: removing worktree for {State} session {ThreadId}.",
                        state, session.ThreadId);
                    await RemoveWorktreeAsync(session, path);
                }
            }
        }
    }

    private async Task RemoveWorktreeAsync(Session session, string worktreePath)
    {
        try
        {
            var repoPath = _options.Repos.TryGetValue(session.Repo, out var p) ? p : session.Repo;
            await Worktree.RemoveAsync(repoPath, worktreePath, CancellationToken.None);
            session.WorktreePath = null;
            _sessions.Update(session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove worktree {Path} for session {ThreadId}.",
                worktreePath, session.ThreadId);
        }
    }

    private async Task SafePostAsync(string threadId, string message)
    {
        try
        {
            await _threads.PostAsync(threadId, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not post recovery notice to thread {ThreadId}.", threadId);
        }
    }

    private sealed record Recovered(Session Session, string Message, string? OrphanedWorktree = null);
}
