using Discord;
using Discord.WebSocket;
using Ember.Sessions;

namespace Ember.Discord.Interactions;

/// <summary><c>/status</c> — reports the state of the session for the current thread.</summary>
public sealed class StatusCommand : ISlashCommand
{
    private readonly SessionStore _sessions;

    public StatusCommand(SessionStore sessions) => _sessions = sessions;

    public string Name => "status";

    public SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(Name)
        .WithDescription("Show the planning session for this thread")
        .Build();

    public async Task HandleAsync(SocketSlashCommand command)
    {
        var session = _sessions.Get(command.Channel.Id.ToString());
        if (session is null)
        {
            await command.RespondAsync("No planning session is attached to this channel. Run `/plan` in a text channel.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("Planning session")
            .WithColor(Color.Blue)
            .AddField("State", session.State.ToString(), inline: true)
            .AddField("Round", session.CurrentRound.ToString(), inline: true)
            .AddField("Repo", session.Repo, inline: true)
            .AddField("Brief", Truncate(session.Brief, 1000));

        if (session.BranchName is { } branch)
            embed.AddField("Branch", branch, inline: true);
        if (session.WorktreePath is { } worktree)
            embed.AddField("Worktree", Truncate(worktree, 1000));
        if (session.PrUrl is { } prUrl)
            embed.AddField("PR", prUrl);
        if (session.LastError is { } error)
            embed.AddField("Last error", Truncate(error, 1000));

        if (session.GateReason is { } gateReason)
            embed.WithFooter($"Gate: {gateReason}");

        await command.RespondAsync(embed: embed.Build(), ephemeral: true);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
