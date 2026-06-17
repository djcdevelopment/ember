using System.Diagnostics;
using System.Net;
using Discord;
using Discord.WebSocket;
using Ember.Config;
using Ember.Observability;
using Ember.Overnight;
using Ember.Reflect;
using Ember.Sessions;
using Microsoft.Extensions.Options;

namespace Ember.Discord;

/// <summary>
/// Hosts the Discord gateway connection: logs in, registers slash commands to the
/// configured guild, enforces the owner check, dispatches commands, and handles the
/// gate abort-reaction.
/// </summary>
public sealed class DiscordBotService : BackgroundService
{
    private const string AbortEmoji = "🛑";

    private readonly DiscordSocketClient _client;
    private readonly IReadOnlyDictionary<string, ISlashCommand> _commands;
    private readonly SessionStore _sessions;
    private readonly RecapStore _recaps;
    private readonly BriefStore _briefs;
    private readonly ThreadGateway _threads;
    private readonly DiscordOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DiscordBotService> _logger;

    private ulong _guildId;
    private ulong _ownerId;
    private int _fatalAuthSignaled;

    public DiscordBotService(
        DiscordSocketClient client,
        IEnumerable<ISlashCommand> commands,
        SessionStore sessions,
        RecapStore recaps,
        BriefStore briefs,
        ThreadGateway threads,
        IOptions<DiscordOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<DiscordBotService> logger)
    {
        _client = client;
        _commands = commands.ToDictionary(c => c.Name);
        _sessions = sessions;
        _recaps = recaps;
        _briefs = briefs;
        _threads = threads;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;

        _client.Log += OnClientLog;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.ReactionAdded += OnReactionAddedAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ValidateConfig())
        {
            _logger.LogError("ember cannot start until the configuration above is set. Shutting down.");
            _lifetime.StopApplication();
            return;
        }

        await _client.LoginAsync(TokenType.Bot, _options.BotToken);
        await _client.StartAsync();
        _logger.LogInformation("Discord client started; {Count} commands defined.", _commands.Count);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.LogoutAsync();
        await _client.StopAsync();
        await base.StopAsync(cancellationToken);
    }

    private bool ValidateConfig()
    {
        var ok = true;

        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            _logger.LogError("Discord:BotToken is not set. Set it with: dotnet user-secrets set \"Discord:BotToken\" <token>");
            ok = false;
        }
        else if (!LooksLikeBotToken(_options.BotToken))
        {
            _logger.LogWarning(
                "Discord:BotToken does not look like a bot token (expect ~70+ characters with two '.' separators). "
                + "Confirm it is the Bot token from the Discord developer portal — not the application id, client secret, or public key.");
        }

        if (!ulong.TryParse(_options.GuildId, out _guildId))
        {
            _logger.LogError("Discord:GuildId is missing or not a numeric id.");
            ok = false;
        }

        if (!ulong.TryParse(_options.OwnerUserId, out _ownerId))
        {
            _logger.LogError("Discord:OwnerUserId is missing or not a numeric id.");
            ok = false;
        }

        return ok;
    }

    private static bool LooksLikeBotToken(string token) =>
        token.Length >= 50
        && token.Count(c => c == '.') == 2
        && !token.Any(char.IsWhiteSpace);

    private async Task OnReadyAsync()
    {
        try
        {
            var guild = _client.GetGuild(_guildId);
            if (guild is null)
            {
                var joined = _client.Guilds.Count == 0
                    ? "none — the bot has not been invited to any server"
                    : string.Join("; ", _client.Guilds.Select(g => $"{g.Name} ({g.Id})"));
                _logger.LogError(
                    "Configured Discord:GuildId {GuildId} not found. The bot is currently a member of: {JoinedGuilds}. "
                    + "Invite the bot to that server, or set Discord:GuildId to one of those ids, then restart.",
                    _guildId, joined);
                return;
            }

            var payload = _commands.Values
                .Select(c => (ApplicationCommandProperties)c.Build())
                .ToArray();

            await guild.BulkOverwriteApplicationCommandAsync(payload);
            _logger.LogInformation("Registered {Count} slash commands to guild {GuildId}.", payload.Length, _guildId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands.");
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        using var activity = Telemetry.Activity.StartActivity($"command /{command.Data.Name}");

        if (command.User.Id != _ownerId)
        {
            await command.RespondAsync("This bot only accepts commands from its owner.", ephemeral: true);
            return;
        }

        if (!_commands.TryGetValue(command.Data.Name, out var handler))
        {
            await command.RespondAsync("Unknown command.", ephemeral: true);
            return;
        }

        try
        {
            await handler.HandleAsync(command);
            Telemetry.CommandsHandled.Add(1, new KeyValuePair<string, object?>("command", command.Data.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}.", command.Data.Name);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await RespondWithErrorAsync(command);
        }
    }

    private async Task OnReactionAddedAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction)
    {
        if (reaction.UserId != _ownerId)
            return;

        if (reaction.Emote.Name == AbortEmoji)
        {
            var session = _sessions.Get(channel.Id.ToString());
            if (session is null || session.State != SessionState.AwaitingGate)
                return;

            session.State = SessionState.Aborted;
            _sessions.Update(session);
            await _threads.PostAsync(session.ThreadId,
                "**Aborted at the gate** — 🛑 received. The plan will not proceed.");
            _logger.LogInformation("Session {ThreadId} aborted via gate reaction.", session.ThreadId);
            return;
        }

        // Recap labels — the operator's verdict on a reflect post, keyed by the
        // label-request message id. Trim the emoji variation selector (U+FE0F) so
        // ✏️ matches however the client reports it.
        var label = reaction.Emote.Name.TrimEnd('️') switch
        {
            "✅" => RecapLabels.Accurate,
            "✏" => RecapLabels.Partial,
            "❌" => RecapLabels.Wrong,
            _ => null,
        };
        if (label is null)
            return;

        // The same ✅/✏️/❌ verdict applies to both reflect recaps and overnight briefs; the label
        // message id disambiguates which store owns it.
        var messageId = message.Id.ToString();
        if (_recaps.SetLabel(messageId, label))
            _logger.LogInformation("Recap labelled '{Label}' via reaction on message {MessageId}.", label, message.Id);
        else if (_briefs.SetLabel(messageId, label))
            _logger.LogInformation("Brief labelled '{Label}' via reaction on message {MessageId}.", label, message.Id);
    }

    private static async Task RespondWithErrorAsync(SocketSlashCommand command)
    {
        const string message = "Something went wrong handling that command. Check the logs.";
        try
        {
            if (command.HasResponded)
                await command.FollowupAsync(message, ephemeral: true);
            else
                await command.RespondAsync(message, ephemeral: true);
        }
        catch
        {
            // best effort — the interaction token may have expired
        }
    }

    private Task OnClientLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information,
        };
        _logger.Log(level, message.Exception, "[Discord] {Source}: {Message}", message.Source, message.Message);

        if (message.Exception is global::Discord.Net.HttpException { HttpCode: HttpStatusCode.Unauthorized }
            && Interlocked.Exchange(ref _fatalAuthSignaled, 1) == 0)
        {
            _logger.LogCritical(
                "Discord rejected the bot token (401 Unauthorized) — retrying cannot fix this. "
                + "Discord:BotToken is not a valid bot token: wrong value, stray spaces/quotes, or the token was reset. "
                + "Correct it and restart — dotnet user-secrets set \"Discord:BotToken\" <token>");
            _lifetime.StopApplication();
        }

        return Task.CompletedTask;
    }
}
