using Discord;
using Discord.WebSocket;
using Ember.Config;
using Ember.Overnight;
using Microsoft.Extensions.Options;

namespace Ember.Discord.Interactions;

/// <summary><c>/brief</c> — runs the overnight backlog planner now instead of waiting for the schedule.</summary>
public sealed class BriefCommand : ISlashCommand
{
    private readonly OvernightExecutor _executor;
    private readonly EmberOptions _options;
    private readonly ILogger<BriefCommand> _logger;

    public BriefCommand(
        OvernightExecutor executor,
        IOptions<EmberOptions> options,
        ILogger<BriefCommand> logger)
    {
        _executor = executor;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "brief";

    public SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(Name)
        .WithDescription("Run the overnight backlog planner now (morning brief + safe reconciliation)")
        .Build();

    public async Task HandleAsync(SocketSlashCommand command)
    {
        if (!_options.Overnight.Enabled)
        {
            await command.RespondAsync(
                "Overnight planner is disabled — set `Ember:Overnight:Enabled` and `Ember:Overnight:ChannelId`, then restart.",
                ephemeral: true);
            return;
        }

        await command.RespondAsync(
            "Overnight run started — the brief thread will appear in the brief channel.",
            ephemeral: true);

        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await _executor.ExecuteAsync(CancellationToken.None);
                _logger.LogInformation("/brief finished: {Summary}", summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "/brief run failed.");
            }
        });
    }
}
