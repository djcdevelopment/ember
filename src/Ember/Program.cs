using Discord;
using Discord.WebSocket;
using Ember.Build;
using Ember.Cli;
using Ember.Config;
using Ember.Demo;
using Ember.Discord;
using Ember.Discord.Interactions;
using Ember.Gate;
using Ember.Loop;
using Ember.Manifest;
using Ember.Models;
using Ember.Observability;
using Ember.Reflect;
using Ember.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// `dotnet run -- demo` emits a synthetic OTel trace set to the console and exits —
// it never starts the bot. See Demo/TraceDemo.cs.
if (TraceDemo.IsRequested(args))
{
    await TraceDemo.RunAsync(TraceDemo.OtlpEndpoint(args));
    return 0;
}

// `dotnet run -- plan "<brief>" <repo>` runs the planning loop on the console and exits.
// See Cli/PlanCli.cs.
if (PlanCli.IsRequested(args))
{
    return await PlanCli.RunAsync(args);
}

// `dotnet run -- reflect [--dry-run] [--since-hours N]` runs the reflect pipeline on the
// console and exits — no Discord, no persistence. See Cli/ReflectCli.cs.
if (ReflectCli.IsRequested(args))
{
    return await ReflectCli.RunAsync(args);
}

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.Section));
builder.Services.Configure<EmberOptions>(builder.Configuration.GetSection(EmberOptions.Section));
builder.Services.AddSingleton<IPostConfigureOptions<EmberOptions>, EmberOptionsPostConfigure>();
builder.Services.Configure<ModelsOptions>(builder.Configuration.GetSection(ModelsOptions.Section));
builder.Services.Configure<OtelOptions>(builder.Configuration.GetSection(OtelOptions.Section));

// ── Sessions ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<SessionStore>();

// ── Chat clients (planner = Claude, critic = GPT, reflect judges = local) ─────
builder.Services.AddKeyedSingleton<IChatClient>("planner", (sp, _) =>
    ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.Planner));
builder.Services.AddKeyedSingleton<IChatClient>("critic", (sp, _) =>
    ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.Critic));
builder.Services.AddKeyedSingleton<IChatClient>("reflectA", (sp, _) =>
    ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.ReflectA));
builder.Services.AddKeyedSingleton<IChatClient>("reflectB", (sp, _) =>
    ChatClientFactory.Create(sp.GetRequiredService<IOptions<ModelsOptions>>().Value.ReflectB));

// ── Discord ───────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
    LogLevel = LogSeverity.Info,
}));
builder.Services.AddSingleton<ThreadGateway>();
builder.Services.AddSingleton<ISlashCommand, PlanCommand>();
builder.Services.AddSingleton<ISlashCommand, StatusCommand>();
builder.Services.AddSingleton<ISlashCommand, AbortCommand>();
builder.Services.AddSingleton<ISlashCommand, ReflectCommand>();

// ── Planning loop + gate ──────────────────────────────────────────────────────
builder.Services.AddSingleton<Planner>();
builder.Services.AddSingleton<Critic>();
builder.Services.AddSingleton<ManifestLoader>();
builder.Services.AddSingleton<GraphContext>();
builder.Services.AddSingleton<PlanningLoopRunner>();

// ── Reflect (dual-judge recap) ────────────────────────────────────────────────
builder.Services.AddSingleton<RecapStore>();
builder.Services.AddSingleton<JournalWriter>();
builder.Services.AddSingleton<EvidenceAssembler>();
builder.Services.AddSingleton<DivergenceComparer>();
builder.Services.AddSingleton<ReflectRunner>();
builder.Services.AddSingleton<ReflectExecutor>();

// ── Builder ───────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<PullRequest>();
builder.Services.AddSingleton<BuilderRunner>();
builder.Services.AddSingleton<BuildQueue>();

// RecoveryService is registered first: its StartAsync flips stale active sessions to FAILED
// before GateService / BuildQueue start.
builder.Services.AddHostedService<RecoveryService>();
builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<GateService>();
builder.Services.AddHostedService<ReflectService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BuildQueue>());

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
var otelOptions = builder.Configuration.GetSection(OtelOptions.Section).Get<OtelOptions>() ?? new OtelOptions();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otelOptions.ServiceName))
    .WithTracing(tracing =>
    {
        tracing.AddSource(Telemetry.SourceName);
        tracing.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(otelOptions.Endpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otelOptions.Endpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(Telemetry.SourceName);
        if (!string.IsNullOrWhiteSpace(otelOptions.Endpoint))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otelOptions.Endpoint));
    });

var host = builder.Build();

// Create the SQLite schema before the bot accepts commands.
host.Services.GetRequiredService<SessionStore>().Initialize();
host.Services.GetRequiredService<RecapStore>().Initialize();

host.Run();
return 0;
