namespace Ember.Config;

/// <summary>Discord connection and authorization settings.</summary>
public sealed class DiscordOptions
{
    public const string Section = "Discord";

    /// <summary>Bot token. Secret — set via user-secrets or environment, never appsettings.json.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Application (client) id.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Guild the bot registers its commands to (the personal server).</summary>
    public string GuildId { get; set; } = "";

    /// <summary>The single operator allowed to invoke commands.</summary>
    public string OwnerUserId { get; set; } = "";
}

/// <summary>Core ember behaviour settings.</summary>
public sealed class EmberOptions
{
    public const string Section = "Ember";

    /// <summary>SQLite database path; relative paths resolve against the working directory.</summary>
    public string DatabasePath { get; set; } = "ember.db";

    /// <summary>Soft-gate countdown length, in seconds.</summary>
    public int GateCountdownSeconds { get; set; } = 300;

    /// <summary>Hard backstop on planning-loop rounds.</summary>
    public int MaxPlanRounds { get; set; } = 6;

    /// <summary>
    /// Directory under which per-build git worktrees are created. Relative paths resolve
    /// against the working directory (like <see cref="DatabasePath"/>).
    /// </summary>
    public string WorktreeRoot { get; set; } = "worktrees";

    /// <summary>
    /// Days to keep the worktree of a finished-but-not-PR'd build (FAILED / ABORTED) before
    /// boot-time cleanup removes it. The branch is always kept. 0 disables cleanup.
    /// </summary>
    public int WorktreeRetentionDays { get; set; } = 7;

    /// <summary>Headless-builder settings.</summary>
    public BuilderOptions Builder { get; set; } = new();

    /// <summary>Constellation-manifest consumer settings (Slice B of v1.5).</summary>
    public ManifestOptions Manifest { get; set; } = new();

    /// <summary>Code-knowledge-graph consumer settings (codebase-memory-mcp; ADR 13).</summary>
    public GraphOptions Graph { get; set; } = new();

    /// <summary>Reflect (dual-judge recap) settings (ADR 14).</summary>
    public ReflectOptions Reflect { get; set; } = new();

    /// <summary>Overnight backlog-planner settings — the morning brief + PM reconciliation (ADR 19).</summary>
    public OvernightOptions Overnight { get; set; } = new();

    /// <summary>
    /// Allowlisted repos: key -> <see cref="RepoEntry"/>. Each entry has an absolute host path
    /// and, optionally, the path to a constellation manifest the planner should read as round-1
    /// context. Config parses either the legacy string shape (<c>"name": "C:\\path"</c>) or the
    /// extended object shape (<c>"name": { "path": "C:\\path", "constellation": "C:\\..." }</c>) —
    /// see <c>Config/EmberOptionsPostConfigure</c>.
    /// </summary>
    public Dictionary<string, RepoEntry> Repos { get; set; } = new();
}

/// <summary>
/// One entry in the <see cref="EmberOptions.Repos"/> allowlist. The path is the absolute
/// host directory ember targets for this key; the optional constellation path points the
/// planner at the manifest framework's <c>constellation.yaml</c> for that repo. Both shapes
/// in appsettings.json bind to this record — see <c>Config/EmberOptionsPostConfigure</c>.
/// </summary>
public sealed class RepoEntry
{
    /// <summary>Absolute host path of the target repo.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Optional absolute path to a constellation manifest (e.g. <c>D:\\work\\gad\\constellation.yaml</c>)
    /// whose validated JSON projection is folded into the round-1 planner prompt. <c>null</c> /
    /// missing means "this repo has no constellation context" — the planner sees only the
    /// file-system context, as before.
    /// </summary>
    public string? Constellation { get; set; }
}

