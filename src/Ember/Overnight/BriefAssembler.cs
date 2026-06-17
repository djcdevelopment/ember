using System.Text;
using Ember.Config;
using Ember.Reflect;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>The assembled objective state handed to the brief author (and printed by the dry-run).</summary>
public sealed record BriefInputs(
    string Text,
    bool GlanceAvailable,
    int GlanceRepoCount,
    BoardSyncDelta? Board,
    string? ReflectRecap,
    IReadOnlyDictionary<string, GlanceRepo> Glance)
{
    public int TotalChars => Text.Length;
}

/// <summary>
/// Assembles the morning brief's <em>objective state</em> — deterministically, from the
/// constellation glance (the in-flight truth), the latest Reflect recap (last night's narrative),
/// and the board-sync delta (the PM reconciliation playbook). This is the read-only heart of the
/// overnight planner (ADR 19): it categorises the constellation into <c>changed</c>,
/// <c>drifting/sitting</c>, <c>needs-a-decision</c>, and <c>next-slice candidates</c>, so the
/// author writes a brief grounded in state, not vibes. Every input is soft — a missing glance,
/// recap, or board reader degrades to a stated gap, never a crash.
/// </summary>
public sealed class BriefAssembler
{
    private readonly GlanceReader _glance;
    private readonly BoardSyncReader _board;
    private readonly EmberOptions _options;
    private readonly ILogger<BriefAssembler> _logger;

