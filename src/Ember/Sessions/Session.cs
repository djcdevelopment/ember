namespace Ember.Sessions;

/// <summary>Lifecycle state of a planning session. One session maps to one Discord thread.</summary>
public enum SessionState
{
    Planning,
    AwaitingGate,
    Building,
    PrOpen,
    Aborted,
    Failed,
}

/// <summary>Why the planning loop handed off to the gate.</summary>
public enum GateReason
{
    /// <summary>Critic raised no blocking or major issues.</summary>
    Approved,

    /// <summary>Hit the round-count backstop with issues still open.</summary>
    RoundCap,

    /// <summary>A round closed zero issues — the loop stopped making progress.</summary>
    Stalled,
}

/// <summary>A planning session row. Mirrors the <c>sessions</c> SQLite table.</summary>
public sealed class Session
{
    /// <summary>Discord thread id — the primary key and session id.</summary>
    public required string ThreadId { get; set; }

    public SessionState State { get; set; } = SessionState.Planning;

    public GateReason? GateReason { get; set; }

    /// <summary>Allowlist key of the target repo.</summary>
    public required string Repo { get; set; }

    public required string Brief { get; set; }

    public int CurrentRound { get; set; }

    /// <summary>Frozen converged plan, set on gate entry.</summary>
    public string? PlanSnapshot { get; set; }

    /// <summary>JSON array of critic issues still open at gate time.</summary>
    public string? OpenIssues { get; set; }

    /// <summary>Epoch milliseconds at which the soft gate elapses.</summary>
    public long? GateExpiresAt { get; set; }

    public string? BranchName { get; set; }

    public string? WorktreePath { get; set; }

    public string? PrUrl { get; set; }

    public string? LastError { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