/// <summary>
/// How ember invokes the constellation-manifest framework to load a manifest. The framework's
/// consumer contract (<c>MANIFEST-CONSUMER-PATTERN.md</c>) accepts both <c>framework</c> on
/// PATH and <c>python -m constellation_manifest.cli</c>; this lets the operator pick. ember
/// invokes <c>{Command} {ExtraArgs...} load &lt;path&gt; --json</c>.
/// </summary>
public sealed class ManifestOptions
{
    /// <summary>
    /// Executable. Default <c>framework</c> assumes the console-script is on PATH; set this to
    /// <c>python</c> with <see cref="ExtraArgs"/> = <c>["-m", "constellation_manifest.cli"]</c>
    /// when the script is not on PATH.
    /// </summary>
    public string Command { get; set; } = "framework";

    /// <summary>Args inserted between the command and the <c>load …</c> args. Useful for <c>-m</c>.</summary>
    public List<string> ExtraArgs { get; set; } = new();

    /// <summary>Kill the framework subprocess if it runs longer than this. Defaults to 30s.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// The maximum manifest <c>schema_version</c> this consumer knows how to read. A newer one
    /// is treated like any other read failure: warn and proceed without manifest context. Slice
    /// B is built against schema_version 1; bump when the consumer is updated to a new shape.
    /// </summary>
    public int MaxSchemaVersion { get; set; } = 1;
}

/// <summary>
/// How ember invokes the codebase-memory-mcp CLI to read the local code knowledge graph.
/// ember invokes <c>{Command} {ExtraArgs...} cli &lt;tool&gt; &lt;json&gt;</c>. Failures are
/// uniformly soft — the planner/critic run without graph context, exactly like the
/// manifest seam (ADR 13 mirrors ADR 11's posture).
/// </summary>
public sealed class GraphOptions
{
    /// <summary>Master switch. Off skips the subprocess entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Executable. The npm global install ships only <c>.cmd</c>/shell shims, which
    /// <c>Process.Start</c> cannot launch directly — point this at the real
    /// <c>codebase-memory-mcp.exe</c> inside the npm package.
    /// </summary>
    public string Command { get; set; } = "codebase-memory-mcp";

    /// <summary>Args inserted between the command and <c>cli &lt;tool&gt; &lt;json&gt;</c>.</summary>
    public List<string> ExtraArgs { get; set; } = new();

    /// <summary>
    /// Value for the <c>CBM_CACHE_DIR</c> environment variable of the subprocess — where the
    /// graph SQLite files live. Empty inherits the parent environment.
    /// </summary>
    public string? CacheDir { get; set; }

    /// <summary>Kill the CLI subprocess if it runs longer than this. Defaults to 30s.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Hard cap on the graph-context section folded into the round-1 prompt.</summary>
    public int MaxChars { get; set; } = 4000;

    /// <summary>
    /// Re-index a repo before reading its symbols. The background watcher does not reliably
    /// keep the graph fresh across sessions (ADR 15; ember sat 6 days stale on 2026-06-17),
    /// so reads that must be correct — Reflect's evidence enrichment — re-index just-in-time.
    /// On disables it for fast interactive iteration.
    /// </summary>
    public bool ReindexBeforeRead { get; set; } = true;
}

/// <summary>
/// The Reflect subsystem: a scheduled dual-judge recap of the constellation's committed work
/// since the last recap (ADR 14). Disabled by default — enabling it and choosing the channel
/// is an explicit operator action.
/// </summary>
public sealed class ReflectOptions
{
    /// <summary>Master switch. Off: the hosted service idles and <c>/reflect</c> refuses.</summary>
    public bool Enabled { get; set; }

    /// <summary>Channel the nightly recap threads are created in.</summary>
    public string ChannelId { get; set; } = "";

    /// <summary>
    /// Whether the nightly auto-run fires. True (default) runs at <see cref="RunAtLocalTime"/>;
    /// false is "manual-only" — Reflect stays enabled for <c>/reflect</c> and the launcher
    /// trigger, but nothing fires unattended (ADR 17). On a single-operator rig that also games
    /// and streams on the same GPUs, an unattended 03:00 run is opt-in, not default.
    /// </summary>
    public bool ScheduleEnabled { get; set; } = true;

    /// <summary>Local time of day the scheduled run fires (24h <c>HH:mm</c>), when <see cref="ScheduleEnabled"/>.</summary>
    public string RunAtLocalTime { get; set; } = "03:00";

