using System.Net;
using System.Net.Sockets;
using System.Text;
using Discord.WebSocket;
using Ember.Config;
using Ember.Reflect;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>
/// A loopback-only "run now" trigger so the <c>Start-Plan</c> desktop launcher can start a morning
/// brief without Discord — the sibling of <see cref="ReflectTriggerService"/> (ADR 17/19). Binds
/// <c>http://127.0.0.1:{Overnight:LocalTriggerPort}/</c> only (never routable), only when a port is
/// configured (0 = off, default).
///
/// <code>
///   GET  /ready  -> 200 "ready" when a run can start now, else 503 with a reason.
///   POST /brief  -> runs SYNCHRONOUSLY, 200 + summary when done (so the launcher frees the GPUs
///                   only after), 409 if a run is already in progress, 503 if not ready / disabled.
/// </code>
/// Raw TCP (not HttpListener) so a non-elevated process can bind loopback without a urlacl.
/// </summary>
public sealed class OvernightTriggerService : BackgroundService
{
    private readonly OvernightExecutor _executor;
    private readonly DiscordSocketClient _client;
    private readonly OvernightOptions _options;
    private readonly ILogger<OvernightTriggerService> _logger;

    public OvernightTriggerService(
        OvernightExecutor executor,
        DiscordSocketClient client,
        IOptions<EmberOptions> options,
        ILogger<OvernightTriggerService> logger)
    {
        _executor = executor;
        _client = client;
        _options = options.Value.Overnight;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _options.LocalTriggerPort;
        if (port <= 0)
        {
            _logger.LogInformation("Overnight local trigger disabled (Ember:Overnight:LocalTriggerPort=0).");
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Overnight local trigger could not bind 127.0.0.1:{Port}; trigger unavailable.", port);
            return;
        }

        _logger.LogInformation("Overnight local trigger listening on http://127.0.0.1:{Port}/ (loopback only).", port);
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
                    _logger.LogWarning(ex, "Overnight local trigger accept failed.");
                    continue;
                }

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

                string firstLine;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                    var head = await ReadRequestHeadAsync(stream, readCts.Token);
                    firstLine = head.Length == 0 ? "" : head.Split('\n', 2)[0];
                }

                var (method, path) = ReflectTriggerService.ParseRequestLine(firstLine);
                var (ready, reason) = ReflectTriggerService.Decide(_options.Enabled, _client.ConnectionState);

                int status;
                string body;
                if (path.Equals("/ready", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    (status, body) = ready ? (200, "ready") : (503, reason);
                }
                else if (path.Equals("/brief", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    if (!ready)
                    {
                        (status, body) = (503, reason);
                    }
                    else
                    {
                        // Synchronous on purpose: the launcher waits so it frees the GPUs only after
                        // the judges finish. Bound to the service token, not the client.
                        var summary = await _executor.ExecuteAsync(ct);
                        (status, body) = ReflectTriggerService.RunInProgress(summary) ? (409, summary) : (200, summary);
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
                _logger.LogWarning(ex, "Overnight local trigger request failed.");
            }
        }
    }

    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
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
            404 => "Not Found",
            409 => "Conflict",
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
