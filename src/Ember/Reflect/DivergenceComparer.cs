using System.Xml.Linq;
using Ember.Config;
using Microsoft.Extensions.AI;

namespace Ember.Reflect;

/// <summary>One point where the two recaps disagree.</summary>
public sealed class Divergence
{
    public string Topic { get; set; } = "";

    /// <summary>contradiction | omission — set by the v2 comparer so emphasis-noise is excluded.</summary>
    public string Kind { get; set; } = "";

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
/// Uses the <c>comparer-prompt/v2-contradiction-or-omission</c> + <c>comparer-schema/v2-xml</c>
/// contracts (ADR 16, from EXP-0001): the improved prompt was the dominant win against the
/// "over-fires on emphasis" problem, XML the cleaner/faster add-on. Parse-and-retry once; a
/// persistent parse failure soft-fails to null — the recap posts without the comparison rather
/// than not at all.
/// </summary>
public sealed class DivergenceComparer
{
    internal const string SystemPrompt =
        """
        You compare two independently written recaps of the same engineering evidence. Report ONLY genuine divergences: one recap asserts something the other CONTRADICTS, or states a load-bearing fact the other OMITS. Do NOT report differences that are merely tone, emphasis, confidence, or wording - those are not divergences.

        Respond with ONLY this XML, nothing else:
        <comparison>
          <agreements><item>shared claim</item></agreements>
          <divergences>
            <divergence>
              <topic>subject</topic>
              <kind>contradiction|omission</kind>
              <a>A's position</a>
              <b>B's position</b>
            </divergence>
          </divergences>
        </comparison>
        Empty <agreements/> or <divergences/> are valid.
        """;

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
            var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
            var text = response.Text ?? "";

            var result = TryParse(text);
            if (result is not null)
                return result;

            _logger.LogWarning("Divergence comparer returned unparseable output (attempt {Attempt}).", attempt);
            messages.Add(new ChatMessage(ChatRole.Assistant, text));
            messages.Add(new ChatMessage(ChatRole.User,
                "That was not valid XML. Respond again with ONLY the <comparison>...</comparison> XML."));
        }

        return null;
    }

    private static ComparisonResult? TryParse(string text)
    {
        var start = text.IndexOf("<comparison", StringComparison.Ordinal);
        var closeAt = text.IndexOf("</comparison>", StringComparison.Ordinal);
        if (start < 0 || closeAt <= start)
            return null;
        try
        {
            var xml = RecapXml.SanitizeForParse(text.Substring(start, closeAt - start + "</comparison>".Length));
            var root = XElement.Parse(xml);

            var result = new ComparisonResult();
            foreach (var item in root.Descendants("item"))
            {
                var v = item.Value.Trim();
                if (v.Length > 0)
                    result.Agreements.Add(v);
            }
            foreach (var d in root.Descendants("divergence"))
            {
                result.Divergences.Add(new Divergence
                {
                    Topic = ((string?)d.Element("topic") ?? "").Trim(),
                    Kind = ((string?)d.Element("kind") ?? "").Trim(),
                    ASays = ((string?)d.Element("a") ?? "").Trim(),
                    BSays = ((string?)d.Element("b") ?? "").Trim(),
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
