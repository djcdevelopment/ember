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

To see the OpenTelemetry signals without a live run, emit a synthetic trace set
(real span names and tags, console exporter) — no Discord, no model calls:

```
dotnet run --project src/Ember -- demo
```

## Ports

ember has no inbound listener — it is a Discord gateway client. Its allocation
in **portmap** (`D:/work/start/portmap`, project id `ember`, range 18110-18119)
reserves ports for the local OpenTelemetry collector it exports to:

| Port | Service | Use |
|---|---|---|
| 18110 | aspire-dashboard | OTLP collector / dashboard UI |
| 18111 | aspire-otlp | OTLP gRPC ingest — point `Otel:Endpoint` here |

Before changing ports, check portmap:
`python D:/work/start/portmap/portmap.py check <port>`.

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
