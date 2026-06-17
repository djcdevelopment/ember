using Microsoft.Extensions.AI;

namespace Ember.Overnight;

/// <summary>
/// The morning-brief author — the planner role applied to <em>planning</em> (ADR 19). Given the
/// assembled objective state, it writes a tight, scannable brief in four sections so the operator
/// wakes oriented: what changed, what's drifting, what needs a decision, the recommended next
/// slice — plus tiered board proposals when the reconciliation read found a delta. Grounded:
/// every line must trace to the objective state it was handed; it never invents repos or work.
/// Runs on a local vllama judge (the <c>reflectA</c> client) so the run stays free-VRAM.
/// </summary>
public sealed class BriefAuthor
{
    internal const string SystemPrompt =
        """
        You are the chief of staff for a solo developer who works across many repos (the "constellation"). You are given the OBJECTIVE STATE of the constellation (assembled from git working-tree truth + the last recap + a board-sync delta). Write a MORNING BRIEF the operator reads first thing, so they open the day building, not reconciling.

        Output Markdown in EXACTLY these sections, in order:
        ## What changed
        ## What's drifting
        ## Needs your call
        ## Recommended next slice
        ## Proposals (tiered)

        Rules:
        - Ground EVERY statement in the objective state you were given. Never invent a repo, a commit, or work that is not in the state. If a section has nothing, write "Nothing — quiet here."
        - "Recommended next slice" = pick ONE concrete next piece of work from the candidates and justify it in 1-2 sentences (which repo, why now). This is the day's opening move.
        - "Proposals (tiered)" mirrors pm/board-sync.md tiers. Label each item [auto-safe], [decision], or [editorial]. Auto-safe = additive/reversible (e.g. draft a missing summary doc, create an area path). Decision = a human structural call (epic structure, state transitions, lifecycle). Editorial = finding-comments or Discord copy. If board-sync was unavailable or in sync, say so and propose nothing there.
        - Be concise and scannable — short bullets, no preamble, no restating these instructions.
        """;

    private readonly IChatClient _chat;

    public BriefAuthor(IChatClient chat) => _chat = chat;

    /// <summary>Writes the first draft of the brief from the objective state.</summary>
    public async Task<string> DraftAsync(string objectiveState, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"OBJECTIVE STATE of the constellation:\n\n{objectiveState}"),
        };
        var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
        return response.Text ?? "";
    }

    /// <summary>Revises the brief to resolve the critic's issues, still grounded in the state.</summary>
    public async Task<string> ReviseAsync(
        string objectiveState, string draft, IReadOnlyList<string> issues, CancellationToken ct)
    {
        var issueBlock = string.Join("\n", issues.Select(i => $"- {i}"));
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,
                $"OBJECTIVE STATE:\n{objectiveState}\n\nYour draft brief:\n{draft}\n\n"
                + $"A reviewer raised these issues — fix every one, keep the same section structure, stay grounded:\n{issueBlock}\n\n"
                + "Output the full revised brief."),
        };
        var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
        return response.Text ?? "";
    }
}
