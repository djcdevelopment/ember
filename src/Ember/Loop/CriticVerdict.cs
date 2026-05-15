namespace Ember.Loop;

/// <summary>Severity of a critic-raised issue. Blocking/Major keep the loop running; Minor does not.</summary>
public enum IssueSeverity
{
    Blocking,
    Major,
    Minor,
}

/// <summary>A single issue the critic raised against a plan.</summary>
public sealed class CriticIssue
{
    public IssueSeverity Severity { get; set; }
    public string Summary { get; set; } = "";
    public string Fix { get; set; } = "";
}

/// <summary>The critic's structured review of a plan revision.</summary>
public sealed class CriticVerdict
{
    /// <summary>One-line overall read from the critic.</summary>
    public string Assessment { get; set; } = "";

    /// <summary>Every issue the critic raised, at any severity.</summary>
    public List<CriticIssue> Issues { get; set; } = new();

    /// <summary>Open (blocking or major) issues — these are what keep the loop running.</summary>
    public IReadOnlyList<CriticIssue> OpenIssues =>
        Issues.Where(i => i.Severity is IssueSeverity.Blocking or IssueSeverity.Major).ToList();

    /// <summary>True when no blocking or major issues remain.</summary>
    public bool Approved => OpenIssues.Count == 0;
}
