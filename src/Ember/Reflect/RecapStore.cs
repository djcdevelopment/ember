using Ember.Config;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>
/// SQLite persistence for reflect runs and per-repo baselines. Shares the database file with
/// <see cref="Sessions.SessionStore"/> but owns its own tables.
/// </summary>
public sealed class RecapStore
{
    private readonly string _connectionString;
    private readonly ILogger<RecapStore> _logger;

    public RecapStore(IOptions<EmberOptions> options, ILogger<RecapStore> logger)
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
            CREATE TABLE IF NOT EXISTS recaps (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                date             TEXT    NOT NULL,
                thread_id        TEXT,
                repos            TEXT    NOT NULL,
                evidence_chars   INTEGER NOT NULL DEFAULT 0,
                judge_a_model    TEXT,
                judge_b_model    TEXT,
                recap_a          TEXT,
                recap_b          TEXT,
                divergences      TEXT,
                label            TEXT,
                message_id       TEXT,
                status           TEXT    NOT NULL,
                error            TEXT,
                created_at       INTEGER NOT NULL,
                updated_at       INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS repo_reflect_state (
                repo        TEXT PRIMARY KEY,
                last_sha    TEXT NOT NULL,
                last_run_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("RecapStore ready ({ConnectionString})", _connectionString);
    }

    /// <summary>Inserts a recap row and returns its id.</summary>
    public long Create(Recap recap)
    {
        var now = NowMs();
        recap.CreatedAt = now;
        recap.UpdatedAt = now;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO recaps
                (date, thread_id, repos, evidence_chars, judge_a_model, judge_b_model,
                 recap_a, recap_b, divergences, label, message_id, status, error,
                 created_at, updated_at)
            VALUES
                ($date, $thread_id, $repos, $evidence_chars, $judge_a_model, $judge_b_model,
                 $recap_a, $recap_b, $divergences, $label, $message_id, $status, $error,
                 $created_at, $updated_at);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, recap);
        cmd.Parameters.AddWithValue("$created_at", recap.CreatedAt);
        recap.Id = (long)cmd.ExecuteScalar()!;
        return recap.Id;
    }

    /// <summary>The date string of the most recent run of any status, or null when none exist.</summary>
    public string? LatestRunDate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date FROM recaps ORDER BY id DESC LIMIT 1;";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Records the operator's verdict from the reaction on a recap's label-request message.
    /// Returns true when a recap row matched the message id.
    /// </summary>
    public bool SetLabel(string messageId, string label)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE recaps SET label = $label, updated_at = $updated_at
            WHERE message_id = $message_id;
            """;
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$updated_at", NowMs());
        cmd.Parameters.AddWithValue("$message_id", messageId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Per-repo last-recapped commit shas.</summary>
    public Dictionary<string, string> GetShas()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT repo, last_sha FROM repo_reflect_state;";

        var shas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            shas[reader.GetString(0)] = reader.GetString(1);
        return shas;
    }

    /// <summary>Advances one repo's baseline to the given sha.</summary>
    public void SetSha(string repo, string sha)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO repo_reflect_state (repo, last_sha, last_run_at)
            VALUES ($repo, $sha, $now)
            ON CONFLICT(repo) DO UPDATE SET last_sha = $sha, last_run_at = $now;
            """;
        cmd.Parameters.AddWithValue("$repo", repo);
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.Parameters.AddWithValue("$now", NowMs());
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Bind(SqliteCommand cmd, Recap r)
    {
        cmd.Parameters.AddWithValue("$date", r.Date);
        cmd.Parameters.AddWithValue("$thread_id", (object?)r.ThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repos", r.ReposJson);
        cmd.Parameters.AddWithValue("$evidence_chars", r.EvidenceChars);
        cmd.Parameters.AddWithValue("$judge_a_model", (object?)r.JudgeAModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$judge_b_model", (object?)r.JudgeBModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$recap_a", (object?)r.RecapA ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$recap_b", (object?)r.RecapB ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$divergences", (object?)r.DivergencesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$label", (object?)r.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$message_id", (object?)r.MessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", r.Status.ToString());
        cmd.Parameters.AddWithValue("$error", (object?)r.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", r.UpdatedAt);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
