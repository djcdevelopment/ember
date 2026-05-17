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

    /// <summary>Allowlisted repos: key -> absolute path on the host.</summary>
    public Dictionary<string, string> Repos { get; set; } = new();
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

/// <summary>Planner and critic model configuration.</summary>
public sealed class ModelsOptions
{
    public const string Section = "Models";

    public ModelOptions Planner { get; set; } = new();

    public ModelOptions Critic { get; set; } = new();
}

/// <summary>OpenTelemetry export settings.</summary>
public sealed class OtelOptions
{
    public const string Section = "Otel";

    public string ServiceName { get; set; } = "ember";

    /// <summary>OTLP endpoint. Empty disables OTLP export; the console exporter still runs.</summary>
    public string? Endpoint { get; set; }
}