    /// <summary>Per-repo cap on commits listed in the evidence.</summary>
    public int MaxCommitsPerRepo { get; set; } = 20;

    /// <summary>Per-repo cap on changed files listed in the evidence.</summary>
    public int MaxFilesPerRepo { get; set; } = 40;

    /// <summary>Per-repo cap on evidence characters.</summary>
    public int MaxEvidenceCharsPerRepo { get; set; } = 3000;

    /// <summary>Cap on the whole evidence bundle handed to each judge.</summary>
    public int MaxTotalEvidenceChars { get; set; } = 16000;

    /// <summary>
    /// Absolute directory for the dated recap markdown artifact (e.g.
    /// <c>D:\\work\\gad\\pm\\journal\\reflect</c>). Empty disables journaling. The git trail
    /// is what makes a cron/on-demand run's history durable (ADR 15).
    /// </summary>
    public string JournalDir { get; set; } = "";

    /// <summary>
    /// Commit the journal artifact in its repo after writing it (additive, by-name). Off by
    /// default — observe the written files first, then enable for the unattended cron run.
    /// </summary>
    public bool CommitArtifacts { get; set; }

    /// <summary>
    /// Port for the loopback-only "run now" trigger (127.0.0.1) the desktop launcher pokes to
    /// start a recap without Discord (ADR 17). 0 disables the listener (default), matching
    /// Reflect's disabled-by-default posture; set a free port (e.g. 8091) to enable. The run it
    /// starts is identical to <c>/reflect</c> and is still gated by <see cref="Enabled"/>.
    /// </summary>
    public int LocalTriggerPort { get; set; }

    /// <summary>Constellation-glance evidence settings (ADR 18). The glance is the primary read.</summary>
    public GlanceOptions Glance { get; set; } = new();

    /// <summary>
    /// Attempts per judge against its primary endpoint before failover, on transient errors
    /// (503/timeout/5xx). 1 disables retry. The vllama facade may answer 503 while a slot is
    /// still loading (ADR-0007); a short retry rides that out (ADR 18 / RF2).
    /// </summary>
    public int JudgeMaxAttempts { get; set; } = 3;

    /// <summary>Base backoff between judge attempts, seconds; doubles each retry.</summary>
    public int JudgeRetryBaseSeconds { get; set; } = 3;

    /// <summary>
    /// When a judge's primary endpoint is down after retries, try the sibling judge's endpoint
    /// once (the other card) so the slot still produces a recap. The cross-sourced recap is
    /// labelled loudly — it is not an independent second perspective (ADR 18 / RF2).
    /// </summary>
    public bool JudgeFailover { get; set; } = true;
}

/// <summary>
/// How Reflect invokes the constellation-glance script (<c>constellation-glance.py --json</c>)
/// to read the cross-repo working-tree state — uncommitted WIP, branch/unpushed, lifecycle,
/// and drift. This is Reflect's <em>primary</em> evidence (ADR 18): in-flight work the
/// commit-delta is structurally blind to. Failures are soft, like the manifest/graph seams —
/// Reflect falls back to the commit-led read and notes the glance was unavailable.
/// </summary>
public sealed class GlanceOptions
{
    /// <summary>Master switch. Off skips the subprocess and Reflect reads commits only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Interpreter/executable. Default <c>python</c> for the <c>.py</c> script.</summary>
    public string Command { get; set; } = "python";

    /// <summary>
    /// Absolute path to <c>constellation-glance.py</c>. Empty disables the glance (the script
    /// lives outside ember, in <c>gad/pm/scripts</c>), so an operator without it degrades
    /// cleanly to the commit-led read.
    /// </summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>Args inserted before <c>--json</c> (e.g. a custom <c>--since</c> window).</summary>
    public List<string> ExtraArgs { get; set; } = new();

