using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Ember.Loop;

/// <summary>The GPT red-team critic. Reviews a plan and returns a structured verdict.</summary>
public sealed class Critic
{
    private const string SystemPrompt =
        """
        You are a rigorous red-team reviewer of software implementation plans. Find real
        problems: gaps, wrong assumptions, missing acceptance criteria, unhandled failure
        modes, scope creep. Do NOT raise style preferences.

        Respond with ONLY a JSON object — no prose, no markdown fences:
        {
          "assessment": "<one sentence overall read>",
          "issues": [
            { "severity": "blocking|major|minor", "summary": "<the problem>", "fix": "<concrete fix>" }
          ]
        }
        Use "blocking" or "major" only for issues that would make the build fail or build the
        wrong thing. Everything else is "minor". An empty issues list means the plan is sound.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IChatClient _chat;
    private readonly ILogger<Critic> _logger;

    public Critic([FromKeyedServices("critic")] IChatClient chat, ILogger<Critic> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    /// <summary>Reviews a plan against the brief and returns a structured verdict.</summary>
    public async Task<CriticVerdict> ReviewAsync(string brief, string plan, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Brief:\n{brief}\n\nPlan to review:\n{plan}"),
        };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
            var text = response.Text ?? "";

            var verdict = TryParse(text);
            if (verdict is not null)
                return verdict;

            _logger.LogWarning("Critic returned unparseable output (attempt {Attempt}).", attempt);
            messages.Add(new ChatMessage(ChatRole.Assistant, text));
            messages.Add(new ChatMessage(ChatRole.User, "That was not valid JSON. Respond again with ONLY the JSON object."));
        }

        // A parse failure must never read as approval.
        return new CriticVerdict
        {
            Assessment = "Critic output could not be parsed; treating the plan as not approved.",
            Issues =
            {
                new CriticIssue
                {
                    Severity = IssueSeverity.Major,
                    Summary = "The critic model did not return valid JSON.",
                    Fix = "Re-run, or check the critic model and endpoint configuration.",
                },
            },
        };
    }

    private static CriticVerdict? TryParse(string text)
    {
        var json = ExtractJson(text);
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<CriticVerdict>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
