using System.Diagnostics;
using Ember.Config;
using Ember.Loop;
using Ember.Manifest;
using Ember.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ember.Cli;

/// <summary>
/// <c>dotnet run -- plan "&lt;brief&gt;" &lt;repo&gt;</c> — runs the Claude/GPT planning loop
/// from the console, with no Discord, no gate, and no build. A way to exercise the loop
/// directly (including against a local Ollama critic) without a Discord round-trip.
/// </summary>
public static class PlanCli
{
    /// <summary>True when the process args ask for the CLI planning loop instead of the bot.</summary>
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("plan", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3 || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine("usage: dotnet run -- plan \"<brief>\" <repo> [--dry-run]");
            return 1;
        }
        var brief = args[1];
        var repoKey = args[2];
        var dryRun = args.Skip(3).Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the loop unwind cleanly rather than hard-killing
            cts.Cancel();
        };

        // A minimal host: configuration (appsettings + Development + user-secrets + env) and
        // just the planner/critic — no Discord, no SQLite, no hosted services. Args are not
        // passed to the config builder, so the positional brief/repo cannot trip it up.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<ModelsOptions>(builder.Configuration.GetSection(ModelsOptions.Section));
        builder.Services.Configure<EmberOptions>(builder.Configuration.GetSection(EmberOptions.Section));
        builder.Services.AddSingleton<IPostConfigureOptions<EmberOptions>, EmberOptionsPostConfigure>();
        builder.Services.AddKeyedSingleton<IChatClient>("planner", (sp, _) =>
            ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.Planner));
        builder.Services.AddKeyedSingleton<IChatClient>("critic", (sp, _) =>
            ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.Critic));
        builder.Services.AddSingleton<Planner>();
        builder.Services.AddSingleton<Critic>();
        builder.Services.AddSingleton<ManifestLoader>();
        builder.Services.AddSingleton<GraphContext>();
        using var host = builder.Build();

        var ember = host.Services.GetRequiredService<IOptions<EmberOptions>>().Value;
        var models = host.Services.GetRequiredService<IOptions<ModelsOptions>>().Value;

        if (!ember.Repos.TryGetValue(repoKey, out var entry))
        {
            var known = ember.Repos.Count == 0 ? "(none configured)" : string.Join(", ", ember.Repos.Keys);
            Console.Error.WriteLine($"Unknown repo '{repoKey}'. Allowlisted: {known}");
            return 1;
        }
        var repoPath = entry.Path;

        var planner = host.Services.GetRequiredService<Planner>();
        var critic = host.Services.GetRequiredService<Critic>();
        var manifest = host.Services.GetRequiredService<ManifestLoader>();
        var graph = host.Services.GetRequiredService<GraphContext>();

        Console.WriteLine();
        Console.WriteLine($"Planning loop — repo \"{repoKey}\"  ({repoPath})");
        Console.WriteLine($"  planner: {models.Planner.Provider} / {models.Planner.Model}");
        Console.WriteLine($"  critic:  {models.Critic.Provider} / {models.Critic.Model}");
        Console.WriteLine($"  brief:   {brief}");
        Console.WriteLine("  dry run — loop only, no gate and no build");
        Console.WriteLine();

        try
        {
            return await RunLoopAsync(brief, repoKey, repoPath, entry.Constellation, planner, critic, manifest, graph, ember, dryRun, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Aborted.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunLoopAsync(
        string brief, string repoKey, string repoPath, string? constellationPath,
        Planner planner, Critic critic, ManifestLoader manifest, GraphContext graph,
        EmberOptions ember, bool dryRun,
        CancellationToken ct)
    {
        var repoContext = RepoContext.Gather(repoPath);
        var graphSection = await graph.GatherAsync(repoPath, brief, ct);
        if (graphSection is not null)
        {
            Console.WriteLine($"  graph:     folded {graphSection.Length} chars of knowledge-graph context");
            repoContext = graphSection + "\n\n" + repoContext;
        }
        else
        {
            Console.WriteLine("  graph:     unavailable — proceeding without graph context");
        }
        if (constellationPath is not null)
        {
            var summary = await manifest.LoadSummaryAsync(constellationPath, repoKey, ct);
            if (summary is not null)
            {
                Console.WriteLine($"  manifest:  folded {summary.Length} chars of constellation context");
                Console.WriteLine();
                Console.WriteLine("──── manifest summary (prepended to round-1 prompt) ────");
                Console.WriteLine();
                Console.WriteLine(summary);
                Console.WriteLine();
                repoContext = summary + "\n\n" + repoContext;
            }
            else
            {
                Console.WriteLine($"  manifest:  failed to load from {constellationPath} — see warning logs above");
                Console.WriteLine();
            }
        }

        if (dryRun)
        {
            Console.WriteLine("(--dry-run: stopping before any model calls)");
            return 0;
        }

        Console.WriteLine("round 1");
        var plan = await Step("planner drafts", () => planner.DraftAsync(brief, repoContext, ct));

        var round = 1;
        var previousOpen = int.MaxValue;
        string gateReason;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var verdict = await Step("critic reviews", () => critic.ReviewAsync(brief, plan, repoContext, ct));
            PrintVerdict(verdict);

            if (verdict.Approved) { gateReason = "approved"; break; }
            if (round >= ember.MaxPlanRounds) { gateReason = "round_cap"; break; }
            if (verdict.OpenIssues.Count >= previousOpen) { gateReason = "stalled"; break; }
            previousOpen = verdict.OpenIssues.Count;

            round++;
            Console.WriteLine($"round {round}");
            plan = await Step("planner revises", () => planner.ReviseAsync(brief, plan, verdict, "", ct));
        }

        Console.WriteLine();
        Console.WriteLine(gateReason switch
        {
            "approved" => "Gate: approved — the critic raised no blocking or major issues.",
            "round_cap" => $"Gate: round_cap — forced forward after {ember.MaxPlanRounds} rounds.",
            _ => "Gate: stalled — a round closed no issues.",
        });
        Console.WriteLine();
        Console.WriteLine("──── converged plan ────");
        Console.WriteLine();
        Console.WriteLine(plan);
        return 0;
    }

    /// <summary>Runs one labelled, timed loop step — prints the label, then the elapsed time.</summary>
    private static async Task<T> Step<T>(string label, Func<Task<T>> work)
    {
        Console.Write($"  {label} ");
        var stopwatch = Stopwatch.StartNew();
        var result = await work();
        stopwatch.Stop();

        var dots = new string('.', Math.Max(3, 24 - label.Length));
        Console.WriteLine($"{dots} {stopwatch.Elapsed.TotalSeconds,6:0.0}s");
        return result;
    }

    private static void PrintVerdict(CriticVerdict verdict)
    {
        Console.WriteLine(verdict.Approved
            ? "    verdict: approved"
            : $"    verdict: not approved — {verdict.OpenIssues.Count} open issue(s)");

        if (!string.IsNullOrWhiteSpace(verdict.Assessment))
            Console.WriteLine($"    \"{verdict.Assessment}\"");

        foreach (var issue in verdict.Issues)
            Console.WriteLine($"      [{issue.Severity}] {issue.Summary}");
    }
}
