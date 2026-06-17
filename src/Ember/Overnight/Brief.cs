namespace Ember.Overnight;

/// <summary>One overnight run. Mirrors the <c>briefs</c> SQLite table.</summary>
public sealed class Brief
{
    public long Id { get; set; }

    /// <summary>Local calendar date of the run, <c>yyyy-MM-dd</c>.</summary>
    public required string Date { get; set; }

    /// <summary>Discord thread the brief was posted to (null pre-post or for console runs).</summary>
    public string? ThreadId { get; set; }

    public int GlanceRepos { get; set; }

    public int EvidenceChars { get; set; }

    public string? AuthorModel { get; set; }

    public string? CriticModel { get; set; }

    /// <summary>The composed brief markdown.</summary>
    public string? BriefText { get; set; }

    /// <summary>JSON array of the critic's issues on the first draft.</summary>
    public string? CriticIssuesJson { get; set; }

    /// <summary>JSON array of auto-safe items applied this run (E3).</summary>
    public string? AppliedJson { get; set; }

    /// <summary>Operator label — reuses <see cref="Reflect.RecapLabels"/>. Null until reacted.</summary>
    public string? Label { get; set; }

    /// <summary>Id of the label-request message whose reactions carry the verdict.</summary>
    public string? MessageId { get; set; }

    public BriefStatus Status { get; set; } = BriefStatus.Ran;

    public string? Error { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
