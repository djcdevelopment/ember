using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ember.Config;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>One repo's slice of the constellation glance (the <c>--json</c> read).</summary>
/// <remarks>
/// The glance reports <see cref="Wip"/> as a <em>count</em> of dirty working-tree entries, not
/// the paths — the paths are read locally by <see cref="EvidenceAssembler"/> so they can be
/// cited. <see cref="Lifecycle"/> and <see cref="DriftFlag"/> are the glance's unique signal:
/// they come from the constellation manifest and cannot be derived from a single repo's git.
/// </remarks>
public sealed record GlanceRepo(
    string Name,
    string Kind,
    string Lifecycle,
    int Wip,
    IReadOnlyList<string> Recent,
    string? Branch,
    bool Ahead,
    bool Behind,
    int? DaysSinceCommit,
    bool Hot,
    bool DriftFlag)
{
    /// <summary>True when the glance shows in-flight work the commit-delta would miss.</summary>
    public bool HasInFlightSignal => Wip > 0 || Ahead || Recent.Count > 0;
}

/// <summary>
/// Reads the constellation glance (<c>constellation-glance.py --json</c>) — the cross-repo
/// working-tree read that is Reflect's primary evidence (ADR 18). Mirrors
/// <see cref="Loop.GraphContext"/>'s posture exactly: every IO / parse / subprocess problem
/// returns an empty read with a logged warning, so a glance issue degrades Reflect to the
/// commit-led path rather than blocking the recap. The glance itself is read-only.
/// </summary>
public class GlanceReader
{
    private readonly GlanceOptions _options;
    private readonly ILogger<GlanceReader> _logger;

    public GlanceReader(IOptions<EmberOptions> options, ILogger<GlanceReader> logger)
    {
        _options = options.Value.Reflect.Glance;
        _logger = logger;
    }

    /// <summary>
    /// The glance keyed by repo name (lower-cased, matching the allowlist keys), or an empty
    /// map when the glance is disabled, missing, or unreadable.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GlanceRepo>> ReadAsync(CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ScriptPath))
            return EmptyMap;

        var json = await RunGlanceAsync(ct);
        if (json is null)
            return EmptyMap;

        try
        {
            return Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Reflect: constellation glance output was not valid JSON; using commit-led evidence.");
            return EmptyMap;
        }
    }

    private static readonly IReadOnlyDictionary<string, GlanceRepo> EmptyMap =
        new Dictionary<string, GlanceRepo>();

    private static Dictionary<string, GlanceRepo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, GlanceRepo>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("repos", out var repos) || repos.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var r in repos.EnumerateArray())
        {
            var name = Str(r, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var recent = new List<string>();
            if (r.TryGetProperty("recent", out var rec) && rec.ValueKind == JsonValueKind.Array)
                recent.AddRange(rec.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0));

            map[name] = new GlanceRepo(
                Name: name,
                Kind: Str(r, "kind") ?? "git",
                Lifecycle: Str(r, "lifecycle") ?? "?",
                Wip: Int(r, "wip") ?? 0,
                Recent: recent,
                Branch: Str(r, "branch"),
                Ahead: Bool(r, "ahead"),
                Behind: Bool(r, "behind"),
                DaysSinceCommit: Int(r, "days_since_commit"),
                Hot: Bool(r, "hot"),
                DriftFlag: Bool(r, "drift_flag"));
        }
        return map;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Spawns <c>{Command} {ExtraArgs...} {ScriptPath} --json</c> and returns stdout, or
    /// <c>null</c> on missing executable, non-zero exit, timeout, or any other failure.
    /// <c>protected virtual</c> so tests can supply canned JSON without spawning python.
    /// </summary>
    protected virtual async Task<string?> RunGlanceAsync(CancellationToken ct)
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
                ex, "Reflect: could not start the constellation glance ({Command}); using commit-led evidence.",
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
                    "Reflect: constellation glance exceeded its {Timeout}s timeout; using commit-led evidence.",
                    timeout.TotalSeconds);
                return null;
            }

            if (process.ExitCode != 0)
            {
                var detail = stderr.ToString().Trim();
                _logger.LogWarning(
                    "Reflect: constellation glance exited {Code}: {Detail}",
                    process.ExitCode, string.IsNullOrEmpty(detail) ? "(no stderr)" : detail);
                return null;
            }

            // The script prints one JSON document to stdout; extract the outermost braces
            // defensively in case a log line ever lands on stdout.
            var text = stdout.ToString();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text[start..(end + 1)] : null;
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
