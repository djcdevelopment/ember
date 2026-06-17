using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ember.Overnight;

/// <summary>
/// The brief reviewer — the critic role applied to <em>planning</em> (ADR 19). It checks the
/// author's brief against the objective state and flags ungrounded claims, invented repos,
/// mis-tiered proposals (the load-bearing one: an editorial/Discord item dressed as auto-safe),
/// and high-signal omissions (a repo with large WIP the brief ignored). Returns a short issue
/// list the author revises against — the planning analogue of Reflect's citation discipline.
/// Runs on the second local vllama judge (the <c>reflectB</c> client).
/// </summary>
public sealed class BriefCritic
{
    internal const string SystemPrompt =
        """
        You review a MORNING BRIEF against the OBJECTIVE STATE it was written from. You are the guard against a brief that reads well but misleads.

        Flag an issue when the brief:
        - states something not supported by the objective state (ungrounded), or invents a repo / commit / work;
        - mis-tiers a proposal — especially anything touching Discord copy or finding-comments marked [auto-safe] (those are [editorial]); or a structural call (epic/state/lifecycle) marked [auto-safe] (that is [decision]);
        - omits a high-signal item that is clearly in the state (e.g. a repo with large uncommitted WIP, or a "needs your call" tension) and not mentioned;
        - recommends a next slice that contradicts the state (e.g. a deprecating repo, or one with no in-flight work).

        Respond with ONLY a JSON array of short issue strings, e.g. ["raidui WIP not mentioned", "Discord draft mis-tiered as auto-safe"]. If the brief is well-grounded and correctly tiered, respond with exactly [].
        """;

    private readonly IChatClient _chat;

    public BriefCritic(IChatClient chat) => _chat = chat;

    /// <summary>Returns the reviewer's issues (empty when the brief passes).</summary>
    public async Task<IReadOnlyList<string>> ReviewAsync(
        string objectiveState, string brief, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"OBJECTIVE STATE:\n{objectiveState}\n\nBRIEF to review:\n{brief}"),
        };
        var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
        return ParseIssues(response.Text ?? "");
    }

    /// <summary>Extracts the JSON array of issue strings; tolerant of prose around it.</summary>
    public static IReadOnlyList<string> ParseIssues(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return Array.Empty<string>();
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(text[start..(end + 1)]);
            return arr?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
