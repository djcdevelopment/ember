using Discord;
using Discord.WebSocket;

namespace Ember.Discord;

/// <summary>A slash command ember exposes. One implementation per command.</summary>
public interface ISlashCommand
{
    /// <summary>The command name, without the leading slash.</summary>
    string Name { get; }

    /// <summary>Builds the registration payload for this command.</summary>
    SlashCommandProperties Build();

    /// <summary>Handles an invocation. The caller has already enforced the owner check.</summary>
    Task HandleAsync(SocketSlashCommand command);
}
