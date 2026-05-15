using System.Threading.Channels;
using Ember.Sessions;

namespace Ember.Build;

/// <summary>Result of a <see cref="BuildQueue.TryCancel"/> request.</summary>
public enum CancelResult
{
    /// <summary>No build for that thread was running or queued.</summary>
    NotFound,

    /// <summary>The build was in flight — its process is being killed.</summary>
    CancellingRunning,

    /// <summary>The build was waiting in the queue — it was marked aborted and will be skipped.</summary>
    CancelledQueued,
}

/// <summary>
/// Global FIFO build queue — a deliberate safety throttle: exactly one build runs at a time
/// across all repos, so two builders can never contend for the host or API limits. It also
/// owns the cancellation token of the in-flight build for <c>/abort</c>.
/// </summary>
public sealed class BuildQueue : BackgroundService
{
    private readonly Channel<string> _queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly BuilderRunner _runner;
    private readonly SessionStore _sessions;
    private readonly ILogger<BuildQueue> _logger;

    private readonly object _gate = new();
    private string? _currentThreadId;
    private CancellationTokenSource? _currentCts;

    public BuildQueue(BuilderRunner runner, SessionStore sessions, ILogger<BuildQueue> logger)
    {
        _runner = runner;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>Enqueues a session (already in <c>Building</c>) for the builder.</summary>
    public void Enqueue(string threadId)
    {
        if (!_queue.Writer.TryWrite(threadId))
            _logger.LogError("Could not enqueue build for session {ThreadId}.", threadId);
    }

    /// <summary>
    /// Cancels a build. If it is the in-flight build, its process is killed. If it is still
    /// queued (or an orphan), the session is marked aborted so the worker skips it.
    /// </summary>
    public CancelResult TryCancel(string threadId)
    {
        lock (_gate)
        {
            if (_currentThreadId == threadId && _currentCts is not null)
            {
                _currentCts.Cancel();
                return CancelResult.CancellingRunning;
            }
        }

        // Not the running build. If it is a queued Building session, mark it aborted now;
        // the worker re-checks state on dequeue and skips it.
        var session = _sessions.Get(threadId);
        if (session is { State: SessionState.Building })
        {
            session.State = SessionState.Aborted;
            _sessions.Update(session);
            return CancelResult.CancelledQueued;
        }

        return CancelResult.NotFound;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var threadId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(threadId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Build queue item {ThreadId} faulted.", threadId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on host shutdown.
        }
    }

    private async Task ProcessAsync(string threadId, CancellationToken stoppingToken)
    {
        var session = _sessions.Get(threadId);
        if (session is not { State: SessionState.Building })
        {
            // Aborted while queued, or the row vanished — nothing to build.
            _logger.LogInformation(
                "Skipping queued build {ThreadId} — state is {State}.",
                threadId, session?.State.ToString() ?? "missing");
            return;
        }

        // A linked CTS: cancelled either by /abort (via TryCancel) or host shutdown.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _currentThreadId = threadId;
            _currentCts = cts;
        }

        try
        {
            // Re-read after claiming to close the race with a concurrent /abort.
            session = _sessions.Get(threadId);
            if (session is not { State: SessionState.Building })
                return;

            await _runner.RunAsync(session, cts.Token);
        }
        finally
        {
            lock (_gate)
            {
                _currentThreadId = null;
                _currentCts = null;
            }
        }
    }
}
