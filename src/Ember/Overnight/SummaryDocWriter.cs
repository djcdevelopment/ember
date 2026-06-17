using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ember.Config;
using Ember.Reflect;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>What the auto-safe apply step did (E3): the docs drafted, and the items left to surface.</summary>
public sealed record AutoSafeResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Surfaced)
{
    public static readonly AutoSafeResult None = new(Array.Empty<string>(), Array.Empty<string>());
    public bool DidAnything => Applied.Count > 0;
}

/// <summary>
/// E3's auto-apply, scoped to the one genuinely-safe, in-repo, reversible reconciliation: drafting
/// a missing <c>pm/repos/&lt;name&gt;.md</c> summary stub from the manifest/glance (the board-sync
/// "missing summary-doc → draft" auto-safe item). Additive and by-name only — never <c>git add .</c>,
/// never a history rewrite (the repo rule, mirrors <see cref="JournalWriter"/>). Gated behind
/// <see cref="OvernightOptions.AutoApplyAutoSafe"/> (default off — earn it). Everything else in the
/// auto-safe tier (ADO area paths, which need creds) and every decision/editorial item is returned
/// in <see cref="AutoSafeResult.Surfaced"/> for the brief to propose — never auto-run.
/// </summary>
public sealed class SummaryDocWriter
{
    private static readonly Regex MissingDoc =
        new(@"^(?<name>[^:]+):\s*missing\s+pm/(?<rel>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly OvernightOptions _options;
    private readonly ILogger<SummaryDocWriter> _logger;

    public SummaryDocWriter(IOptions<EmberOptions> options, ILogger<SummaryDocWriter> logger)
    {
        _options = options.Value.Overnight;
        _logger = logger;
    }

    /// <summary>
    /// Drafts every missing summary doc named in the board delta's in-repo auto-safe items, when
    /// auto-apply is enabled. Returns the applied paths plus the items left for the operator.
    /// </summary>
    public async Task<AutoSafeResult> ApplyAsync(
        BoardSyncDelta? board, IReadOnlyDictionary<string, GlanceRepo> glance, CancellationToken ct)
    {
        if (board is null)
            return AutoSafeResult.None;

        var everything = board.AutoSafe.Concat(board.Decisions).Concat(board.LiveTruth).ToList();
        var inRepo = board.InRepoAutoSafe;

        // Gated off (or nothing to apply): nothing was done — surface the whole delta.
        if (!_options.AutoApplyAutoSafe || inRepo.Count == 0)
            return new AutoSafeResult(Array.Empty<string>(), everything);

        var gadRoot = GadRoot();
        if (gadRoot is null)
        {
            _logger.LogWarning("Overnight: cannot resolve the gad root from the board-sync script path; not auto-applying.");
            return new AutoSafeResult(Array.Empty<string>(), everything);
        }

        // Applying: the in-repo items we handle; everything else (creds / judgment) stays surfaced.
        var surfaced = board.AutoSafe.Where(a => !inRepo.Contains(a))
            .Concat(board.Decisions).Concat(board.LiveTruth).ToList();

        var applied = new List<string>();
        foreach (var item in inRepo)
        {
            ct.ThrowIfCancellationRequested();
            var m = MissingDoc.Match(item);
            if (!m.Success)
            {
                surfaced.Add(item);
                continue;
            }
            var name = m.Groups["name"].Value.Trim();
            var rel = m.Groups["rel"].Value.Trim();
            var path = Path.GetFullPath(Path.Combine(gadRoot, "pm", rel.Replace('/', Path.DirectorySeparatorChar)));

            if (File.Exists(path))
                continue; // already drafted (or the delta is stale) — nothing to do
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                glance.TryGetValue(name, out var g);
                await File.WriteAllTextAsync(path, StubFor(name, rel, g), ct);
                applied.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Overnight: could not draft summary stub {Path}.", path);
                surfaced.Add(item);
            }
        }

        if (applied.Count > 0)
            await TryCommitAsync(gadRoot, applied, ct);

        return new AutoSafeResult(applied, surfaced);
    }

    /// <summary>A minimal, honest summary stub — additive, flagged for enrichment, glance-seeded.</summary>
    private static string StubFor(string name, string rel, GlanceRepo? g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {name}");
        sb.AppendLine();
        sb.AppendLine("> Stub auto-drafted by ember overnight (board-sync auto-safe). Enrich from the");
        sb.AppendLine("> manifest entry + repo reality, then remove this note. See `pm/board-sync.md`.");
        sb.AppendLine();
        if (g is not null)
        {
            sb.AppendLine($"- **Lifecycle:** {g.Lifecycle}");
            if (!string.IsNullOrWhiteSpace(g.Branch)) sb.AppendLine($"- **Branch:** {g.Branch}");
            sb.AppendLine($"- **Working tree:** {(g.Wip > 0 ? $"{g.Wip} uncommitted file(s)" : "clean")}"
                + (g.Ahead ? ", unpushed commits" : ""));
            sb.AppendLine($"- **Last commit:** {(g.DaysSinceCommit is { } d ? (d == 0 ? "today" : $"{d}d ago") : "unknown")}");
        }
        else
        {
            sb.AppendLine("- **Lifecycle:** (unknown — glance had no entry; fill from the manifest)");
        }
        sb.AppendLine();
        sb.AppendLine("## Role");
        sb.AppendLine("_TODO — what this repo is, who it's for, what it depends on._");
        sb.AppendLine();
        sb.AppendLine("## Current state");
        sb.AppendLine("_TODO._");
        return sb.ToString();
    }

    /// <summary>Stage the drafted files by name and commit them in the gad repo. Soft.</summary>
    private async Task TryCommitAsync(string gadRoot, IReadOnlyList<string> files, CancellationToken ct)
    {
        if (!_options.CommitArtifacts)
            return;
        try
        {
            var root = (await GitAsync(gadRoot, ["rev-parse", "--show-toplevel"], ct))?.Trim();
            if (string.IsNullOrEmpty(root))
            {
                _logger.LogWarning("Overnight: {Dir} is not inside a git repo; not committing drafted stubs.", gadRoot);
                return;
            }
            var args = new List<string> { "add", "--" };
            args.AddRange(files);
            await GitAsync(root, args.ToArray(), ct);
            var msg = $"overnight: draft {files.Count} missing summary doc(s) (automated)";
            var committed = await GitAsync(root, ["commit", "-m", msg], ct);
            _logger.LogInformation(committed is null
                ? "Overnight: nothing to commit for drafted stubs."
                : $"Overnight: committed {files.Count} drafted summary stub(s).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Overnight: summary-stub commit failed (non-fatal).");
        }
    }

    /// <summary>gad root = the board-sync script's grandparent (…/gad/pm/scripts/board-sync-check.py).</summary>
    private string? GadRoot()
    {
        var script = _options.BoardSync.ScriptPath;
        if (string.IsNullOrWhiteSpace(script))
            return null;
        try
        {
            return new FileInfo(script).Directory?.Parent?.Parent?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GitAsync(string cwd, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(cwd);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("git did not start");
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }
        return process.ExitCode == 0 ? stdout.ToString() : null;
    }
}
