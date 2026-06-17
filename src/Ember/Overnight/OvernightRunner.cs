using System.ClientModel;
using System.Diagnostics;
using System.Text;
using Ember.Config;
using Ember.Observability;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>Terminal status of one overnight run.</summary>
public enum BriefStatus
{
    /// <summary>A brief was produced (possibly degraded — see <see cref="OvernightOutcome.Degrade"/>).</summary>
    Ran,

    /// <summary>The run errored before any brief could be produced (e.g. evidence assembly threw).</summary>
    Failed,
}

/// <summary>Everything one overnight run produced, before posting / persistence / auto-apply.</summary>
public sealed class OvernightOutcome
{
    public BriefStatus Status { get; set; } = BriefStatus.Ran;

    public required BriefInputs Inputs { get; init; }

    /// <summary>The composed brief markdown (authored, or the raw objective state on author-down).</summary>
    public string Brief { get; set; } = "";

    public required string AuthorModel { get; init; }

    public required string CriticModel { get; init; }

    /// <summary>Issues the critic raised on the first draft (informational; the author revised against them).</summary>
    public IReadOnlyList<string> CriticIssues { get; set; } = Array.Empty<string>();

    /// <summary>Loud degradation notice (author/critic down). Null when both ran. Never silent.</summary>
    public string? Degrade { get; set; }

    public string? Error { get; set; }

    /// <summary>The composed post text, ready for Discord or the console.</summary>
    public string PostText { get; set; } = "";
}

/// <summary>
/// The overnight core: objective state → author drafts the brief → critic reviews → author
/// revises → composed post. No Discord, persistence, or auto-apply here — the executor owns those
/// (keeping the console runner read-only). Judges run on the local vllama clients with transient
/// retry; if the author is down the run still emits the raw objective state under a loud banner,
/// and if the critic is down the brief posts unreviewed (also stated) — never a silent gap.
/// </summary>
public sealed class OvernightRunner
{
    private readonly BriefAssembler _assembler;
    private readonly BriefAuthor _author;
    private readonly BriefCritic _critic;
    private readonly ModelsOptions _models;
    private readonly OvernightOptions _overnight;
    private readonly ILogger<OvernightRunner> _logger;

    public OvernightRunner(
        BriefAssembler assembler,
        [FromKeyedServices("reflectA")] IChatClient author,
        [FromKeyedServices("reflectB")] IChatClient critic,
        IOptions<ModelsOptions> models,
        IOptions<EmberOptions> ember,
        ILogger<OvernightRunner> logger)
    {
        _assembler = assembler;
        _author = new BriefAuthor(author);
        _critic = new BriefCritic(critic);
        _models = models.Value;
        _overnight = ember.Value.Overnight;
        _logger = logger;
    }

    /// <summary>
    /// Runs the overnight pipeline. With <paramref name="runJudges"/> false it stops after the
    /// objective state is assembled (the console <c>--dry-run</c> path — read-only, no models).
    /// </summary>
    public async Task<OvernightOutcome> PrepareAsync(bool runJudges, CancellationToken ct)
    {
        using var activity = Telemetry.Activity.StartActivity("overnight.session");

        BriefInputs inputs;
        using (Telemetry.Activity.StartActivity("overnight.assemble"))
        {
            inputs = await _assembler.AssembleAsync(ct);
        }
        activity?.SetTag("ember.overnight.evidence_chars", inputs.TotalChars);

        var outcome = new OvernightOutcome
        {
            Inputs = inputs,
            AuthorModel = _models.ReflectA.Model,
            CriticModel = _models.ReflectB.Model,
        };

        if (!runJudges)
        {
            outcome.Brief = inputs.Text;
            outcome.PostText = inputs.Text;
            return outcome;
        }

        var (draft, authorError) = await JudgeAsync(
            "author", outcome.AuthorModel, ct2 => _author.DraftAsync(inputs.Text, ct2), ct);

        if (draft is null)
        {
            // Author down: the read-only objective state is still useful — ship it loudly degraded.
            outcome.Brief = inputs.Text;
            outcome.Degrade =
                $"> ⚠️ **Degraded — author unavailable.** The brief author ({outcome.AuthorModel}) failed "
                + $"after retry ({authorError}). Below is the raw objective state — accurate, but un-synthesised.";
            outcome.PostText = Compose(outcome);
            return outcome;
        }

        // Critic review → one revision. Critic-down is non-fatal: the draft posts, said-so.
        var (issuesRaw, criticError) = await JudgeAsync(
            "critic", outcome.CriticModel,
            async ct2 => string.Join("\n", await _critic.ReviewAsync(inputs.Text, draft, ct2)), ct);

        var brief = draft;
        if (issuesRaw is null)
        {
            outcome.Degrade =
                $"> ⚠️ **Degraded — unreviewed.** The critic ({outcome.CriticModel}) failed after retry "
                + $"({criticError}); this brief was not cross-checked against the objective state.";
        }
        else
        {
            var issues = issuesRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            outcome.CriticIssues = issues;
            if (issues.Length > 0)
            {
                var (revised, _) = await JudgeAsync(
                    "author", outcome.AuthorModel,
                    ct2 => _author.ReviseAsync(inputs.Text, draft, issues, ct2), ct);
                if (revised is not null)
                    brief = revised;
            }
        }

        outcome.Brief = brief;
        outcome.PostText = Compose(outcome);
        return outcome;
    }

    /// <summary>One judge call with transient retry/backoff. Returns (result, error) — null result on failure.</summary>
    private async Task<(string? Result, string? Error)> JudgeAsync(
        string role, string model, Func<CancellationToken, Task<string>> call, CancellationToken ct)
    {
        var attempts = Math.Max(1, _overnight.JudgeMaxAttempts);
        string? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var activity = Telemetry.Activity.StartActivity("overnight.judge");
            activity?.SetTag("ember.overnight.role", role);
            activity?.SetTag("ember.overnight.model", model);
            activity?.SetTag("ember.overnight.attempt", attempt);
            try
            {
                var result = await call(ct);
                if (string.IsNullOrWhiteSpace(result) && role == "author")
                    throw new InvalidOperationException("author returned empty output");
                return (result, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                if (attempt < attempts && IsTransient(ex))
                {
                    var delay = TimeSpan.FromSeconds(_overnight.JudgeRetryBaseSeconds * Math.Pow(2, attempt - 1));
                    _logger.LogWarning(
                        "Overnight {Role} ({Model}) attempt {Attempt}/{Max} failed ({Error}); retrying in {Delay}s.",
                        role, model, attempt, attempts, ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }
                _logger.LogWarning(ex, "Overnight {Role} ({Model}) exhausted {Attempts} attempt(s).", role, model, attempt);
                break;
            }
        }
        return (null, lastError);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status is 0 or 408 or 429 or >= 500,
        TimeoutException => true,
        TaskCanceledException => true,
        HttpRequestException => true,
        _ => false,
    };

    private static string Compose(OvernightOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Morning brief — {DateTime.Now:yyyy-MM-dd}** · author {outcome.AuthorModel} / critic {outcome.CriticModel}");
        if (!outcome.Inputs.GlanceAvailable)
            sb.AppendLine("_Glance unavailable — objective state incomplete; brief is provisional._");
        sb.AppendLine();

        if (outcome.Degrade is not null)
        {
            sb.AppendLine(outcome.Degrade);
            sb.AppendLine();
        }

        sb.AppendLine(outcome.Brief.Trim());
        return sb.ToString().TrimEnd();
    }
}
