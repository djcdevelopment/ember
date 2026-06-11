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

Beyond the four phases, the planner/critic loop is being rehearsed on local
models — see the [local-loop retrospective](docs/retrospective-local-loop.md) —
and ember carries the first two ember-side layers of the constellation-awareness
plan (`D:\work\gad\pm\constellation-awareness-plan.md`): round-1 context from the
code knowledge graph ([ADR 13](docs/adr/0013-graph-context.md)) and the Reflect
dual-judge recap subsystem, disabled by default
([ADR 14](docs/adr/0014-reflect-dual-judge-recap.md)).

## Documentation

| Doc | What |
|---|---|
| [PLAN.md](PLAN.md) | The full design and the phased build plan |
| [docs/architecture.md](docs/architecture.md) | Diagrams — dataflow, state machine, sequence, data contracts, telemetry |
| [docs/local-loop-runbook.md](docs/local-loop-runbook.md) | Running the planner/critic on local models |
| [docs/reflect-enable-runbook.md](docs/reflect-enable-runbook.md) | The operator evening — enabling the Reflect recap, phase by phase |
| [docs/adr](docs/adr) | Architecture decision records (Nygard-style) |
| [docs/retrospective.md](docs/retrospective.md) | Build retrospective — Phases 0–3 |
| [docs/retrospective-local-loop.md](docs/retrospective-local-loop.md) | Local-loop rehearsal retrospective |
| [RnD/](RnD) | Research notebook — local inference, the loop, the builder, observability, Discord |

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

The bot:

```
dotnet run --project src/Ember
```

Or run just the planning loop on the console — no Discord, no gate, no build.
This is the quickest way to exercise the loop or try a local critic:

```
dotnet run --project src/Ember -- plan "add a /ping command" ember
```

### Local models

The planner and critic reach models through one OpenAI-compatible `IChatClient`
([ADR 3](docs/adr/0003-openai-compatible-ichatclient.md)), so pointing either at
a local model is a config change. The validated local stack is the dual Intel
Arc Pro B70 box: **llama.cpp `llama-server` (Vulkan, native Windows), one
endpoint per card** — no swap proxy needed
([ADR 12](docs/adr/0012-windows-phase-llamacpp-vulkan.md)); vLLM is deferred to
the Linux migration. The committed Development config still carries the earlier
4070 Ti rehearsal shape — Ollama through the sole-residency swap proxy
([`tools/OllamaSwapProxy`](tools/OllamaSwapProxy)) — until the loop's first run
against the `llama-server` pair re-points it. Both procedures are in the
[local-loop runbook](docs/local-loop-runbook.md). The base `appsettings.json`
keeps the hosted planner and critic.

The critic requests JSON-object mode, which keeps smaller local models'
structured verdicts parseable.

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
| `/reflect` | Runs the constellation recap now (requires Reflect enabled) |

All commands are locked to the configured owner.

## Reflect

A scheduled (default 03:00 local) dual-judge recap of the constellation's
committed work since the last recap: git supplies each repo's delta, the code
knowledge graph enriches it with the symbols behind the changed files, two
local models on the vllama facade write independent recaps, and a structured
comparison surfaces their divergences. The recap posts as a `reflect:` thread;
reacting ✅ / ✏️ / ❌ on it persists your verdict — the label corpus the later
adaptation loop trains against. See [ADR 14](docs/adr/0014-reflect-dual-judge-recap.md).

**Disabled by default.** To enable: set `Ember:Reflect:Enabled` to `true` and
`Ember:Reflect:ChannelId` to the target channel. The pipeline never launches a
model server — if the vllama endpoints are down, the run fails soft and the
night re-reports tomorrow.

Validate the pipeline read-only from the console — no Discord, no database,
baseline resolved from git:

```
dotnet run --project src/Ember -- reflect --dry-run --since-hours 48
dotnet run --project src/Ember -- reflect                # judges too, if endpoints are up
```

## Project layout

```
src/Ember/
  Program.cs                    host, DI, OpenTelemetry
  appsettings.json              non-secret configuration
  Config/Options.cs             strongly-typed options
  Sessions/                     Session model + SQLite SessionStore
  Discord/                      bot service, slash commands, thread helpers
  Loop/                         Planner, Critic, PlanningLoopRunner, RepoContext, GraphContext
  Gate/GateService.cs           the resumable soft-gate poller
  Build/                        Worktree, PlanArtifact, BuilderRunner, BuildQueue, PullRequest
  Reflect/                      dual-judge recap — evidence, judges, comparer, scheduler, store
  Sessions/RecoveryService.cs   boot recovery for interrupted sessions
  Cli/PlanCli.cs                console planning-loop runner (dotnet run -- plan)
  Cli/ReflectCli.cs             console reflect runner (dotnet run -- reflect)
  Demo/TraceDemo.cs             synthetic OTel trace demo (dotnet run -- demo)
  Models/ChatClientFactory.cs   IChatClient builder
  Observability/Telemetry.cs    ActivitySource + Meter
tools/
  OllamaSwapProxy/              sole-residency model swapper for the local loop
scripts/
  serve-local.sh                vLLM launcher for the Arc-target local path
docs/
  architecture.md               diagrams — dataflow, state machine, contracts
  local-loop-runbook.md         running the planner/critic on local models
  retrospective.md              build retrospective (Phases 0–3)
  retrospective-local-loop.md   local-loop rehearsal retrospective
  adr/                          architecture decision records
RnD/                            research notebook — inference, loop, builder, …
```
