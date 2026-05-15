using Discord;
using Discord.WebSocket;
using Ember.Config;
using Ember.Loop;
using Ember.Sessions;
using Microsoft.Extensions.Options;

namespace Ember.Discord.Interactions;

/// <summary><c>/plan</c> — opens a planning session in a new thread and starts the loop.</summary>
public sealed class PlanCommand : ISlashCommand
{
    private readonly SessionStore _sessions;
    private readonly PlanningLoopRunner _loop;
    private readonly EmberOptions _options;
    private readonly ILogger<PlanCommand> _logger;

    public PlanCommand(
        SessionStore sessions,
        PlanningLoopRunner loop,
        IOptions<EmberOptions> options,
        ILogger<PlanCommand> logger)
    {
        _sessions = sessions;
        _loop = loop;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "plan";

    public SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(Name)
        .WithDescription("Start a planning session for a brief")
        .AddOption("brief", ApplicationCommandOptionType.String, "What you want planned and built", isRequired: true)
        .AddOption("repo", ApplicationCommandOptionType.String, "Target repo (allowlist key)", isRequired: true)
        .Build();

    public async Task HandleAsync(SocketSlashCommand command)
    {
        var brief = (string)command.Data.Options.First(o => o.Name == "brief").Value;
        var repo = (string)command.Data.Options.First(o => o.Name == "repo").Value;

        if (!_options.Repos.ContainsKey(repo))
        {
            var known = _options.Repos.Count == 0 ? "(none configured)" : string.Join(", ", _options.Repos.Keys);
            await command.RespondAsync($"Unknown repo `{repo}`. Allowlisted: {known}", ephemeral: true);
            return;
        }

        if (command.Channel is IThreadChannel)
        {
            await command.RespondAsync("Run `/plan` in a channel, not inside an existing thread.", ephemeral: true);
            return;
        }

        if (command.Channel is not ITextChannel textChannel)
        {
            await command.RespondAsync("Run `/plan` in a server text channel — ember opens a thread for the session.", ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);

        var thread = await ThreadHelpers.CreateSessionThreadAsync(textChannel, brief);

        var session = new Session
        {
            ThreadId = thread.Id.ToString(),
            State = SessionState.Planning,
            Repo = repo,
            Brief = brief,
        };
        _sessions.Create(session);
        _logger.LogInformation("Session {ThreadId} created (repo {Repo}); starting planning loop.", session.ThreadId, repo);

        await thread.SendMessageAsync($"**New planning session**\nRepo: `{repo}`\nBrief: {brief}");
        _loop.Start(session);

        await command.FollowupAsync($"Session opened: {MentionUtils.MentionChannel(thread.Id)}", ephemeral: true);
    }
}
