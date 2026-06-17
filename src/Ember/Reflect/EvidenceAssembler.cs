using System.Diagnostics;
using System.Text;
using Ember.Config;
using Ember.Loop;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>How the assembler picks each repo's baseline commit.</summary>
public abstract record BaselineMode
{
    /// <summary>Per-repo shas recorded by previous runs (production). Unknown repo → baseline init.</summary>
    public sealed record LastRecorded(IReadOnlyDictionary<string, string> Shas) : BaselineMode;

    /// <summary>The newest commit older than N hours (console validation — no store needed).</summary>
    public sealed record SinceHours(int Hours) : BaselineMode;
}

/// <summary>One repo's slice of the evidence bundle.</summary>
public sealed record RepoEvidence(
    string Repo, string Path, string? FromSha, string? ToSha, string Section, bool HasChanges,
    int WipCount = 0, bool DriftFlag = false, string? Lifecycle = null);

/// <summary>The full evidence bundle handed to each judge.</summary>
public sealed record EvidenceBundle(string Text, IReadOnlyList<RepoEvidence> Repos)
{
    public int TotalChars => Text.Length;
}

/// <summary>
/// Builds the day's evidence, glance-first (ADR 18). The constellation glance is the primary
/// read — uncommitted WIP, branch/unpushed, lifecycle, and drift — so in-flight work is
/// first-class, not an afterthought the commit-delta is blind to. Each repo's section then
/// layers the committed delta since baseline (git, the authority on what landed), the
/// uncommitted file paths (read locally so they are citable), and the touched symbols from the
/// code graph (enrichment; soft-fails to nothing). Repos that are missing, not git checkouts,
/// or unreadable are noted and skipped — a broken repo never blocks the recap, and a missing
/// glance degrades to the commit-led read.
/// </summary>
public sealed class EvidenceAssembler
{
    private readonly GraphContext _graph;
    private readonly GlanceReader _glance;
    private readonly EmberOptions _options;
    private readonly ILogger<EvidenceAssembler> _logger;

