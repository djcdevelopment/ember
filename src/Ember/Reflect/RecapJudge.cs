using Microsoft.Extensions.AI;

namespace Ember.Reflect;

/// <summary>
/// One independent recap author. Two of these run per reflect session — different models on
/// different cards — and never see each other's output; the comparison happens afterward in
/// <see cref="DivergenceComparer"/>.
///
/// The prompt is the <c>recap-prompt/v2-xml-cite</c> contract (ADR 16, adopted from EXP-0001):
/// every claim must carry a <c>&lt;from&gt;</c> citing evidence, which in the experiment took
/// the 30B's cross-repo hallucination rate from 3/3 plain runs to 0/3 cited runs. The XML is
/// rendered to readable markdown downstream by <see cref="RecapXml"/>.
/// </summary>
public sealed class RecapJudge
{
    internal const string SystemPrompt =
        """
        You are an independent engineering-journal writer for a solo developer's multi-repo workspace (the "constellation"). You are given structured evidence: per-repo commits, changed files, and code symbols.

        Write the recap as XML in EXACTLY this shape:
        <recap>
          <repo name="...">
            <claim>
              <statement>one concrete thing that happened</statement>
              <from>a commit hash or file path that appears VERBATIM in the evidence</from>
            </claim>
          </repo>
          <threads><thread>cross-repo connection or risk grounded in evidence</thread></threads>
          <open-questions><question>something the evidence cannot settle</question></open-questions>
        </recap>

        HARD RULE: every <statement> MUST carry a <from> that cites a hash or path present verbatim in the evidence. If you cannot cite it, do not write it. Never invent cross-repo dependencies. Output ONLY the XML.
        """;

    private readonly IChatClient _chat;

    /// <summary>Display label ("A"/"B") used in posts and telemetry.</summary>
    public string Label { get; }

    public RecapJudge(IChatClient chat, string label)
    {
        _chat = chat;
        Label = label;
    }

    /// <summary>Returns the raw recap XML; rendering and citation-scoring happen in the runner.</summary>
    public async Task<string> WriteAsync(string evidence, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Evidence of the working period:\n\n{evidence}"),
        };

        var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
        return response.Text ?? "";
    }
}
