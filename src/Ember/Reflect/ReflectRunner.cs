using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ember.Config;
using Ember.Observability;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>Everything one reflect run produced, before any posting or persistence.</summary>
public sealed class ReflectOutcome
{
    public RecapStatus Status { get; set; } = RecapStatus.Ran;

    public required EvidenceBundle Evidence { get; init; }

    public string? RecapA { get; set; }

    public string? RecapB { get; set; }

    public ComparisonResult? Comparison { get; set; }

    public string? Error { get; set; }

    public required string JudgeAModel { get; init; }

    public required string JudgeBModel { get; init; }

    /// <summary>Grounding score "valid/total" for each recap's evidence citations (null if no recap).</summary>
    public string? CitesA { get; set; }

    public string? CitesB { get; set; }

    /// <summary>The composed recap text, ready for Discord or the console.</summary>
    public string PostText { get; set; } = "";

    /// <summary>JSON array of the changed repo keys.</summary>
    public string ReposJson =>
        JsonSerializer.Serialize(Evidence.Repos.Where(r => r.HasChanges).Select(r => r.Repo));
}

/// <summary>
/// The reflect core: evidence → two independent judges (concurrently) → divergence
/// extraction → composed post. No Discord and no persistence here — the executor owns
/// those, which keeps the console runner read-only.
/// </summary>
public sealed class ReflectRunner
{
    private readonly EvidenceAssembler _evidence;
    private readonly RecapJudge _judgeA;
    private readonly RecapJudge _judgeB;
    private readonly DivergenceComparer _comparer;
    private readonly ModelsOptions _models;
    private readonly ILogger<ReflectRunner> _logger;