    public EvidenceAssembler(
        GraphContext graph, GlanceReader glance, IOptions<EmberOptions> options,
        ILogger<EvidenceAssembler> logger)
    {
        _graph = graph;
        _glance = glance;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EvidenceBundle> AssembleAsync(BaselineMode mode, CancellationToken ct)
    {
        var reflect = _options.Reflect;

        // Primary read: the constellation glance (soft — empty map on any failure).
        var glance = await _glance.ReadAsync(ct);
        var glanceOn = reflect.Glance.Enabled && !string.IsNullOrWhiteSpace(reflect.Glance.ScriptPath);

        var repos = new List<RepoEvidence>();
        foreach (var (key, entry) in _options.Repos.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            glance.TryGetValue(key, out var g);
            repos.Add(await GatherRepoAsync(key, entry.Path, mode, reflect, g, ct));
        }

        var changed = repos.Where(r => r.HasChanges).ToList();
        var quiet = repos.Where(r => !r.HasChanges).ToList();
        var drifting = quiet.Where(r => r.DriftFlag).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Evidence — {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"{changed.Count} repo(s) in flight, {quiet.Count} quiet.");
        if (glanceOn && glance.Count == 0)
            sb.AppendLine("_Constellation glance unavailable — evidence is commit-led only (in-flight WIP framing may be incomplete)._");
        else if (glanceOn)
            sb.AppendLine($"_Primary read: constellation glance ({glance.Count} repos)._");
        sb.AppendLine();

        foreach (var repo in changed)
        {
            sb.AppendLine(repo.Section);
            sb.AppendLine();
        }

        if (drifting.Count > 0)
            sb.AppendLine("Drift — declared active, quiet past the threshold, no WIP: "
                + string.Join(", ", drifting.Select(r => $"{r.Repo} ({r.Lifecycle})")));
        if (quiet.Count > 0)
            sb.AppendLine("Quiet: " + string.Join(", ", quiet.Select(r => $"{r.Repo} ({QuietNote(r)})")));

        var text = sb.ToString();
        if (text.Length > reflect.MaxTotalEvidenceChars)
            text = text[..reflect.MaxTotalEvidenceChars] + "\n...(evidence truncated)";

        return new EvidenceBundle(text, repos);
    }

    private static string QuietNote(RepoEvidence r) =>
        r.DriftFlag ? "drift"
        : r.FromSha is null && r.ToSha is not null ? "baseline recorded"
        : r.ToSha is null ? "unreadable"
        : "no changes";

    private async Task<RepoEvidence> GatherRepoAsync(
        string key, string path, BaselineMode mode, ReflectOptions reflect, GlanceRepo? glance,
        CancellationToken ct)
    {
        if (!Directory.Exists(path) || !Directory.Exists(Path.Combine(path, ".git")))
        {
            _logger.LogWarning("Reflect: {Repo} at {Path} is missing or not a git checkout; skipping.", key, path);
            return new RepoEvidence(key, path, null, null, "", HasChanges: false);
        }

        var to = (await GitAsync(path, ["rev-parse", "HEAD"], ct))?.Trim();
        if (string.IsNullOrEmpty(to))
        {
            _logger.LogWarning("Reflect: could not resolve HEAD for {Repo}; skipping.", key);
            return new RepoEvidence(key, path, null, null, "", HasChanges: false);
        }

        // In-flight work (uncommitted) — the signal the commit-delta is blind to. Read locally
        // so the paths are citable; the glance only carries a count.
        var wip = await UncommittedFilesAsync(path, ct);

        var from = mode switch
        {
            BaselineMode.LastRecorded last => last.Shas.TryGetValue(key, out var sha) ? sha : null,
            BaselineMode.SinceHours since =>
                (await GitAsync(path, ["rev-list", "-1", $"--before={since.Hours} hours ago", "HEAD"], ct))?.Trim(),
            _ => null,
        };

        List<string> commits = new();
        List<string> files = new();
        var fromUsable = !string.IsNullOrEmpty(from) && from != to;
        if (fromUsable)
        {
            var commitsRaw = await GitAsync(path, ["log", "--format=%h %s", $"{from}..{to}"], ct);
            if (commitsRaw is null)
            {
                // The recorded baseline is not an ancestor of HEAD (history rewritten or the
                // object is gone). Re-baseline rather than failing the whole recap.
                _logger.LogWarning(
                    "Reflect: baseline {From} for {Repo} is unusable; re-baselining at {To}.",
                    Short(from!), key, Short(to));
                from = null;
            }
            else
            {
                commits = SplitLines(commitsRaw);
                files = SplitLines(await GitAsync(path, ["diff", "--name-only", from!, to], ct) ?? "");
            }
        }

        // The baseline we actually have after any re-baseline above (null = first run / reset).
        var baselineSha = string.IsNullOrEmpty(from) ? null : from;
        var drift = glance?.DriftFlag ?? false;
        var ahead = glance?.Ahead ?? false;
        var hasChanges = commits.Count > 0 || wip.Count > 0 || ahead;

        if (!hasChanges)
        {
            // Nothing landed and nothing in flight. Record the baseline (or note drift) and move on.
            return new RepoEvidence(
                key, path, baselineSha, to, "", HasChanges: false,
                WipCount: 0, DriftFlag: drift, Lifecycle: glance?.Lifecycle);
        }

        var section = BuildSection(key, glance, commits, files, wip, from, to, reflect);

        // Freshen the graph before reading symbols — the watcher is not reliable across
        // sessions, and stale enrichment is a silent correctness bug (ADR 15). Only in-flight
        // repos reach here, so the cost is bounded. Soft: ReindexAsync never throws.
        if (_options.Graph.ReindexBeforeRead)
            await _graph.ReindexAsync(path, ct);

        // Enrich from both what landed and what is in flight — symbols for either are useful.
        var symbolFiles = files.Concat(wip).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var symbols = await SafeSymbolsAsync(path, symbolFiles, ct);
        if (symbols is not null)
            section += "\nSymbols touched (from the code graph):\n" + symbols;

        section = section.TrimEnd();
        if (section.Length > reflect.MaxEvidenceCharsPerRepo)
            section = section[..reflect.MaxEvidenceCharsPerRepo] + "\n...(repo evidence truncated)";

        return new RepoEvidence(
            key, path, baselineSha, to, section, HasChanges: true,
            WipCount: wip.Count, DriftFlag: drift, Lifecycle: glance?.Lifecycle);
    }

    /// <summary>Renders one in-flight repo's section, glance framing first, then commits/WIP.</summary>
    private static string BuildSection(
        string key, GlanceRepo? glance, List<string> commits, List<string> files, List<string> wip,
        string? from, string? to, ReflectOptions reflect)
    {
        var sb = new StringBuilder();

        // Headline: the glance framing (lifecycle, branch, age) plus the concrete counts.
        var bits = new List<string>();
        if (glance is not null)
            bits.Add(glance.Lifecycle);
        bits.Add($"{wip.Count} uncommitted");
        bits.Add($"{commits.Count} commit(s) since baseline");
        if (glance?.Branch is { Length: > 0 } br)
        {
            var sync = glance.Ahead ? " [unpushed]" : glance.Behind ? " [behind]" : "";
            bits.Add($"branch {br.Split("...")[0]}{sync}");
        }
        if (glance?.DaysSinceCommit is { } days)
            bits.Add(days == 0 ? "committed today" : $"last commit {days}d ago");
        sb.AppendLine($"### {key} — {string.Join(" · ", bits)}");
        if (from is not null && to is not null)
            sb.AppendLine($"({Short(from)}..{Short(to)})");

        if (wip.Count > 0)
        {
            sb.AppendLine($"Uncommitted (working tree — in-flight, not yet committed): {wip.Count} file(s)");
            foreach (var file in wip.Take(reflect.MaxFilesPerRepo))
                sb.AppendLine($"- {file}");
            if (wip.Count > reflect.MaxFilesPerRepo)
                sb.AppendLine($"- ...({wip.Count - reflect.MaxFilesPerRepo} more)");
        }

        if (commits.Count > 0)
        {
            sb.AppendLine("Commits since baseline:");
            foreach (var line in commits.Take(reflect.MaxCommitsPerRepo))
                sb.AppendLine($"- {line}");
            if (commits.Count > reflect.MaxCommitsPerRepo)
                sb.AppendLine($"- ...({commits.Count - reflect.MaxCommitsPerRepo} more)");

            if (files.Count > 0)
            {
                sb.AppendLine("Committed files:");
                foreach (var file in files.Take(reflect.MaxFilesPerRepo))
                    sb.AppendLine($"- {file}");
                if (files.Count > reflect.MaxFilesPerRepo)
                    sb.AppendLine($"- ...({files.Count - reflect.MaxFilesPerRepo} more)");
            }
        }
        else if (glance is { Recent.Count: > 0 })
        {
            // No delta against the recap baseline, but the glance's recent-window commits give
            // the judge the headline narrative (e.g. a night already baselined by a prior run).
            sb.AppendLine("Recent commits (glance window):");
            foreach (var line in glance.Recent.Take(reflect.MaxCommitsPerRepo))
                sb.AppendLine($"- {line}");
        }

        return sb.ToString();
    }

    /// <summary>Uncommitted (tracked-modified + staged + untracked) paths, citable in the recap.</summary>
    private async Task<List<string>> UncommittedFilesAsync(string path, CancellationToken ct)
    {
        var raw = await GitAsync(path, ["status", "--porcelain"], ct);
        if (string.IsNullOrEmpty(raw))
            return new List<string>();

        var paths = new List<string>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Porcelain v1: "XY <path>" or "XY <old> -> <new>" for renames.
            if (line.Length < 4)
                continue;
            var entry = line[3..].Trim();
            var arrow = entry.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                entry = entry[(arrow + 4)..];
            entry = entry.Trim().Trim('"');
            if (entry.Length > 0)
                paths.Add(entry);
        }
        return paths;
    }

    private async Task<string?> SafeSymbolsAsync(string path, List<string> files, CancellationToken ct)
    {
        try
        {
            return await _graph.SymbolsForFilesAsync(path, files, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reflect: graph enrichment failed for {Path}.", path);
            return null;
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Runs git in a repo and returns stdout, or null on any failure.</summary>
    private async Task<string?> GitAsync(string repoPath, string[] args, CancellationToken ct)
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
        psi.ArgumentList.Add(repoPath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reflect: git {Args} failed in {Path}.", string.Join(' ', args), repoPath);
            return null;
        }
    }
}
