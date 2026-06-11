using System.Diagnostics;
using Ember.Config;
using Ember.Loop;
using Ember.Models;
using Ember.Reflect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ember.Cli;

/// <summary>
/// <c>dotnet run -- reflect [--dry-run] [--since-hours N]</c> — runs the reflect pipeline
/// from the console with no Discord and no persistence. The baseline is "N hours ago"
/// (default 24) resolved per repo from git, so the database is never touched: a read-only
/// way to validate evidence assembly, and — when the local endpoints are up — the judges.
/// </summary>
public static class ReflectCli
{
    /// <summary>True when the process args ask for the console reflect run instead of the bot.</summary>
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("reflect", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        var sinceHours = 24;
        var sinceIndex = Array.FindIndex(args, a => a.Equals("--since-hours", StringComparison.OrdinalIgnoreCase));
        if (sinceIndex >= 0)
        {
            if (sinceIndex + 1 >= args.Length || !int.TryParse(args[sinceIndex + 1], out sinceHours) || sinceHours <= 0)
            {
                Console.Error.WriteLine("usage: dotnet run -- reflect [--dry-run] [--since-hours N]");
                return 1;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // A minimal host: configuration and just the reflect core — no Discord, no SQLite,
        // no hosted services. Mirrors PlanCli.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<EmberOptions>(builder.Configuration.GetSection(EmberOptions.Section));
        builder.Services.AddSingleton<IPostConfigureOptions<EmberOptions>, EmberOptionsPostConfigure>();
        builder.Services.Configure<ModelsOptions>(builder.Configuration.GetSection(ModelsOptions.Section));
        builder.Services.AddKeyedSingleton<IChatClient>("reflectA", (sp, _) =>
            ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.ReflectA));
        builder.Services.AddKeyedSingleton<IChatClient>("reflectB", (sp, _) =>
            ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.ReflectB));
        builder.Services.AddSingleton<GraphContext>();
        builder.Services.AddSingleton<EvidenceAssembler>();
        builder.Services.AddSingleton<DivergenceComparer>();
        builder.Services.AddSingleton<ReflectRunner>();
        using var host = builder.Build();

        var models = host.Services.GetRequiredService<IOptions<ModelsOptions>>().Value;
        var runner = host.Services.GetRequiredService<ReflectRunner>();

        Console.WriteLine();
        Console.WriteLine($"Reflect — baseline: {sinceHours}h ago per repo  (console, read-only)");
        Console.WriteLine($"  judge A: {models.ReflectA.Provider} / {models.ReflectA.Model}");
        Console.WriteLine($"  judge B: {models.ReflectB.Provider} / {models.ReflectB.Model}");
        if (dryRun)
            Console.WriteLine("  dry run — evidence only, no model calls");
        Console.WriteLine();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var outcome = await runner.PrepareAsync(
                new BaselineMode.SinceHours(sinceHours), runJudges: !dryRun, cts.Token);
            stopwatch.Stop();

            Console.WriteLine("──── evidence ────");
            Console.WriteLine();
            Console.WriteLine(outcome.Evidence.Text);

            if (!dryRun && outcome.Status == RecapStatus.Ran)
            {
                Console.WriteLine("──── recap (as it would post) ────");
                Console.WriteLine();
                Console.WriteLine(outcome.PostText);
            }

            Console.WriteLine();
            Console.WriteLine($"status: {outcome.Status}  ({stopwatch.Elapsed.TotalSeconds:0.0}s"
                + $", {outcome.Evidence.TotalChars} evidence chars)");
            if (outcome.Error is not null)
                Console.WriteLine($"error: {outcome.Error}");

            return outcome.Status == RecapStatus.Failed ? 1 : 0;
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
}
