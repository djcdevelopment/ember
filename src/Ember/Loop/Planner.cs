using Microsoft.Extensions.AI;

namespace Ember.Loop;

/// <summary>The Claude planner. Authors an implementation plan and revises it against critique.</summary>
public sealed class Planner
{
    private const string SystemPrompt =
        """
        You are a senior engineer who writes precise, buildable implementation plans.
        A good plan states what to build, the concrete steps, the files involved, and
        clear acceptance criteria. Be specific and realistic. Output Markdown, no preamble.
        """;

    private readonly IChatClient _chat;

    public Planner([FromKeyedServices("planner")] IChatClient chat) => _chat = chat;

    /// <summary>Produces the first draft of a plan from the brief and repo context.</summary>
    public async Task<string> DraftAsync(string brief, string repoContext, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,
                $"Write an implementation plan for this brief.\n\nBrief:\n{brief}\n\nTarget repo context:\n{repoContext}"),
        };

        var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
        return response.Text ?? "";
    }

    /// <summary>Revises the plan to resolve the critic's open issues and any operator steering.</summary>
    public async Task<string> ReviseAsync(
        string brief, string currentPlan, CriticVerdict critique, string steering, CancellationToken ct)
    {
        var issues = string.Join("\n", critique.OpenIssues.Select(
            i => $"- [{i.Severity}] {i.Summary} — suggested fix: {i.Fix}"));

        var steeringBlock = string.IsNullOrWhiteSpace(steering)
            ? ""
            : $"\n\nThe operator added this steering — incorporate it:\n{steering}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,
                $"Brief:\n{brief}\n\nCurrent plan:\n{currentPlan}\n\n"
                + $"A reviewer raised these issues — revise the plan to resolve every one:\n{issues}{steeringBlock}\n\n"
                + "Output the full revised plan in Markdown."),
        };

        var response = await _chat.GetResponseAsync(messages, cancellationToken: ct);
        return response.Text ?? "";
    }
}
