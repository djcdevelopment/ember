# ember

A Discord bot that runs an iterative **Claude / GPT planning loop** from a
personal Discord server, then hands the converged plan to a builder that
implements it.

Type `/plan` in your server. ember opens a thread; Claude drafts an
implementation plan, GPT red-teams it, Claude revises — round after round —
until the critic is satisfied. After a soft approval gate, a builder (headless
Claude Code) implements the plan in a git worktree and opens a draft PR.

Personal dev tool, single operator — not a product.

## Pipeline

```
/plan <brief> <repo>
  -> PLANNING        Claude/GPT loop until the critic is satisfied
  -> AWAITING_GATE   converged plan posted + a soft abort countdown
  -> BUILDING        headless Claude Code in a git worktree
  -> PR_OPEN         branch pushed, draft PR opened
```

`ABORTED` (operator) and `FAILED` (error) are off-ramps from any active state.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Host, SQLite session store, owner-locked `/plan` `/status` `/abort`, OpenTelemetry | Built |
| 1 | The Claude/GPT planning loop and the resumable soft gate | Built |
| 2 | The builder — headless Claude Code in a git worktree | Built |
| 3 | PR handoff and hardening | Built |

Full design: [PLAN.md](PLAN.md). Architecture decisions: [docs/adr](docs/adr).

## Stack

- C# / .NET 9, Generic Host
- [Discord.Net](https://github.com/discord-net/Discord.Net) — the bot
- `Microsoft.Extensions.AI` — planner (Claude) and critic (GPT) via `IChatClient`
- `Microsoft.Data.Sqlite` — session state
- OpenTelemetry — tracing

## Setup

Prerequisites: the .NET 9 SDK; `git` and authenticated `gh` on the host; Claude Code installed and authenticated (the builder).

Non-secret settings live in `src/Ember/appsettings.json`. Secrets go in user-secrets:

```
cd src/Ember
dotnet user-secrets set "Discord:BotToken"      "<bot token>"
dotnet user-secrets set "Discord:ClientId"      "<application id>"
dotnet user-secrets set "Discord:GuildId"       "<server id>"
dotnet user-secrets set "Discord:OwnerUserId"   "<your user id>"
dotnet user-secrets set "Models:Planner:ApiKey" "<anthropic key>"
dotnet user-secrets set "Models:Critic:ApiKey"  "<openai key>"
```

List the repos ember may target under `Ember:Repos` in `appsettings.json`
(an allowlist of name -> absolute host path).

Invite the bot to your server with the `bot` and `applications.commands`
scopes; ember registers its slash commands on startup.

## Run

```
dotnet run --project src/Ember
```

## Observability

ember exports OpenTelemetry traces and metrics over OTLP. The dev setup targets
the shared Jaeger on this machine (Jaeger v2 all-in-one, built-in OTLP receiver):

```powershell
# start Jaeger — idempotent, no-op if it is already up
& "D:\World of Warcraft\Tempo\infra\start-jaeger.ps1"
```

- `Otel:Endpoint` is set to `http://localhost:4317` in `appsettings.Development.json`
- Jaeger UI: http://localhost:16686 (service: `ember`)

To see the telemetry without a live run, emit a synthetic — but faithful — trace
set: the real span names and tags a `/plan` run produces, no Discord or model calls.

```
dotnet run --project src/Ember -- demo           # console exporter only
dotnet run --project src/Ember -- demo --otlp     # also ships traces to Jaeger
```

ember has no inbound listener — it is a Discord gateway client — so it owns no
ports. It is tracked in **portmap** as project `ember` with no services, and
exports to the shared Jaeger collector (portmap project `tempo`, OTLP `:4317`).

## Commands

| Command | Effect |
|---|---|
| `/plan <brief> <repo>` | Opens a thread and starts the planning loop |
| `/status` | Reports the session state for the current thread |
| `/abort` | Cancels the session — loop, gate, or build |

All three are locked to the configured owner.

## Project layout

```
src/Ember/
  Program.cs                    host, DI, OpenTelemetry
  appsettings.json              non-secret configuration
  Config/Options.cs             strongly-typed options
  Sessions/                     Session model + SQLite SessionStore
  Discord/                      bot service, slash commands, thread helpers
  Loop/                         Planner, Critic, CriticVerdict, PlanningLoopRunner
  Gate/GateService.cs           the resumable soft-gate poller
  Build/                        Worktree, PlanArtifact, BuilderRunner, BuildQueue, PullRequest
  Sessions/RecoveryService.cs   boot recovery for interrupted sessions
  Demo/TraceDemo.cs             synthetic OTel trace demo (dotnet run -- demo)
  Models/ChatClientFactory.cs   IChatClient builder
  Observability/Telemetry.cs    ActivitySource + Meter
```
