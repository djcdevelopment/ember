using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ember.Config;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>
/// The manifest→board delta, tiered exactly as <c>pm/board-sync.md</c> defines: <em>auto-safe</em>
/// (additive, scriptable), <em>decision</em> (a human structural call), <em>live-truth</em>
/// (verify, don't guess). <see cref="BoardAvailable"/> is false when ADO was unreachable (not
/// authed) — the area-path/epic tiers are then absent, but summary-doc and marker items still
/// stand (they are filesystem/manifest-only).
/// </summary>
public sealed record BoardSyncDelta(
    bool InSync,
    bool BoardAvailable,
    int ManifestRepos,
    IReadOnlyList<string> AutoSafe,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> LiveTruth)
{
    public int Total => AutoSafe.Count + Decisions.Count + LiveTruth.Count;

    /// <summary>The auto-safe items the planner can apply in-repo without external creds —
    /// today, the "missing pm/repos/&lt;name&gt;.md → draft" items. Everything else is surfaced.</summary>
    public IReadOnlyList<string> InRepoAutoSafe =>
        AutoSafe.Where(s => s.Contains("missing pm/", StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>
/// Reads the GAD board-sync delta (<c>board-sync-check.py --json</c>) — the PM reconciliation
/// playbook's read step. Mirrors <see cref="Reflect.GlanceReader"/>'s posture: any IO / parse /
/// subprocess / auth problem returns <c>null</c> with a logged warning, and the overnight brief
/// proceeds glance-fed with no board proposals (and says so). Read-only; the checker writes nothing.
/// </summary>
public class BoardSyncReader
{
    private readonly BoardSyncOptions _options;
    private readonly ILogger<BoardSyncReader> _logger;

    public BoardSyncReader(IOptions<EmberOptions> options, ILogger<BoardSyncReader> logger)
    {
        _options = options.Value.Overnight.BoardSync;
        _logger = logger;
    }

    /// <summary>The tiered delta, or <c>null</c> when board-sync is disabled, missing, or failed.</summary>
    public async Task<BoardSyncDelta?> ReadAsync(CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ScriptPath))
            return null;

        var json = await RunCheckerAsync(ct);
        if (json is null)
            return null;

        try
        {
            return Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Overnight: board-sync output was not valid JSON; brief carries no board proposals.");
            return null;
        }
    }

    private static BoardSyncDelta Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new BoardSyncDelta(
            InSync: Bool(root, "in_sync"),
            BoardAvailable: Bool(root, "board_available"),
            ManifestRepos: Int(root, "manifest_repos"),
            AutoSafe: StrList(root, "auto_safe"),
            Decisions: StrList(root, "decisions"),
            LiveTruth: StrList(root, "live_truth"));
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static List<string> StrList(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            list.AddRange(arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
        return list;
    }

    /// <summary>
    /// Spawns <c>{Command} {ExtraArgs...} {ScriptPath} --json</c>; returns stdout or <c>null</c>.
    /// <c>protected virtual</c> so tests can supply canned JSON without spawning python/ADO.
    /// </summary>
    protected virtual async Task<string?> RunCheckerAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var extra in _options.ExtraArgs)
            psi.ArgumentList.Add(extra);
        psi.ArgumentList.Add(_options.ScriptPath);
        psi.ArgumentList.Add("--json");

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Overnight: could not start board-sync ({Command}); brief carries no board proposals.",
                _options.Command);
            return null;
        }

        using (process)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (ct.IsCancellationRequested)
                    return null;
                _logger.LogWarning(
                    "Overnight: board-sync exceeded its {Timeout}s timeout; brief carries no board proposals.",
                    timeout.TotalSeconds);
                return null;
            }

            // Exit 1 = "delta exists" (not an error); only a crash/missing-deps yields no JSON.
            var text = stdout.ToString();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                return text[start..(end + 1)];

            var detail = stderr.ToString().Trim();
            _logger.LogWarning(
                "Overnight: board-sync produced no JSON (exit {Code}): {Detail}",
                process.ExitCode, string.IsNullOrEmpty(detail) ? "(no stderr)" : detail);
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited between the check and the kill.
        }
    }
}
