using Ember.Build;
using Ember.Config;
using Ember.Discord;
using Ember.Observability;
using Ember.Sessions;
using Microsoft.Extensions.Options;

namespace Ember.Gate;

/// <summary>
/// Drives the soft gate: a steady poller fires gates whose countdown has elapsed, and a
/// boot pass re-arms any gate that expired while ember was down (so it is never auto-fired
/// without a live veto window).
/// </summary>
public sealed class GateService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly SessionStore _sessions;
    private readonly ThreadGateway _threads;
    private readonly BuildQueue _builds;
    private readonly EmberOptions _options;
    private readonly ILogger<GateService> _logger;

    public GateService(
        SessionStore sessions,
        ThreadGateway threads,
        BuildQueue builds,
        IOptions<EmberOptions> options,
        ILogger<GateService> logger)
    {
        _sessions = sessions;
        _threads = threads;
        _builds = builds;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnBootAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gate poll failed.");
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

    private async Task PollAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var session in _sessions.GetByState(SessionState.AwaitingGate))
        {
            if (session.GateExpiresAt is { } expiry && expiry <= now)
                await FireGateAsync(session);
        }
    }

    private async Task FireGateAsync(Session session)
    {
        using var activity = Telemetry.Activity.StartActivity("gate.fire");
        activity?.SetTag("ember.gate_reason", session.GateReason?.ToString());

        // Transition to Building, then hand off to the FIFO build queue. The queue worker
        // re-reads the row before running, so this ordering is safe across a restart.
        session.State = SessionState.Building;
        _sessions.Update(session);
        _builds.Enqueue(session.ThreadId);

        await _threads.PostAsync(session.ThreadId,
            "**Gate elapsed — plan accepted.** Queued for the builder; "
            + "the build starts when the queue is free. Run `/abort` to stop it.");
        _logger.LogInformation("Gate fired for session {ThreadId}; queued for build.", session.ThreadId);
    }

    private async Task ReconcileOnBootAsync()
    {
        var bootTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var session in _sessions.GetByState(SessionState.AwaitingGate))
        {
            if (session.GateExpiresAt is not { } expiry || expiry > bootTime)
                continue;

            // The countdown elapsed while ember was down — do not auto-fire.
            // Re-arm a fresh window so the operator gets a live chance to veto.
            session.GateExpiresAt = DateTimeOffset.UtcNow
                .AddSeconds(_options.GateCountdownSeconds)
                .ToUnixTimeMilliseconds();
            _sessions.Update(session);

            var minutes = Math.Max(1, _options.GateCountdownSeconds / 60);
            await _threads.PostAsync(session.ThreadId,
                "**ember restarted while this plan was at the gate.** The countdown was re-armed "
                + $"(~{minutes} min) — react 🛑 or run `/abort` to stop it.");
            _logger.LogInformation("Re-armed gate for session {ThreadId} after restart.", session.ThreadId);
        }
    }
}
