using Ember.Config;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>
/// SQLite persistence for overnight runs. Shares the database file with the other stores but owns
/// the <c>briefs</c> table. The label flow reuses Reflect's reaction machinery — a brief posts a
/// label-request message and the operator's ✅/✏️/❌ reaction is recorded here by message id.
/// </summary>
public sealed class BriefStore
{
    private readonly string _connectionString;
    private readonly ILogger<BriefStore> _logger;

    public BriefStore(IOptions<EmberOptions> options, ILogger<BriefStore> logger)
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
            CREATE TABLE IF NOT EXISTS briefs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                date            TEXT    NOT NULL,
                thread_id       TEXT,
                glance_repos    INTEGER NOT NULL DEFAULT 0,
                evidence_chars  INTEGER NOT NULL DEFAULT 0,
                author_model    TEXT,
                critic_model    TEXT,
                brief           TEXT,
                critic_issues   TEXT,
                applied         TEXT,
                label           TEXT,
                message_id      TEXT,
                status          TEXT    NOT NULL,
                error           TEXT,
                created_at      INTEGER NOT NULL,
                updated_at      INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("BriefStore ready ({ConnectionString})", _connectionString);
    }

    /// <summary>Inserts a brief row and returns its id.</summary>
    public long Create(Brief brief)
    {
        var now = NowMs();
        brief.CreatedAt = now;
        brief.UpdatedAt = now;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO briefs
                (date, thread_id, glance_repos, evidence_chars, author_model, critic_model,
                 brief, critic_issues, applied, label, message_id, status, error,
                 created_at, updated_at)
            VALUES
                ($date, $thread_id, $glance_repos, $evidence_chars, $author_model, $critic_model,
                 $brief, $critic_issues, $applied, $label, $message_id, $status, $error,
                 $created_at, $updated_at);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, brief);
        cmd.Parameters.AddWithValue("$created_at", brief.CreatedAt);
        brief.Id = (long)cmd.ExecuteScalar()!;
        return brief.Id;
    }

    /// <summary>The date string of the most recent run of any status, or null when none exist.</summary>
    public string? LatestRunDate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date FROM briefs ORDER BY id DESC LIMIT 1;";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Records the operator's verdict from the reaction on a brief's label message.</summary>
    public bool SetLabel(string messageId, string label)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE briefs SET label = $label, updated_at = $updated_at
            WHERE message_id = $message_id;
            """;
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$updated_at", NowMs());
        cmd.Parameters.AddWithValue("$message_id", messageId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Bind(SqliteCommand cmd, Brief b)
    {
        cmd.Parameters.AddWithValue("$date", b.Date);
        cmd.Parameters.AddWithValue("$thread_id", (object?)b.ThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$glance_repos", b.GlanceRepos);
        cmd.Parameters.AddWithValue("$evidence_chars", b.EvidenceChars);
        cmd.Parameters.AddWithValue("$author_model", (object?)b.AuthorModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$critic_model", (object?)b.CriticModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$brief", (object?)b.BriefText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$critic_issues", (object?)b.CriticIssuesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$applied", (object?)b.AppliedJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$label", (object?)b.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$message_id", (object?)b.MessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", b.Status.ToString());
        cmd.Parameters.AddWithValue("$error", (object?)b.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", b.UpdatedAt);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
