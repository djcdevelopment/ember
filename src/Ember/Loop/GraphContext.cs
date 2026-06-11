using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ember.Config;
using Microsoft.Extensions.Options;

namespace Ember.Loop;

/// <summary>
/// Reads the local code knowledge graph (codebase-memory-mcp) for a repo and renders compact
/// context sections for the planner/critic and the reflect evidence assembler. Mirrors
/// <see cref="Manifest.ManifestLoader"/>'s posture exactly: every IO / parse / subprocess
/// problem returns <c>null</c> with a logged warning, so a graph issue can never block a run.
/// </summary>
public class GraphContext
{
    private static readonly string[] SkipLabels = ["File", "Folder", "Section", "Project"];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "with", "that", "this", "from", "into", "have", "what", "when", "then", "them",
        "build", "make", "adds", "should", "would", "could", "support", "command", "feature",
    };

    private readonly EmberOptions _options;
    private readonly ILogger<GraphContext> _logger;

    public GraphContext(IOptions<EmberOptions> options, ILogger<GraphContext> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// The architecture-plus-relevant-symbols section folded into the round-1 prompt, or
    /// <c>null</c> when the graph is disabled or unavailable.
    /// </summary>
    public async Task<string?> GatherAsync(string repoPath, string brief, CancellationToken ct)
    {
        if (!_options.Graph.Enabled)
            return null;

        var project = DeriveProjectName(repoPath);
        var arch = await RunToolAsync(
            "get_architecture", JsonSerializer.Serialize(new { project }), ct);
        if (arch is null)
            return null;

        string? section;
        try
        {
            section = FormatArchitecture(project, arch);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Graph architecture for {Project} was not valid JSON.", project);
            return null;
        }
        if (section is null)
            return null;

        var symbols = await SearchSymbolsAsync(project, BriefTerms(brief), ct);
        if (symbols is not null)
            section += "\nSymbols matching the brief (name — kind — file):\n" + symbols;

        section += "\nUse these names as anchors; the tracked-file list below remains the "
                 + "authority on which paths exist.";
        return Truncate(section, _options.Graph.MaxChars);
    }

    /// <summary>
    /// Compact symbol lines for the given changed files (reflect evidence enrichment), or
    /// <c>null</c> when the graph is disabled, unavailable, or matches nothing.
    /// </summary>
    public async Task<string?> SymbolsForFilesAsync(
        string repoPath, IReadOnlyList<string> files, CancellationToken ct)
    {
        if (!_options.Graph.Enabled || files.Count == 0)
            return null;

        var stems = files
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => !string.IsNullOrWhiteSpace(s) && s!.Length >= 3)
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        if (stems.Count == 0)
            return null;

        return await SearchSymbolsAsync(DeriveProjectName(repoPath), stems, ct);
    }

    /// <summary>
    /// The graph's project key for a repo path — drive colon dropped, separators to dashes —
    /// e.g. <c>D:\work\leopard</c> → <c>D-work-leopard</c>.
    /// </summary>
    public static string DeriveProjectName(string repoPath)
    {
        var normalized = repoPath.Replace('\\', '/').TrimEnd('/');
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.TrimEnd(':'));
        return string.Join('-', segments);
    }

    private async Task<string?> SearchSymbolsAsync(
        string project, IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (terms.Count == 0)
            return null;

        var pattern = string.Join("|", terms.Select(Regex.Escape));
        var json = await RunToolAsync(
            "search_graph", JsonSerializer.Serialize(new { project, name_pattern = pattern }), ct);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return null;

            var lines = new List<string>();
            foreach (var r in results.EnumerateArray())
            {
                var label = r.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                if (SkipLabels.Contains(label))
                    continue;
                var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
                var file = r.TryGetProperty("file_path", out var f) ? f.GetString() : null;
                if (name is null)
                    continue;
                lines.Add($"- {name} — {label} — {file ?? "?"}");
                if (lines.Count >= 15)
                    break;
            }
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Graph search for {Project} was not valid JSON.", project);
            return null;
        }
    }

    private static string? FormatArchitecture(string project, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("total_nodes", out var nodes))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine($"Knowledge graph (codebase-memory-mcp, project {project}):");
        sb.Append($"Scale: {nodes.GetInt64()} nodes");
        if (root.TryGetProperty("total_edges", out var edges))
            sb.Append($" / {edges.GetInt64()} edges");
        sb.AppendLine();

        if (root.TryGetProperty("languages", out var languages))
        {
            var parts = languages.EnumerateArray()
                .Select(l => $"{l.GetProperty("language").GetString()} ({l.GetProperty("file_count").GetInt64()} files)");
            sb.AppendLine("Languages: " + string.Join(", ", parts));
        }

        if (root.TryGetProperty("packages", out var packages))
        {
            var parts = packages.EnumerateArray()
                .Take(8)
                .Select(p => $"{p.GetProperty("name").GetString()} ({p.GetProperty("node_count").GetInt64()} nodes)");
            var text = string.Join(", ", parts);
            if (text.Length > 0)
                sb.AppendLine("Packages: " + text);
        }

        if (root.TryGetProperty("entry_points", out var entries))
        {
            var parts = entries.EnumerateArray()
                .Take(8)
                .Select(e => $"{e.GetProperty("name").GetString()} ({e.GetProperty("file").GetString()})");
            var text = string.Join(", ", parts);
            if (text.Length > 0)
                sb.AppendLine("Entry points: " + text);
        }

        return sb.ToString();
    }

    private static IReadOnlyList<string> BriefTerms(string brief)
    {
        return Regex.Matches(brief, "[A-Za-z0-9_]{4,}")
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string Truncate(string text, int maxChars) =>
        maxChars > 0 && text.Length > maxChars
            ? text[..maxChars] + "\n...(graph context truncated)"
            : text;

    /// <summary>
    /// Spawns <c>{Command} {ExtraArgs...} cli &lt;tool&gt; &lt;json&gt;</c> and returns stdout.
    /// <c>null</c> on non-zero exit, missing executable, timeout, or any other failure.
    /// </summary>
    /// <remarks>
    /// <c>protected virtual</c> so test fixtures can override it with canned JSON instead of
    /// spawning the CLI — the same seam <see cref="Manifest.ManifestLoader"/> exposes.
    /// </remarks>
    protected virtual async Task<string?> RunToolAsync(string tool, string argsJson, CancellationToken ct)
    {
        var graph = _options.Graph;
        var psi = new ProcessStartInfo
        {
            FileName = graph.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(graph.CacheDir))
            psi.Environment["CBM_CACHE_DIR"] = graph.CacheDir;
        foreach (var extra in graph.ExtraArgs)
            psi.ArgumentList.Add(extra);
        psi.ArgumentList.Add("cli");
        psi.ArgumentList.Add(tool);
        psi.ArgumentList.Add(argsJson);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not start the graph CLI ({Command}). Proceeding without graph context.",
                graph.Command);
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

            var timeout = TimeSpan.FromSeconds(Math.Max(1, graph.TimeoutSeconds));
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
                    "Graph CLI exceeded its {Timeout}s timeout on {Tool}; proceeding without graph context.",
                    timeout.TotalSeconds, tool);
                return null;
            }

            if (process.ExitCode != 0)
            {
                var detail = stderr.ToString().Trim();
                _logger.LogWarning(
                    "Graph CLI exited {Code} on {Tool}: {Detail}",
                    process.ExitCode, tool, string.IsNullOrEmpty(detail) ? "(no stderr)" : detail);
                return null;
            }

            // The CLI logs to stderr and prints one JSON document to stdout; extract the
            // outermost braces defensively in case a log line ever lands on stdout.
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
