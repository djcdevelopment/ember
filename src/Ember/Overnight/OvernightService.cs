using Ember.Config;
using Ember.Reflect;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>
/// The overnight scheduler: fires one run per local calendar day at the configured time. Disabled
/// by default, and manual-only by default (<c>ScheduleEnabled=false</c>) — the daily driver is the
/// <c>Start-Plan</c> launcher / <c>/brief</c> (ADR 17/19), not an unattended GPU run. Reuses
/// <see cref="ReflectService"/>'s schedule predicates so both subsystems behave identically.
/// </summary>
public sealed class OvernightService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly OvernightExecutor _executor;
    private readonly BriefStore _store;
    private readonly OvernightOptions _options;
    private readonly ILogger<OvernightService> _logger;

    public OvernightService(
        OvernightExecutor executor,
        BriefStore store,
        IOptions<EmberOptions> options,
        ILogger<OvernightService> logger)
    {
        _executor = executor;
        _store = store;
        _options = options.Value.Overnight;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Overnight planner is disabled (Ember:Overnight:Enabled=false); scheduler idle.");
            return;
        }

        if (ReflectService.ScheduleDisabled(_options.ScheduleEnabled, _options.RunAtLocalTime))
        {
            _logger.LogInformation(
                "Overnight schedule disabled (Ember:Overnight:ScheduleEnabled=false); manual-only — "
                + "use /brief or the Start-Plan launcher. The nightly auto-run will not fire.");
            return;
        }

        if (!TimeOnly.TryParse(_options.RunAtLocalTime, out var runAt))
        {
            _logger.LogWarning(
                "Ember:Overnight:RunAtLocalTime '{Value}' is not a valid HH:mm time; defaulting to 06:00.",
                _options.RunAtLocalTime);
            runAt = new TimeOnly(6, 0);
        }
        _logger.LogInformation("Overnight scheduler armed for {RunAt} local daily.", runAt);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ReflectService.IsDue(DateTime.Now, runAt, _store.LatestRunDate()))
                {
                    _logger.LogInformation("Overnight run due; starting.");
                    var summary = await _executor.ExecuteAsync(stoppingToken);
                    _logger.LogInformation("Overnight run finished: {Summary}", summary);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overnight scheduler tick failed.");
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
}
