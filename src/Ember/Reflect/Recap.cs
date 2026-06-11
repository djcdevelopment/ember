namespace Ember.Reflect;

/// <summary>Terminal status of one reflect run.</summary>
public enum RecapStatus
{
    /// <summary>Evidence existed; judges ran; the recap was produced.</summary>
    Ran,

    /// <summary>No repo changed since the last recap (or first-run baselines were recorded).</summary>
    Skipped,

    /// <summary>The run errored — evidence, judges, or posting. Baselines were not advanced.</summary>
    Failed,
}

/// <summary>Operator verdict on a recap, captured from the reaction on the recap message.</summary>
public static class RecapLabels
{
    public const string Accurate = "accurate";
    public const string Partial = "partial";
    public const string Wrong = "wrong";
}

/// <summary>One reflect run. Mirrors the <c>recaps</c> SQLite table.</summary>
public sealed class Recap
{
    public long Id { get; set; }

    /// <summary>Local calendar date of the run, <c>yyyy-MM-dd</c>.</summary>
    public required string Date { get; set; }

    /// <summary>Discord thread the recap was posted to (null pre-post or for console runs).</summary>
    public string? ThreadId { get; set; }

    /// <summary>JSON array of repo keys that had changes in this run.</summary>
    public string ReposJson { get; set; } = "[]";

    public int EvidenceChars { get; set; }

    public string? JudgeAModel { get; set; }

    public string? JudgeBModel { get; set; }

    public string? RecapA { get; set; }

    public string? RecapB { get; set; }

    /// <summary>JSON of the structured comparison (agreements + divergences), when produced.</summary>
    public string? DivergencesJson { get; set; }

    /// <summary>Operator label — see <see cref="RecapLabels"/>. Null until reacted.</summary>
    public string? Label { get; set; }

    /// <summary>Id of the label-request message whose reactions carry the verdict.</summary>
    public string? MessageId { get; set; }

    public RecapStatus Status { get; set; } = RecapStatus.Ran;

    public string? Error { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
