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
    string Repo, string Path, string? FromSha, string? ToSha, string Section, bool HasChanges);

/// <summary>The full evidence bundle handed to each judge.</summary>
public sealed record EvidenceBundle(string Text, IReadOnlyList<RepoEvidence> Repos)
{
    public int TotalChars => Text.Length;
}

/// <summary>
/// Builds the day's evidence: per allowlisted repo, the committed delta since the baseline —
/// commits and changed files from git (the authority on what changed), symbols from the code
/// graph (enrichment; soft-fails to nothing). Repos that are missing, not git checkouts, or
/// unreadable are noted and skipped — a broken repo never blocks the recap.
/// </summary>
public sealed class EvidenceAssembler
{
    private readonly GraphContext _graph;
    private readonly EmberOptions _options;
    private readonly ILogger<EvidenceAssembler> _logger;

    public EvidenceAssembler(
        GraphContext graph, IOptions<EmberOptions> options, ILogger<EvidenceAssembler> logger)
    {
        _graph = graph;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EvidenceBundle> AssembleAsync(BaselineMode mode, CancellationToken ct)
    {
        var reflect = _options.Reflect;
        var repos = new List<RepoEvidence>();

        foreach (var (key, entry) in _options.Repos.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            repos.Add(await GatherRepoAsync(key, entry.Path, mode, reflect, ct));
        }

        var changed = repos.Where(r => r.HasChanges).ToList();
        var quiet = repos.Where(r => !r.HasChanges).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Evidence — {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"{changed.Count} repo(s) with changes, {quiet.Count} quiet.");
        sb.AppendLine();
        foreach (var repo in changed)
        {
            sb.AppendLine(repo.Section);
            sb.AppendLine();
        }
        if (quiet.Count > 0)
            sb.AppendLine("Quiet: " + string.Join(", ", quiet.Select(r => $"{r.Repo} ({QuietNote(r)})")));

        var text = sb.ToString();
        if (text.Length > reflect.MaxTotalEvidenceChars)
            text = text[..reflect.MaxTotalEvidenceChars] + "\n...(evidence truncated)";

        return new EvidenceBundle(text, repos);
    }

    private static string QuietNote(RepoEvidence r) =>
        r.FromSha is null && r.ToSha is not null ? "baseline recorded"
        : r.ToSha is null ? "unreadable"
        : "no changes";

    private async Task<RepoEvidence> GatherRepoAsync(
        string key, string path, BaselineMode mode, ReflectOptions reflect, CancellationToken ct)
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

        var from = mode switch
        {
            BaselineMode.LastRecorded last => last.Shas.TryGetValue(key, out var sha) ? sha : null,
            BaselineMode.SinceHours since =>
                (await GitAsync(path, ["rev-list", "-1", $"--before={since.Hours} hours ago", "HEAD"], ct))?.Trim(),
            _ => null,
        };

        if (string.IsNullOrEmpty(from))
            return new RepoEvidence(key, path, null, to, "", HasChanges: false);
        if (from == to)
            return new RepoEvidence(key, path, from, to, "", HasChanges: false);

        var commitsRaw = await GitAsync(path, ["log", "--format=%h %s", $"{from}..{to}"], ct);
        if (commitsRaw is null)
        {
            // The recorded baseline is not an ancestor of HEAD (history rewritten or the
            // object is gone). Re-baseline rather than failing the whole recap.
            _logger.LogWarning(
                "Reflect: baseline {From} for {Repo} is unusable; re-baselining at {To}.",
                Short(from), key, Short(to));
            return new RepoEvidence(key, path, null, to, "", HasChanges: false);
        }

        var commits = SplitLines(commitsRaw);
        if (commits.Count == 0)
            return new RepoEvidence(key, path, from, to, "", HasChanges: false);

        var files = SplitLines(await GitAsync(path, ["diff", "--name-only", from, to], ct) ?? "");

        var sb = new StringBuilder();
        sb.AppendLine($"### {key} — {commits.Count} commit(s), {files.Count} file(s)  ({Short(from)}..{Short(to)})");
        sb.AppendLine("Commits:");
        foreach (var line in commits.Take(reflect.MaxCommitsPerRepo))
            sb.AppendLine($"- {line}");
        if (commits.Count > reflect.MaxCommitsPerRepo)
            sb.AppendLine($"- ...({commits.Count - reflect.MaxCommitsPerRepo} more)");

        if (files.Count > 0)
        {
            sb.AppendLine("Files:");
            foreach (var file in files.Take(reflect.MaxFilesPerRepo))
                sb.AppendLine($"- {file}");
            if (files.Count > reflect.MaxFilesPerRepo)
                sb.AppendLine($"- ...({files.Count - reflect.MaxFilesPerRepo} more)");
        }

        // Freshen the graph before reading symbols — the watcher is not reliable across
        // sessions, and stale enrichment is a silent correctness bug (ADR 15). Only changed
        // repos reach here, so the cost is bounded. Soft: ReindexAsync never throws.
        if (_options.Graph.ReindexBeforeRead)
            await _graph.ReindexAsync(path, ct);

        var symbols = await SafeSymbolsAsync(path, files, ct);
        if (symbols is not null)
        {
            sb.AppendLine("Symbols touched (from the code graph):");
            sb.AppendLine(symbols);
        }

        var section = sb.ToString().TrimEnd();
        if (section.Length > reflect.MaxEvidenceCharsPerRepo)
            section = section[..reflect.MaxEvidenceCharsPerRepo] + "\n...(repo evidence truncated)";

        return new RepoEvidence(key, path, from, to, section, HasChanges: true);
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
