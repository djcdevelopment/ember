using System.Net;
using System.Net.Sockets;
using System.Text;
using Discord;
using Discord.WebSocket;
using Ember.Config;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>
/// A loopback-only "run now" trigger so the desktop launcher can start a recap without Discord.
/// Binds <c>http://127.0.0.1:{LocalTriggerPort}/</c> — never a routable interface — and only when
/// a port is configured (0 = off, the default). It starts the exact same run as <c>/reflect</c>
/// (ADR 17): the long-running bot still owns the Discord post and the reaction/label gate, so the
/// launcher must ensure this process is up and connected before poking it.
///
/// <code>
///   GET  /ready    -> 200 "ready" when a run can start now, else 503 with a one-line reason.
///   POST /reflect  -> runs SYNCHRONOUSLY and returns 200 + the run summary when the judges
///                     finish (so the launcher can free the GPUs only after), 409 if a run is
///                     already in progress, or 503 if not ready / disabled.
/// </code>
///
/// Both gate on <see cref="ReflectOptions.Enabled"/> and a live Discord connection so the launcher
/// can poll <c>/ready</c> until a freshly-started bot has finished connecting. Raw TCP (not
/// <see cref="HttpListener"/>) keeps it off HTTP.sys, so a non-elevated process can bind loopback
/// without a urlacl reservation.
/// </summary>
public sealed class ReflectTriggerService : BackgroundService
{
    private readonly ReflectExecutor _executor;
    private readonly DiscordSocketClient _client;
    private readonly EmberOptions _options;
    private readonly ILogger<ReflectTriggerService> _logger;

    public ReflectTriggerService(
        ReflectExecutor executor,
        DiscordSocketClient client,
        IOptions<EmberOptions> options,
        ILogger<ReflectTriggerService> logger)
    {
        _executor = executor;
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _options.Reflect.LocalTriggerPort;
        if (port <= 0)
        {
            _logger.LogInformation("Reflect local trigger disabled (Ember:Reflect:LocalTriggerPort=0).");
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Reflect local trigger could not bind 127.0.0.1:{Port}; trigger unavailable.", port);
            return;
        }

        _logger.LogInformation("Reflect local trigger listening on http://127.0.0.1:{Port}/ (loopback only).", port);
        using var reg = stoppingToken.Register(listener.Stop);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reflect local trigger accept failed.");
                    continue;
                }

                // Handle off the accept loop so a minutes-long /reflect can't stall /ready.
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();

                // Read the request head with a short timeout, independent of the (possibly
                // minutes-long) run and the response write below.
                string firstLine;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                    var head = await ReadRequestHeadAsync(stream, readCts.Token);
                    firstLine = head.Length == 0 ? "" : head.Split('\n', 2)[0];
                }

                var (method, path) = ParseRequestLine(firstLine);
                var (ready, reason) = Decide(_options.Reflect.Enabled, _client.ConnectionState);

                int status;
                string body;
                if (path.Equals("/ready", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    (status, body) = ready ? (200, "ready") : (503, reason);
                }
                else if (path.Equals("/reflect", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    if (!ready)
                    {
                        (status, body) = (503, reason);
                    }
                    else
                    {
                        // Synchronous on purpose: the desktop launcher waits on this response so it
                        // frees the GPUs (vllama kill-all) only AFTER the judges are done. The run is
                        // bound to the service token, not the client — if the launcher window is
                        // closed mid-run, the recap still completes; only the auto-teardown is skipped.
                        var summary = await _executor.ExecuteAsync(ct);
                        (status, body) = RunInProgress(summary) ? (409, summary) : (200, summary);
                    }
                }
                else
                {
                    (status, body) = (404, "not found");
                }

                await WriteResponseAsync(stream, status, body, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reflect local trigger request failed.");
            }
        }
    }

    /// <summary>True when <see cref="ReflectExecutor.ExecuteAsync"/> declined because a run already holds the gate.</summary>
    public static bool RunInProgress(string summary) =>
        summary.Contains("already in progress", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a run can start now, plus a one-line reason for the launcher. A run needs Reflect
    /// enabled and a live Discord gateway (so the recap can post and the label gate can arm).
    /// </summary>
    public static (bool ready, string reason) Decide(bool enabled, ConnectionState state)
    {
        if (!enabled)
            return (false, "reflect disabled");
        if (state != ConnectionState.Connected)
            return (false, $"bot not ready (discord {state.ToString().ToLowerInvariant()})");
        return (true, "ready");
    }

    /// <summary>Pulls the method and (query-stripped) path out of an HTTP request line.</summary>
    public static (string method, string path) ParseRequestLine(string requestLine)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        var rawPath = parts.Length > 1 ? parts[1] : "/";
        var q = rawPath.IndexOf('?');
        var path = q >= 0 ? rawPath[..q] : rawPath;
        return (method, path);
    }

    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        // Read the request line + headers up to the blank line that ends them. Draining the
        // inbound bytes before we respond and close is what prevents a TCP RST: closing a socket
        // with unread data makes Windows send a reset the client reports as "connection forcibly
        // closed", even when a valid response was already written.
        var sb = new StringBuilder();
        var buf = new byte[512];
        while (sb.Length < 16384)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n == 0)
                break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            var s = sb.ToString();
            if (s.Contains("\r\n\r\n") || s.Contains("\n\n"))
                break;
        }
        return sb.ToString();
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, CancellationToken ct)
    {
        var phrase = status switch
        {
            200 => "OK",
            202 => "Accepted",
            404 => "Not Found",
            503 => "Service Unavailable",
            _ => "Error",
        };
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status} {phrase}\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {bodyBytes.Length}\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(bodyBytes, ct);
        await stream.FlushAsync(ct);
    }
}