    /// <summary>Kill the glance subprocess if it runs longer than this. Defaults to 60s.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// The overnight backlog planner (ADR 19): an operator-triggered run that reads the objective
/// state (the glance + the latest Reflect recap), authors a <em>morning brief</em> — what
/// changed, what's drifting, what needs a decision, the recommended next slice — and proposes
/// PM reconciliation tiered like <c>pm/board-sync.md</c> (auto-safe / decision / editorial).
/// Disabled by default; manual-trigger + free-VRAM, exactly like Reflect (ADR 17). It reuses
/// the local vllama judges (the <c>reflectA</c>/<c>reflectB</c> clients) — author + critic.
/// </summary>
public sealed class OvernightOptions
{
    /// <summary>Master switch. Off: the hosted service idles and <c>/brief</c> refuses.</summary>
    public bool Enabled { get; set; }

    /// <summary>Channel the morning-brief threads are created in (may equal Reflect's).</summary>
    public string ChannelId { get; set; } = "";

    /// <summary>Whether the nightly auto-run fires. False (default) = manual-only (ADR 17 posture).</summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>Local time of day the scheduled run fires (24h <c>HH:mm</c>), when <see cref="ScheduleEnabled"/>.</summary>
    public string RunAtLocalTime { get; set; } = "06:00";

    /// <summary>
    /// Absolute directory for the dated brief markdown artifact (e.g.
    /// <c>D:\\work\\gad\\pm\\journal\\brief</c>). Empty disables journaling. The git trail is the
    /// open-process record the morning brief is meant to leave (ADR 15 posture).
    /// </summary>
    public string JournalDir { get; set; } = "";

    /// <summary>Commit the brief artifact in its repo after writing it (additive, by-name). Off by default.</summary>
    public bool CommitArtifacts { get; set; }

    /// <summary>Loopback "run now" trigger port for the <c>Start-Plan</c> launcher. 0 disables (default).</summary>
    public int LocalTriggerPort { get; set; }

    /// <summary>Attempts per judge (author/critic) on transient errors before degrading. 1 disables retry.</summary>
    public int JudgeMaxAttempts { get; set; } = 3;

    /// <summary>Base backoff between judge attempts, seconds; doubles each retry.</summary>
    public int JudgeRetryBaseSeconds { get; set; } = 3;

    /// <summary>Cap on the objective-state evidence handed to the author.</summary>
    public int MaxEvidenceChars { get; set; } = 16000;

    /// <summary>
    /// Auto-apply the in-repo <em>auto-safe</em> reconciliations (E3) — today: drafting missing
    /// <c>pm/repos/&lt;name&gt;.md</c> summary stubs (additive, reversible, no external creds).
    /// <strong>Off by default</strong>: start propose-only, earn auto-apply on a track record
    /// (ADR 19). Credentialed auto-safe ops (ADO area paths) and every decision/editorial item are
    /// always surfaced as proposals, never auto-run — the editorial/Discord tier is never touched.
    /// </summary>
    public bool AutoApplyAutoSafe { get; set; }

    /// <summary>How the planner reads the board-sync delta (the PM reconciliation playbook).</summary>
    public BoardSyncOptions BoardSync { get; set; } = new();
}

/// <summary>
/// How the overnight planner invokes the GAD board-sync checker
/// (<c>board-sync-check.py --json</c>) to read the manifest→board delta, tiered auto-safe /
/// decision / live-truth (<c>pm/board-sync.md</c>). Soft, like the glance/graph seams: a missing
/// script, interpreter, or ADO auth degrades to "no board proposals", stated in the brief — the
/// glance-fed sections still stand.
/// </summary>
public sealed class BoardSyncOptions
{
    /// <summary>Master switch. Off skips the subprocess and the brief carries no board proposals.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Interpreter/executable. Default <c>python</c> for the <c>.py</c> checker.</summary>
    public string Command { get; set; } = "python";

    /// <summary>Absolute path to <c>board-sync-check.py</c>. Empty disables board proposals.</summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>Args inserted before <c>--json</c>.</summary>
    public List<string> ExtraArgs { get; set; } = new();

