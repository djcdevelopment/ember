using Ember.Config;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ember.Sessions;

/// <summary>SQLite-backed persistence for planning sessions.</summary>
public sealed class SessionStore
{
    private readonly string _connectionString;
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(IOptions<EmberOptions> options, ILogger<SessionStore> logger)
    {
        _logger = logger;
        var dbPath = Path.GetFullPath(options.Value.DatabasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    /// <summary>Creates the schema if it does not exist. Call once at startup.</summary>
    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS sessions (
                thread_id        TEXT    PRIMARY KEY,
                state            TEXT    NOT NULL,
                gate_reason      TEXT,
                repo             TEXT    NOT NULL,
                brief            TEXT    NOT NULL,
                current_round    INTEGER NOT NULL DEFAULT 0,
                plan_snapshot    TEXT,
                open_issues      TEXT,
                gate_expires_at  INTEGER,
                branch_name      TEXT,
                worktree_path    TEXT,
                pr_url           TEXT,
                last_error       TEXT,
                created_at       INTEGER NOT NULL,
                updated_at       INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("SessionStore ready ({ConnectionString})", _connectionString);
    }

    /// <summary>Inserts a new session.</summary>
    public void Create(Session session)
    {
        var now = NowMs();
        session.CreatedAt = now;
        session.UpdatedAt = now;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO sessions
                (thread_id, state, gate_reason, repo, brief, current_round, plan_snapshot,
                 open_issues, gate_expires_at, branch_name, worktree_path, pr_url, last_error,
                 created_at, updated_at)
            VALUES
                ($thread_id, $state, $gate_reason, $repo, $brief, $current_round, $plan_snapshot,
                 $open_issues, $gate_expires_at, $branch_name, $worktree_path, $pr_url, $last_error,
                 $created_at, $updated_at);
            """;
        Bind(cmd, session);
        cmd.Parameters.AddWithValue("$created_at", session.CreatedAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the session for a thread, or null if there is none.</summary>
    public Session? Get(string threadId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE thread_id = $thread_id;";
        cmd.Parameters.AddWithValue("$thread_id", threadId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Returns every session currently in the given state.</summary>
    public IReadOnlyList<Session> GetByState(SessionState state)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE state = $state;";
        cmd.Parameters.AddWithValue("$state", state.ToString());

        using var reader = cmd.ExecuteReader();
        var results = new List<Session>();
        while (reader.Read())
            results.Add(Map(reader));
        return results;
    }

    /// <summary>Persists all mutable fields of an existing session.</summary>
    public void Update(Session session)
    {
        session.UpdatedAt = NowMs();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE sessions SET
                state           = $state,
                gate_reason     = $gate_reason,
                repo            = $repo,
                brief           = $brief,
                current_round   = $current_round,
                plan_snapshot   = $plan_snapshot,
                open_issues     = $open_issues,
                gate_expires_at = $gate_expires_at,
                branch_name     = $branch_name,
                worktree_path   = $worktree_path,
                pr_url          = $pr_url,
                last_error      = $last_error,
                updated_at      = $updated_at
            WHERE thread_id = $thread_id;
            """;
        Bind(cmd, session);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Bind(SqliteCommand cmd, Session s)
    {
        cmd.Parameters.AddWithValue("$thread_id", s.ThreadId);
        cmd.Parameters.AddWithValue("$state", s.State.ToString());
        cmd.Parameters.AddWithValue("$gate_reason", (object?)s.GateReason?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo", s.Repo);
        cmd.Parameters.AddWithValue("$brief", s.Brief);
        cmd.Parameters.AddWithValue("$current_round", s.CurrentRound);
        cmd.Parameters.AddWithValue("$plan_snapshot", (object?)s.PlanSnapshot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$open_issues", (object?)s.OpenIssues ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gate_expires_at", (object?)s.GateExpiresAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch_name", (object?)s.BranchName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$worktree_path", (object?)s.WorktreePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr_url", (object?)s.PrUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_error", (object?)s.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
    }

    private static Session Map(SqliteDataReader r) => new()
    {
        ThreadId = r.GetString(r.GetOrdinal("thread_id")),
        State = Enum.Parse<SessionState>(r.GetString(r.GetOrdinal("state"))),
        GateReason = r["gate_reason"] is string gr ? Enum.Parse<GateReason>(gr) : null,
        Repo = r.GetString(r.GetOrdinal("repo")),
        Brief = r.GetString(r.GetOrdinal("brief")),
        CurrentRound = (int)r.GetInt64(r.GetOrdinal("current_round")),
        PlanSnapshot = r["plan_snapshot"] as string,
        OpenIssues = r["open_issues"] as string,
        GateExpiresAt = r["gate_expires_at"] is long ge ? ge : null,
        BranchName = r["branch_name"] as string,
        WorktreePath = r["worktree_path"] as string,
        PrUrl = r["pr_url"] as string,
        LastError = r["last_error"] as string,
        CreatedAt = r.GetInt64(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("updated_at")),
    };

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
