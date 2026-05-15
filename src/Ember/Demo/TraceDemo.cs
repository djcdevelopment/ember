using System.Diagnostics;
using Ember.Observability;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ember.Demo;

/// <summary>
/// Emits a synthetic — but faithful — set of ember traces and metrics to the console
/// exporter: the exact span names and tags a real <c>/plan</c> run produces, with realistic
/// nesting and timings. Invoked with <c>dotnet run -- demo</c>; it never starts the bot,
/// touches Discord, or calls a model. It is a way to *see* the telemetry, not a test.
/// </summary>
public static class TraceDemo
{
    /// <summary>True when the process args ask for the trace demo instead of the bot.</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals("demo", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("--demo", StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync()
    {
        // An isolated telemetry pipeline — console exporters only, independent of the
        // production OpenTelemetry wiring in Program.cs.
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(r => r.AddService("ember"))
            .AddSource(Telemetry.SourceName)
            .AddConsoleExporter()
            .Build();

        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(r => r.AddService("ember"))
            .AddMeter(Telemetry.SourceName)
            .AddConsoleExporter()
            .Build();

        PrintHeader();

        // ── One full /plan -> draft-PR session ────────────────────────────────────────
        // ember emits a session's lifecycle as four separate traces, not one: the gate
        // countdown (~5 min) and the build span minutes and can cross a process restart,
        // so a single long-lived span is not viable. They correlate by tag.
        EmitCommandTrace();
        EmitPlanningTrace();
        EmitGateTrace();
        EmitSuccessfulBuildTrace();

        // ── A second session whose build fails ───────────────────────────────────────
        EmitFailedBuildTrace();

        // ── Metrics (Counter + Histogram) ────────────────────────────────────────────
        Section("Metrics — ember.commands.handled, ember.builds.completed, ember.build.duration");
        Telemetry.CommandsHandled.Add(3, Tag("command", "plan"));
        Telemetry.CommandsHandled.Add(2, Tag("command", "status"));
        Telemetry.CommandsHandled.Add(1, Tag("command", "abort"));
        Telemetry.BuildsCompleted.Add(1, Tag("outcome", "success"));
        Telemetry.BuildsCompleted.Add(1, Tag("outcome", "failed"));
        Telemetry.BuildDuration.Record(204.6, Tag("outcome", "success"));
        Telemetry.BuildDuration.Record(47.2, Tag("outcome", "failed"));

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();
        await Task.Delay(250); // let the console exporter drain

        PrintFooter();
    }

    // ── The four lifecycle traces ─────────────────────────────────────────────────────

    /// <summary><c>command /plan</c> — created per slash command in DiscordBotService.</summary>
    private static void EmitCommandTrace()
    {
        Section("Trace 1/5 — command /plan  (the slash command; DiscordBotService)");
        var t = DateTime.UtcNow;
        var span = Start("command /plan", t);
        Finish(span, t.AddMilliseconds(38));
    }

    /// <summary><c>plan.session</c> with a <c>plan.round</c> child per loop round (PlanningLoopRunner).</summary>
    private static void EmitPlanningTrace()
    {
        Section("Trace 2/5 — plan.session  (the Claude/GPT loop; two rounds nested as plan.round)");
        var t = DateTime.UtcNow;
        var session = Start("plan.session", t);
        session?.SetTag("ember.repo", "ember");

        var round1 = Start("plan.round", t.AddMilliseconds(250));
        round1?.SetTag("ember.round", 1);
        Finish(round1, t.AddMilliseconds(250 + 1240)); // critic raised 2 issues

        var round2 = Start("plan.round", t.AddSeconds(3.0));
        round2?.SetTag("ember.round", 2);
        Finish(round2, t.AddSeconds(3.0 + 0.92)); // critic approved

        Finish(session, t.AddSeconds(5.10));
    }

    /// <summary><c>gate.fire</c> — the soft gate elapsing to a build (GateService).</summary>
    private static void EmitGateTrace()
    {
        Section("Trace 3/5 — gate.fire  (the soft gate elapsing; GateService)");
        var t = DateTime.UtcNow;
        var span = Start("gate.fire", t);
        span?.SetTag("ember.gate_reason", "approved");
        Finish(span, t.AddMilliseconds(14));
    }

