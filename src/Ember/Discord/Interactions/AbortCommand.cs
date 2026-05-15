using Discord;
using Discord.WebSocket;
using Ember.Build;
using Ember.Loop;
using Ember.Sessions;

namespace Ember.Discord.Interactions;

/// <summary>
/// <c>/abort</c> — cancels the session for the current thread. A running loop is
/// cancelled via its token; a running build has its process killed; a session at the
/// gate or queued is marked aborted directly.
/// </summary>
public sealed class AbortCommand : ISlashCommand
{
    private readonly SessionStore _sessions;
    private readonly PlanningLoopRunner _loop;
    private readonly BuildQueue _builds;
    private readonly ILogger<AbortCommand> _logger;

    public AbortCommand(
        SessionStore sessions, PlanningLoopRunner loop, BuildQueue builds, ILogger<AbortCommand> logger)
    {
        _sessions = sessions;
        _loop = loop;
        _builds = builds;
        _logger = logger;
    }

    public string Name => "abort";

    public SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(Name)
        .WithDescription("Abort the planning session for this thread")
        .Build();

    public async Task HandleAsync(SocketSlashCommand command)
    {
        var session = _sessions.Get(command.Channel.Id.ToString());
        if (session is null)
        {
            await command.RespondAsync("No planning session is attached to this channel.", ephemeral: true);
            return;
        }

        string reply;
        switch (session.State)
        {
            case SessionState.Planning:
                // The loop owns the session row while running — cancel it and let the
                // loop record the Aborted state itself.
                if (_loop.Cancel(session.ThreadId))
                {
                    reply = "Aborting — stopping the planning loop.";
                }
                else
                {
                    session.State = SessionState.Aborted;
                    _sessions.Update(session);
                    reply = "Session aborted.";
                }
                break;

            case SessionState.AwaitingGate:
                session.State = SessionState.Aborted;
                _sessions.Update(session);
                reply = "Session aborted at the gate — it will not proceed.";
                break;

            case SessionState.Building:
                // The build queue owns the session row once it is in flight — let it record
                // the Aborted state (and keep the worktree) itself.
                reply = _builds.TryCancel(session.ThreadId) switch
                {
                    CancelResult.CancellingRunning => "Aborting — stopping the builder. The worktree is kept.",
                    CancelResult.CancelledQueued => "Aborted — the build was removed from the queue.",
                    _ => MarkAbortedFallback(session),
                };
                break;

            case SessionState.PrOpen:
                reply = session.PrUrl is { } url
                    ? $"Session already finished — PR: {url}"
                    : "Session already finished.";
                break;

            default: // Aborted or Failed
                reply = $"Session is already {session.State} — nothing to abort.";
                break;
        }

        _logger.LogInformation("/abort handled for session {ThreadId}.", session.ThreadId);
        await command.RespondAsync(reply, ephemeral: true);
    }

    /// <summary>
    /// Backstop for a <c>Building</c> session the queue does not recognise (e.g. an orphan
    /// left by a restart) — mark it aborted directly so the operator is not stuck.
    /// </summary>
    private string MarkAbortedFallback(Session session)
    {
        session.State = SessionState.Aborted;
        _sessions.Update(session);
        return "Session marked aborted.";
    }
}