    public ReflectRunner(
        EvidenceAssembler evidence,
        [FromKeyedServices("reflectA")] IChatClient judgeA,
        [FromKeyedServices("reflectB")] IChatClient judgeB,
        DivergenceComparer comparer,
        IOptions<ModelsOptions> models,
        ILogger<ReflectRunner> logger)
    {
        _evidence = evidence;
        _judgeA = new RecapJudge(judgeA, "A");
        _judgeB = new RecapJudge(judgeB, "B");
        _comparer = comparer;
        _models = models.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the reflect pipeline. With <paramref name="runJudges"/> false it stops after
    /// evidence assembly (the console <c>--dry-run</c> path).
    /// </summary>
    public async Task<ReflectOutcome> PrepareAsync(
        BaselineMode mode, bool runJudges, CancellationToken ct)
    {
        using var activity = Telemetry.Activity.StartActivity("reflect.session");

        EvidenceBundle evidence;
        using (Telemetry.Activity.StartActivity("reflect.evidence"))
        {
            evidence = await _evidence.AssembleAsync(mode, ct);
        }
        activity?.SetTag("ember.reflect.evidence_chars", evidence.TotalChars);

        var outcome = new ReflectOutcome
        {
            Evidence = evidence,
            JudgeAModel = _models.ReflectA.Model,
            JudgeBModel = _models.ReflectB.Model,
        };

        var changed = evidence.Repos.Count(r => r.HasChanges);
        activity?.SetTag("ember.reflect.repos_changed", changed);
        if (changed == 0)
        {
            outcome.Status = RecapStatus.Skipped;
            outcome.PostText = "No committed changes since the last recap.";
            return outcome;
        }

        if (!runJudges)
        {
            outcome.PostText = evidence.Text;
            return outcome;
        }

        var (rawA, errorA) = await JudgeAsync(_judgeA, outcome.JudgeAModel, evidence.Text, ct);
        var (rawB, errorB) = await JudgeAsync(_judgeB, outcome.JudgeBModel, evidence.Text, ct);

        // Recaps arrive as XML with per-claim <from> citations (ADR 16 / EXP-0001). Render to
        // readable markdown for the post and the comparison, and score grounding by checking
        // each citation against the evidence — the near-free trust signal the experiment surfaced.
        var mdA = rawA is null ? null : RecapXml.Render(rawA);
        var mdB = rawB is null ? null : RecapXml.Render(rawB);
        outcome.RecapA = mdA;
        outcome.RecapB = mdB;
        if (rawA is not null)
        {
            var (valid, total) = RecapXml.CountCitations(rawA, evidence.Text);
            outcome.CitesA = $"{valid}/{total}";
        }
        if (rawB is not null)
        {
            var (valid, total) = RecapXml.CountCitations(rawB, evidence.Text);
            outcome.CitesB = $"{valid}/{total}";
        }

        if (mdA is null && mdB is null)
        {
            outcome.Status = RecapStatus.Failed;
            outcome.Error = $"both judges failed — A: {errorA}; B: {errorB}";
            activity?.SetStatus(ActivityStatusCode.Error, outcome.Error);
            return outcome;
        }

        if (mdA is not null && mdB is not null)
        {
            using (Telemetry.Activity.StartActivity("reflect.compare"))
            {
                outcome.Comparison = await _comparer.CompareAsync(mdA, mdB, ct);
            }
        }

        outcome.PostText = Compose(outcome, errorA ?? errorB);
        return outcome;
    }

    private async Task<(string? Recap, string? Error)> JudgeAsync(
        RecapJudge judge, string model, string evidence, CancellationToken ct)
    {
        using var activity = Telemetry.Activity.StartActivity("reflect.judge");
        activity?.SetTag("ember.reflect.judge", judge.Label);
        activity?.SetTag("ember.reflect.model", model);
        try
        {
            var recap = await judge.WriteAsync(evidence, ct);
            if (string.IsNullOrWhiteSpace(recap))
                throw new InvalidOperationException("judge returned empty output");
            return (recap, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex, "Reflect judge {Judge} ({Model}) failed.", judge.Label, model);
            return (null, ex.Message);
        }
    }

    private static string Compose(ReflectOutcome outcome, string? judgeError)
    {
        var changed = outcome.Evidence.Repos.Where(r => r.HasChanges).Select(r => r.Repo).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"**Reflect — {DateTime.Now:yyyy-MM-dd}** · {string.Join(", ", changed)}");
        if (outcome.CitesA is not null || outcome.CitesB is not null)
            sb.AppendLine($"_Grounding (claims cited to evidence) — A: {outcome.CitesA ?? "-"}, B: {outcome.CitesB ?? "-"}._");
        sb.AppendLine();

        if (outcome.RecapA is not null)
        {
            sb.AppendLine($"**Recap A** ({outcome.JudgeAModel})");
            sb.AppendLine(outcome.RecapA.Trim());
            sb.AppendLine();
        }
        if (outcome.RecapB is not null)
        {
            sb.AppendLine($"**Recap B** ({outcome.JudgeBModel})");
            sb.AppendLine(outcome.RecapB.Trim());
            sb.AppendLine();
        }
        if (judgeError is not null)
            sb.AppendLine($"_One judge failed ({judgeError}) — single-perspective recap._\n");

        if (outcome.Comparison is { } cmp)
        {
            if (cmp.Agreements.Count > 0)
            {
                sb.AppendLine("**Agreement**");
                foreach (var a in cmp.Agreements)
                    sb.AppendLine($"- {a}");
                sb.AppendLine();
            }
            if (cmp.Divergences.Count > 0)
            {
                sb.AppendLine("**Divergences — worth a look**");
                foreach (var d in cmp.Divergences)
                {
                    var kind = string.IsNullOrWhiteSpace(d.Kind) ? "" : $" _{d.Kind}_";
                    sb.AppendLine($"- **{d.Topic}**{kind} — A: {d.ASays} / B: {d.BSays}");
                }
                sb.AppendLine();
            }
            else if (cmp.Agreements.Count > 0)
            {
                sb.AppendLine("_No meaningful divergences._");
            }
        }
        else if (outcome.RecapA is not null && outcome.RecapB is not null)
        {
            sb.AppendLine("_Comparison unavailable — read both recaps._");
        }

        return sb.ToString().TrimEnd();
    }
}
