using Discord;
using Discord.WebSocket;
using Ember.Loop;
using Ember.Sessions;

namespace Ember.Discord.Interactions;

/// <summary>
/// <c>/abort</c> — cancels the session for the current thread. A running loop is
/// cancelled via its token; a session already at the gate is marked aborted directly.
/// </summary>
public sealed class AbortCommand : ISlashCommand
{
    private readonly SessionStore _sessions;
    private readonly PlanningLoopRunner _loop;
    private readonly ILogger<AbortCommand> _logger;

    public AbortCommand(SessionStore sessions, PlanningLoopRunner loop, ILogger<AbortCommand> logger)
    {
        _sessions = sessions;
        _loop = loop;
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
                session.State = SessionState.Aborted;
                _sessions.Update(session);
                reply = "Session aborted.";
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
}
