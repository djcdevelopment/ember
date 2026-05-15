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

    /// <summary>Allowlisted repos: key -> absolute path on the host.</summary>
    public Dictionary<string, string> Repos { get; set; } = new();
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