    /// <summary><c>build.run</c> with a nested <c>pr.open</c> — a build that reaches a draft PR.</summary>
    private static void EmitSuccessfulBuildTrace()
    {
        Section("Trace 4/5 — build.run  (the headless build; pr.open nested for the PR handoff)");
        const string branch = "ember/add-rate-limit-headers-094879";
        var t = DateTime.UtcNow;

        var build = Start("build.run", t);
        build?.SetTag("ember.repo", "ember");
        build?.SetTag("ember.thread_id", "1504820333126094879");
        build?.SetTag("ember.branch", branch);
        build?.SetTag("ember.build.outcome", "success");

        var prStart = t.AddSeconds(202.4);
        var pr = Start("pr.open", prStart);
        pr?.SetTag("ember.branch", branch);
        pr?.SetTag("ember.pr_url", "https://github.com/djcdevelopment/ember/pull/42");
        Finish(pr, prStart.AddSeconds(2.10));

        Finish(build, t.AddSeconds(204.61));
    }

    /// <summary><c>build.run</c> ending in failure — span status set to Error, no PR child.</summary>
    private static void EmitFailedBuildTrace()
    {
        Section("Trace 5/5 — build.run  (a second session; the build fails — span status Error)");
        var t = DateTime.UtcNow;

        var build = Start("build.run", t);
        build?.SetTag("ember.repo", "ember");
        build?.SetTag("ember.thread_id", "1504820333126094999");
        build?.SetTag("ember.branch", "ember/retry-flaky-uploads-094999");
        build?.SetTag("ember.build.outcome", "failed");
        build?.SetStatus(ActivityStatusCode.Error, "the builder exited with code 1");
        Finish(build, t.AddSeconds(47.20));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>Starts a span on ember's real ActivitySource, anchored to a synthetic start time.</summary>
    private static Activity? Start(string name, DateTime startUtc)
    {
        var activity = Telemetry.Activity.StartActivity(name, ActivityKind.Internal);
        activity?.SetStartTime(startUtc);
        return activity;
    }

    /// <summary>
    /// Stamps the end time and stops the span. Setting the end time fixes a non-zero
    /// duration, which <see cref="Activity.Stop"/> then preserves — so the demo runs
    /// instantly while the spans still read with realistic durations.
    /// </summary>
    private static void Finish(Activity? activity, DateTime endUtc)
    {
        activity?.SetEndTime(endUtc);
        activity?.Dispose();
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        Console.WriteLine($"  {title}");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("ember — synthetic OpenTelemetry trace demo");
        Console.WriteLine();
        Console.WriteLine("Emits the spans a real /plan -> draft-PR run produces — real span names");
        Console.WriteLine("and tags — to the console exporter. No Discord, no model calls, no build.");
        Console.WriteLine();
        Console.WriteLine("Expected shape (each top-level span below is its own trace):");
        Console.WriteLine();
        Console.WriteLine("  command /plan                                    ~38ms");
        Console.WriteLine("  plan.session            {ember.repo}             ~5.1s");
        Console.WriteLine("    plan.round            {ember.round=1}          ~1.2s");
        Console.WriteLine("    plan.round            {ember.round=2}          ~0.9s");
        Console.WriteLine("  gate.fire               {ember.gate_reason}      ~14ms");
        Console.WriteLine("  build.run               {repo, thread_id, ...}   ~3.4m");
        Console.WriteLine("    pr.open               {branch, pr_url}         ~2.1s");
        Console.WriteLine();
        Console.WriteLine("Each OpenTelemetry block below is one span: TraceId/SpanId/ParentId tie");
        Console.WriteLine("the tree together; Tags carry ember's attributes.");
    }

    private static void PrintFooter()
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        Console.WriteLine("Demo complete — 5 traces (8 spans) and 3 metrics emitted above.");
        Console.WriteLine("Set Otel:Endpoint to ship the same signals to an OTLP collector.");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
    }
}
