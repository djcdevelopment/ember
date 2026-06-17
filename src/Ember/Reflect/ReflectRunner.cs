using System.ClientModel;
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

    /// <summary>Model that actually produced each recap — differs from the configured model on failover.</summary>
    public string? JudgeAModelUsed { get; set; }

    public string? JudgeBModelUsed { get; set; }

    /// <summary>
    /// Loud degradation notice rendered at the top of the post when a judge was lost or
    /// failed over (RF2). Null when both judges answered on their own endpoint. Never silent.
    /// </summary>
    public string? Degrade { get; set; }

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
    private readonly ReflectOptions _reflect;
    private readonly ILogger<ReflectRunner> _logger;

    public ReflectRunner(
        EvidenceAssembler evidence,
        [FromKeyedServices("reflectA")] IChatClient judgeA,
        [FromKeyedServices("reflectB")] IChatClient judgeB,
        DivergenceComparer comparer,
        IOptions<ModelsOptions> models,
        IOptions<EmberOptions> ember,
        ILogger<ReflectRunner> logger)
    {
        _evidence = evidence;
        _judgeA = new RecapJudge(judgeA, "A");
        _judgeB = new RecapJudge(judgeB, "B");
        _comparer = comparer;
        _models = models.Value;
        _reflect = ember.Value.Reflect;
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

        // Each judge: retry its own endpoint on transient 503/timeout, then fail over to the
        // sibling's endpoint (the other card), so one down judge cannot silently gut the recap
        // (RF2 / ADR 18). The failover is loud — a cross-sourced recap is not independent.
        var runA = await RunResilientAsync(
            _judgeA, outcome.JudgeAModel, _judgeB, outcome.JudgeBModel, evidence.Text, ct);
        var runB = await RunResilientAsync(
            _judgeB, outcome.JudgeBModel, _judgeA, outcome.JudgeAModel, evidence.Text, ct);

        var rawA = runA.Recap;
        var rawB = runB.Recap;
        var errorA = runA.Error;
        var errorB = runB.Error;
        outcome.JudgeAModelUsed = runA.ModelUsed;
        outcome.JudgeBModelUsed = runB.ModelUsed;
        outcome.Degrade = BuildDegrade(runA, runB);

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

        outcome.PostText = Compose(outcome);
        return outcome;
    }

    /// <summary>One slot's outcome: the recap (or null), the model that produced it, and how.</summary>
    private sealed record JudgeRun(
        string Label, string? Recap, string? Error, string ModelUsed, bool FailedOver);

    /// <summary>
    /// Runs one judge slot resiliently: its own endpoint with transient-error retry/backoff,
    /// then a single failover attempt against the sibling endpoint. A slot returns its recap
    /// however it was produced, or a null recap with the combined error if every path failed —
    /// the survivor still runs on full evidence.
    /// </summary>
    private async Task<JudgeRun> RunResilientAsync(
        RecapJudge primary, string primaryModel,
        RecapJudge failover, string failoverModel,
        string evidence, CancellationToken ct)
    {
        var attempts = Math.Max(1, _reflect.JudgeMaxAttempts);
        string? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var activity = Telemetry.Activity.StartActivity("reflect.judge");
            activity?.SetTag("ember.reflect.judge", primary.Label);
            activity?.SetTag("ember.reflect.model", primaryModel);
            activity?.SetTag("ember.reflect.attempt", attempt);
            try
            {
                var recap = await primary.WriteAsync(evidence, ct);
                if (string.IsNullOrWhiteSpace(recap))
                    throw new InvalidOperationException("judge returned empty output");
                return new JudgeRun(primary.Label, recap, null, primaryModel, FailedOver: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                var transient = IsTransient(ex);
                if (attempt < attempts && transient)
                {
                    var delay = TimeSpan.FromSeconds(_reflect.JudgeRetryBaseSeconds * Math.Pow(2, attempt - 1));
                    _logger.LogWarning(
                        "Reflect judge {Judge} ({Model}) attempt {Attempt}/{Max} failed ({Error}); retrying in {Delay}s.",
                        primary.Label, primaryModel, attempt, attempts, ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }
                _logger.LogWarning(ex,
                    "Reflect judge {Judge} ({Model}) exhausted {Attempts} attempt(s).",
                    primary.Label, primaryModel, attempt);
                break;
            }
        }

        if (_reflect.JudgeFailover)
        {
            _logger.LogWarning(
                "Reflect judge {Judge}: failing over from {Primary} to {Failover}.",
                primary.Label, primaryModel, failoverModel);
            using var activity = Telemetry.Activity.StartActivity("reflect.judge.failover");
            activity?.SetTag("ember.reflect.judge", primary.Label);
            activity?.SetTag("ember.reflect.model", failoverModel);
            try
            {
                var recap = await failover.WriteAsync(evidence, ct);
                if (!string.IsNullOrWhiteSpace(recap))
                    return new JudgeRun(primary.Label, recap, null, failoverModel, FailedOver: true);
                lastError = $"{lastError}; failover ({failoverModel}) returned empty output";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex,
                    "Reflect judge {Judge}: failover to {Failover} also failed.", primary.Label, failoverModel);
                lastError = $"{lastError}; failover ({failoverModel}): {ex.Message}";
            }
        }

        return new JudgeRun(primary.Label, null, lastError, primaryModel, FailedOver: false);
    }

    /// <summary>
    /// Transient = worth a retry: a 5xx/429/408 from the facade (a slot still loading answers
    /// 503, ADR-0007), a timeout, or a transport hiccup. A 4xx contract error is not retried.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status is 0 or 408 or 429 or >= 500,
        TimeoutException => true,
        TaskCanceledException => true,
        HttpRequestException => true,
        _ => false,
    };

    /// <summary>Header label: the model, annotated when the recap came from a failover endpoint.</summary>
    private static string RecapLabel(string configured, string? used) =>
        used is not null && !used.Equals(configured, StringComparison.OrdinalIgnoreCase)
            ? $"{used} — failover from {configured}"
            : configured;

    /// <summary>The loud degrade banner — single judge, or a cross-sourced (failed-over) recap.</summary>
    private static string? BuildDegrade(JudgeRun a, JudgeRun b)
    {
        var down = new List<JudgeRun>();
        if (a.Recap is null) down.Add(a);
        if (b.Recap is null) down.Add(b);

        if (down.Count == 2)
            return null; // both down → the run fails elsewhere; no partial banner needed.

        if (down.Count == 1)
        {
            var d = down[0];
            return $"> ⚠️ **Degraded — single judge.** Judge {d.Label} failed after retry + failover "
                + $"({d.Error}). This recap is one perspective; cross-judge divergence is unavailable.";
        }

        var over = new List<JudgeRun>();
        if (a.FailedOver) over.Add(a);
        if (b.FailedOver) over.Add(b);
        if (over.Count > 0)
        {
            var which = string.Join(" and ", over.Select(o => $"Judge {o.Label} (now on {o.ModelUsed})"));
            return $"> ⚠️ **Degraded — failover.** {which} ran on a sibling endpoint after its own "
                + "endpoint failed. The two recaps are not fully independent; weigh divergences with that in mind.";
        }

        return null;
    }

    private static string Compose(ReflectOutcome outcome)
    {
        var changed = outcome.Evidence.Repos.Where(r => r.HasChanges).Select(r => r.Repo).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"**Reflect — {DateTime.Now:yyyy-MM-dd}** · {string.Join(", ", changed)}");
        if (outcome.CitesA is not null || outcome.CitesB is not null)
            sb.AppendLine($"_Grounding (claims cited to evidence) — A: {outcome.CitesA ?? "-"}, B: {outcome.CitesB ?? "-"}._");
        sb.AppendLine();

        // Degradation is stated loudly, up top — never a silent one-bullet recap (RF2).
        if (outcome.Degrade is not null)
        {
            sb.AppendLine(outcome.Degrade);
            sb.AppendLine();
        }

        if (outcome.RecapA is not null)
        {
            sb.AppendLine($"**Recap A** ({RecapLabel(outcome.JudgeAModel, outcome.JudgeAModelUsed)})");
            sb.AppendLine(outcome.RecapA.Trim());
            sb.AppendLine();
        }
        if (outcome.RecapB is not null)
        {
            sb.AppendLine($"**Recap B** ({RecapLabel(outcome.JudgeBModel, outcome.JudgeBModelUsed)})");
            sb.AppendLine(outcome.RecapB.Trim());
            sb.AppendLine();
        }

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
