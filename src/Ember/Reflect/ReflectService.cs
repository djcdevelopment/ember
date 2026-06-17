using Ember.Config;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>
/// The reflect scheduler: fires one run per local calendar day at the configured time.
/// Disabled by default. A run of any status (including Failed) claims the day — failures
/// surface in logs and the recaps table rather than retry-storming a broken endpoint;
/// <c>/reflect</c> remains the manual override.
/// </summary>
public sealed class ReflectService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly ReflectExecutor _executor;
    private readonly RecapStore _store;
    private readonly EmberOptions _options;
    private readonly ILogger<ReflectService> _logger;

    public ReflectService(
        ReflectExecutor executor,
        RecapStore store,
        IOptions<EmberOptions> options,
        ILogger<ReflectService> logger)
    {
        _executor = executor;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Reflect.Enabled)
        {
            _logger.LogInformation("Reflect is disabled (Ember:Reflect:Enabled=false); scheduler idle.");
            return;
        }

        if (ScheduleDisabled(_options.Reflect.ScheduleEnabled, _options.Reflect.RunAtLocalTime))
        {
            _logger.LogInformation(
                "Reflect schedule disabled (Ember:Reflect:ScheduleEnabled=false); manual-only — "
                + "use /reflect or the desktop launcher. The nightly auto-run will not fire.");
            return;
        }

        if (!TimeOnly.TryParse(_options.Reflect.RunAtLocalTime, out var runAt))
        {
            _logger.LogWarning(
                "Ember:Reflect:RunAtLocalTime '{Value}' is not a valid HH:mm time; defaulting to 03:00.",
                _options.Reflect.RunAtLocalTime);
            runAt = new TimeOnly(3, 0);
        }
        _logger.LogInformation("Reflect scheduler armed for {RunAt} local daily.", runAt);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsDue(DateTime.Now, runAt, _store.LatestRunDate()))
                {
                    _logger.LogInformation("Reflect run due; starting.");
                    var summary = await _executor.ExecuteAsync(stoppingToken);
                    _logger.LogInformation("Reflect run finished: {Summary}", summary);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reflect scheduler tick failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Whether the scheduler stays idle. Manual-only (<c>ScheduleEnabled=false</c>) or a
    /// blank/whitespace run time both mean "no nightly auto-run": Reflect stays enabled for the
    /// manual <c>/reflect</c> command and the launcher trigger, but nothing fires unattended. On
    /// this single-operator rig the operator brings the inference substrate up deliberately
    /// (ADR 17), so an unattended 03:00 run that competes for the GPUs is opt-in, not default.
    /// </summary>
    public static bool ScheduleDisabled(bool scheduleEnabled, string? runAtLocalTime) =>
        !scheduleEnabled || string.IsNullOrWhiteSpace(runAtLocalTime);

    /// <summary>
    /// Due when the local clock has passed today's run time and no run (any status) has
    /// happened today. Self-heals after downtime: a missed 03:00 fires on the next boot.
    /// </summary>
    public static bool IsDue(DateTime nowLocal, TimeOnly runAt, string? latestRunDate) =>
        TimeOnly.FromDateTime(nowLocal) >= runAt
        && latestRunDate != nowLocal.ToString("yyyy-MM-dd");
}
