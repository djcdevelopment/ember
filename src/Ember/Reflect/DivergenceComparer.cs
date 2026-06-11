using System.Text.Json;
using Ember.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>One point where the two recaps disagree.</summary>
public sealed class Divergence
{
    public string Topic { get; set; } = "";

    public string ASays { get; set; } = "";

    public string BSays { get; set; } = "";
}

/// <summary>Structured comparison of the two independent recaps.</summary>
public sealed class ComparisonResult
{
    public List<string> Agreements { get; set; } = new();

    public List<Divergence> Divergences { get; set; } = new();
}

/// <summary>
/// Extracts agreements and divergences from the two recaps with one structured-output call.
/// Same JSON-mode + parse-retry posture as <see cref="Loop.Critic"/>; a parse failure
/// soft-fails to null — the recap posts without the comparison rather than not at all.
/// </summary>
public sealed class DivergenceComparer
{
    private const string SystemPrompt =
        """
        You compare two independently written recaps of the same engineering evidence.
        Identify where they agree and where they meaningfully diverge — different claims,
        different emphasis on risk, or facts one mentions that the other omits. Ignore
        phrasing differences.

        Respond with ONLY a JSON object — no prose, no markdown fences:
        {
          "agreements": ["<shared claim>"],
          "divergences": [
            { "topic": "<subject>", "aSays": "<recap A's position>", "bSays": "<recap B's position>" }
          ]
        }
        Keep each entry to one sentence. Empty arrays are valid.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly ChatOptions JsonMode = new() { ResponseFormat = ChatResponseFormat.Json };

    private readonly IChatClient _chat;
    private readonly ILogger<DivergenceComparer> _logger;

    public DivergenceComparer(
        [FromKeyedServices("reflectB")] IChatClient chat, ILogger<DivergenceComparer> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<ComparisonResult?> CompareAsync(string recapA, string recapB, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Recap A:\n{recapA}\n\nRecap B:\n{recapB}"),
        };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await _chat.GetResponseAsync(messages, JsonMode, ct);
            var text = response.Text ?? "";

            var result = TryParse(text);
            if (result is not null)
                return result;

            _logger.LogWarning("Divergence comparer returned unparseable output (attempt {Attempt}).", attempt);
            messages.Add(new ChatMessage(ChatRole.Assistant, text));
            messages.Add(new ChatMessage(ChatRole.User, "That was not valid JSON. Respond again with ONLY the JSON object."));
        }

        return null;
    }

    private static ComparisonResult? TryParse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ComparisonResult>(text[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