    /// <summary>Kill the checker if it runs longer than this. Defaults to 150s (it queries ADO + gh).</summary>
    public int TimeoutSeconds { get; set; } = 150;
}

/// <summary>Settings for the headless Claude Code builder.</summary>
public sealed class BuilderOptions
{
    /// <summary>
    /// The Claude Code executable. A bare name is resolved against <c>PATH</c> (and
    /// <c>PATHEXT</c> on Windows, so the npm <c>claude.cmd</c> shim is found); an absolute
    /// path is used as-is.
    /// </summary>
    public string Command { get; set; } = "claude";

    /// <summary>
    /// Pass <c>--dangerously-skip-permissions</c> so the builder can edit files and run
    /// build/test commands without interactive prompts. The builder runs in an isolated
    /// per-build worktree and produces only a draft PR — see PLAN.md "Security".
    /// </summary>
    public bool SkipPermissions { get; set; } = true;

    /// <summary>
    /// Run the builder in <c>--bare</c> mode: no hooks, no auto-memory, no CLAUDE.md
    /// auto-discovery, no plugin/MCP inheritance — the operator's personal <c>~/.claude</c>
    /// never bleeds into an unattended build. Off by default: <c>--bare</c> also forces
    /// Anthropic auth to <c>ANTHROPIC_API_KEY</c> (or apiKeyHelper via <c>--settings</c>) and
    /// never reads an OAuth/keychain login — enable it only once the builder authenticates
    /// with an API key.
    /// </summary>
    public bool Bare { get; set; }

    /// <summary>
    /// Hard ceiling on one build's API spend in USD, via <c>--max-budget-usd</c>. An
    /// unattended builder that loops is a real cost risk; a normal plan-implementation build
    /// runs well under this. 0 disables the cap.
    /// </summary>
    public double MaxBudgetUsd { get; set; } = 10.0;

    /// <summary>
    /// Command run in the build worktree after the builder finishes and before the draft PR
    /// is opened — an external check that does not trust the builder's self-reported success.
    /// A non-zero exit fails the session closed: no PR, worktree kept. Empty disables the
    /// gate. One global command for now — a non-.NET repo would need this changed.
    /// </summary>
    public string VerifyCommand { get; set; } = "dotnet build";

    /// <summary>Kill the verify command and fail the build if it runs longer than this.</summary>
    public int VerifyTimeoutMinutes { get; set; } = 10;

    /// <summary>Extra CLI arguments appended verbatim to every builder invocation.</summary>
    public List<string> ExtraArgs { get; set; } = new();

    /// <summary>Kill the builder and fail the build if it runs longer than this.</summary>
    public int TimeoutMinutes { get; set; } = 30;
}

/// <summary>One model endpoint (planner or critic).</summary>
public sealed class ModelOptions
{
    /// <summary>openai | ollama | anthropic — selects the default endpoint.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Model id.</summary>
    public string Model { get; set; } = "";

    /// <summary>Optional explicit base URL; overrides the provider default.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>API key. Secret — set via user-secrets or environment.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Per-request network timeout, seconds. A non-streaming planner/critic call holds one
    /// HTTP request open for a cold model-load plus the full generation; the OpenAI client's
    /// ~100s default is too short for an unattended local run. Also the loop's only per-call
    /// backstop — a hung server fails the turn here rather than blocking until morning.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 300;
}

/// <summary>Planner, critic, and reflect-judge model configuration.</summary>
public sealed class ModelsOptions
{
    public const string Section = "Models";

    public ModelOptions Planner { get; set; } = new();

    public ModelOptions Critic { get; set; } = new();

    /// <summary>First reflect judge. Defaults target the vllama facade's planner alias.</summary>
    public ModelOptions ReflectA { get; set; } = new();

    /// <summary>Second reflect judge. Defaults target the vllama facade's critic alias.</summary>
    public ModelOptions ReflectB { get; set; } = new();
}

/// <summary>OpenTelemetry export settings.</summary>
public sealed class OtelOptions
{
    public const string Section = "Otel";

    public string ServiceName { get; set; } = "ember";

    /// <summary>OTLP endpoint. Empty disables OTLP export; the console exporter still runs.</summary>
    public string? Endpoint { get; set; }
}