    public BriefAssembler(
        GlanceReader glance, BoardSyncReader board, IOptions<EmberOptions> options,
        ILogger<BriefAssembler> logger)
    {
        _glance = glance;
        _board = board;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BriefInputs> AssembleAsync(CancellationToken ct)
    {
        var overnight = _options.Overnight;
        var glance = await _glance.ReadAsync(ct);
        var board = await _board.ReadAsync(ct);
        var recap = ReadLatestRecap();

        // Order repos newest-touched first: committed today, then by WIP volume.
        var repos = glance.Values
            .Where(r => string.Equals(r.Kind, "git", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Recent.Count > 0)
            .ThenByDescending(r => r.Wip)
            .ToList();

        var changed = new List<string>();
        var drifting = new List<string>();
        var needsCall = new List<string>();
        var nextSlice = new List<string>();

        foreach (var r in repos)
        {
            var deprecating = r.Lifecycle.Contains("deprecat", StringComparison.OrdinalIgnoreCase);

            if (r.Recent.Count > 0 || r.Wip > 0)
                changed.Add(Describe(r));

            // Drifting/sitting: declared-active-but-quiet, unpushed-and-idle, or churn in a wind-down.
            if (r.DriftFlag)
                drifting.Add($"{r.Name} — declared {r.Lifecycle}, quiet {Age(r)}, no WIP (drift flag)");
            else if (r.Ahead && r.Wip == 0)
                drifting.Add($"{r.Name} — unpushed commits sitting, no WIP ({r.Lifecycle})");
            else if (deprecating && r.Wip > 0)
                drifting.Add($"{r.Name} — {r.Wip} uncommitted in a DEPRECATING repo (churn in a wind-down)");

            // Needs-a-decision: lifecycle/state tensions the operator must resolve.
            if (deprecating && r.Wip > 0)
                needsCall.Add($"{r.Name}: {r.Wip} uncommitted but lifecycle is `{r.Lifecycle}` — finish, ship, or abandon?");
            if (r.DriftFlag)
                needsCall.Add($"{r.Name}: declared active but quiet {Age(r)} — still active, or update lifecycle?");

            // Next-slice candidates: active repos with live in-flight work, hottest first.
            if (!deprecating && r.Wip > 0 && r.Lifecycle.Contains("active", StringComparison.OrdinalIgnoreCase))
                nextSlice.Add($"{r.Name} — {r.Wip} WIP{(r.Recent.Count > 0 ? $" + {r.Recent.Count} commit(s) recently" : "")}{(r.Hot ? ", hot" : "")}");
        }

        // Fold the board-sync tiers into needs-a-decision (decisions + live-truth need a human).
        if (board is not null)
        {
            foreach (var d in board.Decisions)
                needsCall.Add($"board: {FirstLine(d)}");
            foreach (var t in board.LiveTruth)
                needsCall.Add($"board: {FirstLine(t)}");
        }

        var text = Render(glance.Count, changed, drifting, needsCall, nextSlice, board, recap);
        if (text.Length > overnight.MaxEvidenceChars)
            text = text[..overnight.MaxEvidenceChars] + "\n...(brief inputs truncated)";

        return new BriefInputs(text, glance.Count > 0, glance.Count, board, recap, glance);
    }

    private static string Describe(GlanceRepo r)
    {
        var bits = new List<string> { r.Lifecycle };
        if (r.Recent.Count > 0) bits.Add($"{r.Recent.Count} recent commit(s)");
        if (r.Wip > 0) bits.Add($"{r.Wip} uncommitted");
        if (r.Ahead) bits.Add("unpushed");
        bits.Add(r.DaysSinceCommit is 0 ? "committed today" : Age(r));
        return $"{r.Name} — {string.Join(", ", bits)}";
    }

    private static string Age(GlanceRepo r) =>
        r.DaysSinceCommit is { } d ? (d == 0 ? "today" : $"{d}d ago") : "age unknown";

    private static string FirstLine(string s)
    {
        var nl = s.IndexOf('\n');
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    private static string Render(
        int glanceCount, List<string> changed, List<string> drifting, List<string> needsCall,
        List<string> nextSlice, BoardSyncDelta? board, string? recap)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Morning brief — objective state — {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine(glanceCount > 0
            ? $"Source: constellation glance ({glanceCount} repos) + last Reflect recap. In-flight work is git-truth, not commit-age."
            : "_Constellation glance unavailable — objective state is incomplete; treat the brief as provisional._");
        sb.AppendLine();

        Section(sb, "Changed (committed or in-flight)", changed);
        Section(sb, "Drifting / sitting", drifting);
        Section(sb, "Needs your call", needsCall);
        Section(sb, "Next-slice candidates (author picks + justifies one)", nextSlice);

        sb.AppendLine("## Board reconciliation (pm/board-sync.md tiers)");
        if (board is null)
            sb.AppendLine("- board-sync unavailable — no board proposals this run.");
        else if (board.InSync)
            sb.AppendLine($"- IN SYNC — board, summary docs, markers all match the manifest ({board.ManifestRepos} repos).");
        else
        {
            sb.AppendLine($"- delta: {board.AutoSafe.Count} auto-safe · {board.Decisions.Count} decision(s) · "
                + $"{board.LiveTruth.Count} live-truth (ADO reachable: {(board.BoardAvailable ? "yes" : "NO — area/epic tiers skipped")})");
            foreach (var a in board.AutoSafe)
                sb.AppendLine($"  - [auto-safe] {FirstLine(a)}");
            foreach (var d in board.Decisions)
                sb.AppendLine($"  - [decision] {FirstLine(d)}");
            foreach (var t in board.LiveTruth)
                sb.AppendLine($"  - [live-truth] {FirstLine(t)}");
        }
        sb.AppendLine();

        sb.AppendLine("## Last Reflect recap (narrative context)");
        sb.AppendLine(string.IsNullOrWhiteSpace(recap) ? "- none on record." : recap.Trim());

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<string> items)
    {
        sb.AppendLine($"## {title}");
        if (items.Count == 0)
            sb.AppendLine("- (none)");
        else
            foreach (var i in items)
                sb.AppendLine($"- {i}");
        sb.AppendLine();
    }

    /// <summary>Newest dated markdown in the Reflect journal dir — last night's recap, soft.</summary>
    private string? ReadLatestRecap()
    {
        var dir = _options.Reflect.JournalDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return null;
        try
        {
            var newest = new DirectoryInfo(dir).EnumerateFiles("*.md")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is null)
                return null;
            var text = File.ReadAllText(newest.FullName).Trim();
            const int cap = 2500; // narrative context, not the whole recap
            return text.Length > cap ? text[..cap] + "\n...(recap truncated)" : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Overnight: could not read the latest Reflect recap from {Dir}.", dir);
            return null;
        }
    }
}
