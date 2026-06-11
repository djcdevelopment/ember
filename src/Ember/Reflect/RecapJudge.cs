using Microsoft.Extensions.AI;

namespace Ember.Reflect;

/// <summary>
/// One independent recap author. Two of these run per reflect session — different models on
/// different cards — and never see each other's output; the comparison happens afterward in
/// <see cref="DivergenceComparer"/>.
/// </summary>
public sealed class RecapJudge
{
    private const string SystemPrompt =
        """
        You are an independent engineering-journal writer for a solo developer's multi-repo
        workspace (the "constellation"). You are given structured evidence of a working
        period: per-repo commits, changed files, and code symbols from a knowledge graph.

        Write a concise recap in Markdown with exactly these sections:
        1. **What happened** — per repo, concrete and specific.
        2. **Threads & risks** — cross-repo connections, half-done work, anything that looks
           like it needs follow-up.
        3. **Open questions** — things the evidence cannot settle.

        Ground every statement in the evidence. Do not invent files, symbols, or motives.
        If the evidence is thin, say so plainly. At most 400 words.
        """;

    private readonly IChatClient _chat;

    /// <summary>Display label ("A"/"B") used in posts and telemetry.</summary>
    public string Label { get; }

    public RecapJudge(IChatClient chat, string label)
    {
        _chat = chat;
        Label = label;
    }

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
