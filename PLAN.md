# ember — Implementation Plan

Status: Phases 0-3 built (2026-05-15) — the full `/plan` -> draft PR pipeline.
The local-loop rehearsal (ADR 10) ran on the 4070 Ti via Ollama and a swap
proxy; the Arc hardware then landed and validated llama.cpp Vulkan as the
Windows-phase stack (ADR 12, superseding ADR 9's vLLM call). Post-v1, ember
carries the constellation-awareness layers: knowledge-graph round-1 context
(ADR 13) and the Reflect dual-judge recap — disabled by default, operator-triggered on this rig (ADRs 14, 17) —
plan of record in `D:\work\gad\pm\constellation-awareness-plan.md`.

## What ember is

A Discord bot for a personal server. `/plan <brief> <repo>` kicks off an
autonomous planning loop between two LLMs — Claude authors a plan, GPT
red-teams it — then, after a soft approval gate, auto-launches a local
builder (headless Claude Code) that implements the plan in a git worktree
and opens a draft PR. Each request runs in its own Discord thread.

Personal dev tool, single operator — not a product.

## Pipeline

```
/plan <brief> <repo>
  -> PLANNING        Claude<->GPT loop until the critic is satisfied
  -> AWAITING_GATE   post converged plan + ~5-min abort countdown
  -> BUILDING        headless Claude Code in a git worktree
  -> PR_OPEN         push branch, open draft PR
```

Off-ramps: `ABORTED` (operator) and `FAILED` (error) from any active state.

## Stack

- **Runtime:** C# / .NET 8+, Generic Host (`Microsoft.Extensions.Hosting`) — long-running worker.
- **Bot:** Discord.Net — gateway, slash commands, threads, reactions.
- **Agents:** the planner and critic call `Microsoft.Extensions.AI.IChatClient` directly (the abstraction underneath Microsoft Agent Framework, 1.0 GA April 2026).
- **Models:** one OpenAI-compatible `IChatClient` adapter serves every provider — OpenAI, Ollama, and Anthropic via its OpenAI-compatible endpoint — selected by config. See `docs/adr/0003`.
- **Builder:** headless Claude Code via CLI — `claude -p --output-format stream-json`, run as a child process. The CLI is the language-neutral headless interface; there is no C# Agent SDK.
- **State:** SQLite via `Microsoft.Data.Sqlite` — one `sessions` table.
- **Observability:** OpenTelemetry — `UseOpenTelemetry()` on the `IChatClient` pipeline (model + tool spans, GenAI semantic conventions) plus ember's own `ActivitySource`. OTLP export.

## Project layout

```
Ember.sln
PLAN.md
src/Ember/
  Program.cs                Host builder — DI, config, OTel, hosted services
  appsettings.json          non-secret config (secrets via env / user-secrets)
  Discord/
    DiscordBotService.cs    IHostedService — gateway connect, command registration
    Interactions/           PlanCommand, StatusCommand, AbortCommand
    ThreadHelpers.cs        thread creation, status-message helpers
  Sessions/
    Session.cs              record + SessionState / GateReason enums
    SessionStore.cs         SQLite access
  Loop/
    PlanningLoop.cs         rounds + termination
    Planner.cs              Claude author
    Critic.cs               GPT critic — structured verdict
    CriticVerdict.cs        verdict / issue records
  Gate/
    GateService.cs          IHostedService — countdown poller + boot reconcile
  Build/
    BuildQueue.cs           FIFO safety throttle
    Worktree.cs             git worktree add / remove
    BuilderRunner.cs        claude CLI process + stream-json parse
    PullRequest.cs          push + gh pr create --draft
  Observability/
    Telemetry.cs            ActivitySource + Meter definitions
```

## Session state machine

States: `PLANNING -> AWAITING_GATE -> BUILDING -> PR_OPEN`, with `ABORTED`
and `FAILED` as terminal off-ramps.

- `/plan` -> create thread, insert session (`PLANNING`), start loop.
- Critic verdict `approved`, or a backstop fires -> `AWAITING_GATE`: snapshot the plan, set `gate_expires_at`, post the gate message.
- Stop-reaction or `/abort` during the window -> `ABORTED`. Countdown elapses -> `BUILDING`.
- Build completes -> push + draft PR -> `PR_OPEN`. Build errors -> `FAILED` (log tail posted).

`gate_reason` (`approved | round_cap | stalled`) is carried on the session so
the gate message never implies false confidence — `approved` shows
"critic approved"; `round_cap` / `stalled` show "forced forward — N open
issues" with the issues listed.

## Data model — `sessions` (SQLite)

```
thread_id        TEXT PRIMARY KEY    Discord thread id = session id
state            TEXT NOT NULL       PLANNING|AWAITING_GATE|BUILDING|PR_OPEN|ABORTED|FAILED
gate_reason      TEXT                approved|round_cap|stalled  (null pre-gate)
repo             TEXT NOT NULL       allowlist key
brief            TEXT NOT NULL
current_round    INTEGER DEFAULT 0
plan_snapshot    TEXT                frozen converged plan (set at gate entry)
open_issues      TEXT                JSON — critic issues open at gate (null if approved)
gate_expires_at  INTEGER             epoch ms — for boot reconcile
branch_name      TEXT
worktree_path    TEXT
pr_url           TEXT
last_error       TEXT
created_at       INTEGER NOT NULL
updated_at       INTEGER NOT NULL
```

## The planning loop

- **Planner (Claude):** drafts plan v1 from brief + light repo context (README + top-level tree); on later rounds, revises against the critic's open issues plus any operator steering.
- **Critic (GPT):** returns structured output — `{ verdict, issues: [{ severity, summary, fix }] }`. Instructed to raise only issues that would make the build fail or build the wrong thing — not style. The structured-output parse retries once on malformed output (matters when the critic later runs on a local model).
- **Termination:** loop continues while `blocking` / `major` issues remain. Backstops: hard round cap (default 6) -> `gate_reason = round_cap`; a round that closes zero issues -> `gate_reason = stalled`. Clean finish -> `gate_reason = approved`.
- **Operator steering:** any message the operator posts in the thread mid-loop is folded into the next planner revision.
- Each round posts to the thread, attributed (planner vs critic).

## The gate

- On entering `AWAITING_GATE`: freeze the converged plan into `plan_snapshot`, set `gate_expires_at = now + countdown`, post the plan + gate message.
- **Soft gate:** elapses to `BUILDING` unless a stop-reaction or `/abort` arrives first.
- **Resumable** (`GateService`, an `IHostedService`):
  - Steady state: polls SQLite every ~20s for `AWAITING_GATE` sessions past `gate_expires_at` and fires them.
  - Boot reconcile (one-time, before steady polling): for each `AWAITING_GATE` session — `gate_expires_at` in the future -> leave it (the steady poller will catch it); `gate_expires_at` already past -> the bot was down through the window -> do **not** auto-build: check the gate message's reactions (a stop-reaction -> `ABORTED`), otherwise convert to a hard gate ("ember was down — react to build, `/abort` to drop").

## Plan snapshot artifact

The frozen `plan_snapshot` is the single source of truth downstream:

- shown at the gate,
- written into the worktree as `.ember/PLAN.md` for the builder to consume (the builder never reads Discord history),
- used as the draft-PR body.

`.ember/` is kept out of the target repo via `.git/info/exclude` (local-only —
no tracked `.gitignore` churn).

## The build

- `git worktree add` a fresh directory + branch `ember/<slug>` off the repo's default branch.
- `BuilderRunner` spawns `claude -p --output-format stream-json` with the worktree as working directory; the kickoff prompt points the builder at `.ember/PLAN.md`.
- stream-json events are parsed and digested into **one** Discord status message that ember keeps editing — never a message per event (Discord rate limits).
- **Build queue:** global FIFO — a deliberate **safety throttle** (one build at a time across all repos, so two runners can't contend for the host or API limits), not a scaling decision. Per-repo concurrency is a later option.
- **Builder permissions:** the builder edits files and runs build / test / dependency-restore commands (`dotnet`, `npm`, `nuget`, ...) inside the worktree. It does **not** get git push / PR credentials — ember owns the remote. True network sandboxing is an explicit opt-in, not the default.
- The builder works on its own branch in the worktree; it **never merges to main**.

## PR handoff

On build success: `git push` the branch, then `gh pr create --draft` with
`plan_snapshot` as the body. Post the PR URL to the thread -> `PR_OPEN`. The
worktree is removed; the branch + PR are kept.

## `/abort` semantics by state

| State | `/abort` |
|---|---|
| PLANNING | stop the loop |
| AWAITING_GATE | cancel the countdown |
| BUILDING | cancel the runner if possible, mark `ABORTED`, keep the worktree + post its path |
| PR_OPEN | no-op — re-post the PR link / status |

The command + state-flip ship in Phase 0; real cancellation (a
`CancellationTokenSource` per in-flight operation) wires up in the phase that
adds each state.

## Observability

One end-to-end trace per `/plan`:

- `ember.session` root span (tags: repo, thread id).
- `ember.plan.round` per loop round; MAF emits nested model-call spans (model, token usage) for each planner / critic call automatically.
- `ember.gate` span (gate_reason, outcome).
- `ember.build` span wrapping the builder run; Claude Code's own OTel export can be ingested alongside.
- `ember.pr` span.

Metrics: sessions-by-outcome counter, rounds-per-session and build-duration
histograms, token usage (MAF-provided). OTLP export to the Aspire dashboard /
Jaeger / Application Insights.

## Provider strategy

Planner and critic reach models only through `IChatClient`. As built, a single
OpenAI-compatible adapter serves every provider — OpenAI, Ollama, and Anthropic
(via its OpenAI-compatible endpoint) — selected by config (see `docs/adr/0003`).
Switching the planner or critic to a local model is a config change, not a code
change. The builder (Claude Code) stays on a frontier Anthropic model;
localizing it is out of scope for v1.

## Security

- All three commands are locked to the operator's Discord user ID.
- `/plan` accepts only repos on a configured allowlist (name -> host path); no arbitrary paths.
- The builder executes code on the host by design — isolation comes from the per-build worktree, draft-PR-only output (no auto-merge), and the soft-gate veto.

## Configuration

`appsettings.json` for non-secrets; environment variables / `dotnet
user-secrets` for tokens and keys.

```
Discord:BotToken / ClientId / GuildId
Ember:OwnerUserId
Ember:Repos              allowlist (name -> absolute host path)
Ember:GateCountdown      default 00:05:00
Ember:MaxPlanRounds      default 6
Models:Planner / Critic  connector + model id
Anthropic / OpenAI keys  planner + critic (builder uses Claude Code's own auth)
Otel:Endpoint            OTLP exporter target
```

Host prerequisites: always-on; `git`, `gh` (authenticated), and Claude Code
(authenticated) installed; the allowlisted repos present.

## Phases

Each phase is independently testable.

**Phase 0 — skeleton + schema + plumbing.** .NET host with DI / config /
logging; `SessionStore` with the full schema; `DiscordBotService` connecting
the gateway (`Guilds | GuildMessages | MessageContent`) and registering
`/plan /status /abort` guild-scoped; owner-lock; `/plan` creates thread +
session, `/status` reads, `/abort` flips state per the table; `IChatClient`
registered in DI (not yet called); OpenTelemetry wired with OTLP export.
*Deliverable:* bot online, commands work, sessions persist, command spans
visible in traces — no LLM calls.

**Phase 1 — planning loop + gate.** `Planner`, `Critic` (structured verdict),
`PlanningLoop` with critic-driven termination + backstops + `gate_reason`;
mid-thread steering; plan snapshot on gate entry; `GateService` with steady
poller + boot reconcile; soft-gate countdown + abort-reaction; real `/abort`
for `PLANNING` and `AWAITING_GATE`. Gate firing hits a stub.
*Deliverable:* full planning loop, traceable, converged plan + working
resumable gate — no builder.

**Phase 2 — builder.** `Worktree`; write `.ember/PLAN.md`; `BuilderRunner`
(claude CLI + stream-json -> throttled status message); `BuildQueue` FIFO
throttle; real `/abort` for `BUILDING` (kill process, keep worktree); build
wrapped in an OTel span. Stop before PR — leave the branch + diff stats.
*Deliverable:* gate firing runs a real build.

**Phase 3 — PR + hardening.** `PullRequest` (push + draft PR, snapshot as
body); `PLANNING` / `BUILDING` boot recovery (mark `FAILED`, clean orphaned
worktrees via `worktree_path`); optional per-repo queue; cleanup policy.
*Deliverable:* full `/plan` -> draft PR pipeline.

## Deferred (out of scope for v1)

- Parallel / per-repo concurrent builds.
- Localizing the builder to a non-Anthropic model.
- Persisted per-round history beyond `current_round` (the thread + `plan_snapshot` are the record).
- Network sandboxing of the builder.
