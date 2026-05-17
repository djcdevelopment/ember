using System.ClientModel;
using Ember.Config;
using Ember.Observability;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Ember.Models;

/// <summary>
/// Builds an <see cref="IChatClient"/> from <see cref="ModelOptions"/>.
/// OpenAI, Ollama, and Anthropic are all reached through the OpenAI-compatible
/// chat API, so a single adapter covers every provider — <c>Provider</c> only
/// selects the default endpoint. Registered in DI for Phase 1; not invoked in Phase 0.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(ModelOptions options)
    {
        var clientOptions = new OpenAIClientOptions();
        var endpoint = ResolveEndpoint(options);
        if (endpoint is not null)
            clientOptions.Endpoint = endpoint;

        // The default (~100s) cannot cover a cold local model-load plus a full generation.
        clientOptions.NetworkTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);

        var apiKey = string.IsNullOrEmpty(options.ApiKey) ? "unset" : options.ApiKey;
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);

        // Wrap in the OpenTelemetry chat client so every planner/critic call emits a span
        // with model, token usage, and duration. The source name is ember's own "Ember"
        // ActivitySource/Meter — already registered with OpenTelemetry in Program.cs, so
        // these spans and metrics need no extra wiring.
        return client.GetChatClient(options.Model)
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: Telemetry.SourceName)
            .Build();
    }

    private static Uri? ResolveEndpoint(ModelOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            return new Uri(options.BaseUrl);

        return options.Provider.ToLowerInvariant() switch
        {
            "ollama" => new Uri("http://localhost:11434/v1"),
            "anthropic" => new Uri("https://api.anthropic.com/v1/"),
            _ => null, // OpenAI default endpoint
        };
    }
}
