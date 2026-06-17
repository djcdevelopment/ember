using System.Diagnostics;
using Ember.Config;
using Ember.Loop;
using Ember.Models;
using Ember.Overnight;
using Ember.Reflect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ember.Cli;

/// <summary>
/// <c>dotnet run -- brief [--dry-run]</c> — runs the overnight planner from the console with no
/// Discord, no persistence, and no auto-apply. <c>--dry-run</c> stops after assembling the
/// objective state (the glance + last recap + board-sync delta) — a read-only way to validate
/// what the brief is grounded in, and — when the local endpoints are up — the authored brief.
/// Mirrors <see cref="ReflectCli"/>.
/// </summary>
public static class BriefCli
{
    /// <summary>True when the process args ask for the console brief run instead of the bot.</summary>
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("brief", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // A minimal host: configuration and just the overnight core — no Discord, no SQLite,
        // no hosted services. Mirrors ReflectCli.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<EmberOptions>(builder.Configuration.GetSection(EmberOptions.Section));
        builder.Services.AddSingleton<IPostConfigureOptions<EmberOptions>, EmberOptionsPostConfigure>();
        builder.Services.Configure<ModelsOptions>(builder.Configuration.GetSection(ModelsOptions.Section));
        builder.Services.AddKeyedSingleton<IChatClient>("reflectA", (sp, _) =>
            ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.ReflectA));
        builder.Services.AddKeyedSingleton<IChatClient>("reflectB", (sp, _) =>
            ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.ReflectB));
        builder.Services.AddSingleton<GraphContext>();
        builder.Services.AddSingleton<GlanceReader>();
        builder.Services.AddSingleton<BoardSyncReader>();
        builder.Services.AddSingleton<BriefAssembler>();
        builder.Services.AddSingleton<OvernightRunner>();
        using var host = builder.Build();

        var models = host.Services.GetRequiredService<IOptions<ModelsOptions>>().Value;
        var runner = host.Services.GetRequiredService<OvernightRunner>();

        Console.WriteLine();
        Console.WriteLine("Overnight brief  (console, read-only)");
        Console.WriteLine($"  author: {models.ReflectA.Provider} / {models.ReflectA.Model}");
        Console.WriteLine($"  critic: {models.ReflectB.Provider} / {models.ReflectB.Model}");
        if (dryRun)
            Console.WriteLine("  dry run — objective state only, no model calls");
        Console.WriteLine();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var outcome = await runner.PrepareAsync(runJudges: !dryRun, cts.Token);
            stopwatch.Stop();

            if (dryRun)
            {
                Console.WriteLine("──── objective state (brief inputs) ────");
                Console.WriteLine();
                Console.WriteLine(outcome.Inputs.Text);
            }
            else
            {
                Console.WriteLine("──── brief (as it would post) ────");
                Console.WriteLine();
                Console.WriteLine(outcome.PostText);
            }

            Console.WriteLine();
            Console.WriteLine($"status: {outcome.Status}  ({stopwatch.Elapsed.TotalSeconds:0.0}s"
                + $", {outcome.Inputs.TotalChars} input chars, {outcome.Inputs.GlanceRepoCount} repos)");
            if (outcome.Error is not null)
                Console.WriteLine($"error: {outcome.Error}");

            return outcome.Status == BriefStatus.Failed ? 1 : 0;
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
