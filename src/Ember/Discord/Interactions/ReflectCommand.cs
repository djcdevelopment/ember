using Discord;
using Discord.WebSocket;
using Ember.Config;
using Ember.Reflect;
using Microsoft.Extensions.Options;

namespace Ember.Discord.Interactions;

/// <summary><c>/reflect</c> — runs the constellation recap now instead of waiting for the schedule.</summary>
public sealed class ReflectCommand : ISlashCommand
{
    private readonly ReflectExecutor _executor;
    private readonly EmberOptions _options;
    private readonly ILogger<ReflectCommand> _logger;

    public ReflectCommand(
        ReflectExecutor executor,
        IOptions<EmberOptions> options,
        ILogger<ReflectCommand> logger)
    {
        _executor = executor;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "reflect";

    public SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(Name)
        .WithDescription("Run the constellation recap now")
        .Build();

    public async Task HandleAsync(SocketSlashCommand command)
    {
        if (!_options.Reflect.Enabled)
        {
            await command.RespondAsync(
                "Reflect is disabled — set `Ember:Reflect:Enabled` and `Ember:Reflect:ChannelId`, then restart.",
                ephemeral: true);
            return;
        }

        await command.RespondAsync(
            "Reflect run started — the recap thread will appear in the reflect channel.",
            ephemeral: true);

        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await _executor.ExecuteAsync(CancellationToken.None);
                _logger.LogInformation("/reflect finished: {Summary}", summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "/reflect run failed.");
            }
        });
    }
}
