# ember — architecture

How ember is put together: the components, how data flows between them, the
session state machine, the data contracts, and the telemetry. For *why* the
big decisions were made, see the [ADRs](adr); for the phased build narrative,
see [PLAN.md](../PLAN.md) and the [retrospective](retrospective.md).

ember is one always-on .NET host process. A `/plan` invocation becomes a
long-lived **session** — one Discord thread, one SQLite row — that moves
through a fixed state machine: a Claude/GPT planning loop, a soft approval
gate, a headless build, and a draft-PR handoff.

---

## 1. System dataflow

```mermaid
flowchart LR
    OP([Operator])

    subgraph DISCORD["Discord"]
        CMD["/plan · /status · /abort"]
        THREAD["Session thread"]
    end

    subgraph HOST["ember — .NET Generic Host, always-on"]
        BOT["DiscordBotService"]
        LOOP["PlanningLoopRunner"]
        GATE["GateService"]
        QUEUE["BuildQueue · FIFO"]
        RUNNER["BuilderRunner"]
        PR["PullRequest"]
        REC["RecoveryService"]
        DB[("SessionStore<br/>SQLite")]
    end

    MODELS["Planner = Claude<br/>Critic = GPT<br/>via IChatClient"]
    CC["headless Claude Code<br/>claude -p --output-format stream-json"]
    WT["git worktree<br/>branch ember/&lt;slug&gt;"]
    GH["GitHub<br/>pushed branch + draft PR"]
    JAEGER[("Jaeger<br/>OTLP :4317")]

    OP --> CMD --> BOT
    BOT --> LOOP
    LOOP <--> MODELS
    LOOP --> GATE
    GATE -->|enqueue| QUEUE --> RUNNER
    RUNNER --> CC --> WT
    RUNNER -->|on success| PR --> GH
    BOT -.-> DB
    LOOP -.-> DB
    GATE -.-> DB
    RUNNER -.-> DB
    PR -.-> DB
    REC -.-> DB
    LOOP -->|status posts| THREAD
    GATE -->|status posts| THREAD
    RUNNER -->|status posts| THREAD
    PR -->|status posts| THREAD
    THREAD --> OP
    HOST ==>|spans + metrics| JAEGER
```

Every arrow into `SessionStore` is the durability seam: the session row is the
source of truth, so the pipeline survives a process restart. The in-memory
drivers (the planning loop, the build queue) do not — see §2 and ADR 8.

---

## 2. Session state machine

One session, one Discord thread, one `sessions` row. Four active states and
two terminal off-ramps.

```mermaid
stateDiagram-v2
    [*] --> PLANNING: /plan
    PLANNING --> AWAITING_GATE: critic approved · round cap · stalled
    AWAITING_GATE --> BUILDING: countdown elapsed
    BUILDING --> PR_OPEN: build ok + draft PR opened

    PLANNING --> ABORTED: /abort
    AWAITING_GATE --> ABORTED: stop-reaction or /abort
    BUILDING --> ABORTED: /abort (worktree kept)

    PLANNING --> FAILED: error · restart
    AWAITING_GATE --> FAILED: error
    BUILDING --> FAILED: build error · PR handoff failed · restart

    PR_OPEN --> [*]
    ABORTED --> [*]
    FAILED --> [*]
```

Restart behaviour differs by state, and the difference is deliberate:

- **`AWAITING_GATE`** is the only active state that *survives* a restart. The
  gate is just a persisted deadline (`gate_expires_at`); `GateService` re-arms
  a window that elapsed while ember was down, never auto-firing it (ADR 5).
- **`PLANNING`** and **`BUILDING`** are driven by in-memory tasks that the
  restart kills. `RecoveryService` marks them `FAILED` rather than resuming
  non-idempotent work (ADR 8).

`gate_reason` (`approved` · `round_cap` · `stalled`) rides on the session so a
plan forced forward by a backstop is never shown as endorsed.

---

## 3. End-to-end sequence — `/plan` to draft PR

```mermaid
sequenceDiagram
    actor OP as Operator
    participant D as Discord
    participant E as ember
    participant M as Models · Claude/GPT
    participant C as Claude Code
    participant G as GitHub

    OP->>D: /plan brief repo
    D->>E: interaction, owner-checked
    E->>D: open thread · session = PLANNING

    loop planning rounds, until approved or backstop
        E->>M: Claude drafts or revises the plan
        E->>M: GPT critiques · structured verdict
        E->>D: post round to the thread
    end

    E->>D: post converged plan · session = AWAITING_GATE
    Note over E,D: soft gate — ~5 min veto window, stop-reaction or /abort
    E->>E: countdown elapses · session = BUILDING · enqueue

    E->>C: claude -p in a fresh git worktree
    C-->>E: stream-json events
    E->>D: throttled build status, one edited message
    C->>C: implement plan, commit on the build branch
    E->>G: git push + gh pr create --draft
    E->>D: post PR url · session = PR_OPEN
```

`/abort` and `/status` are not shown: `/status` reads the session row at any
time; `/abort` cancels whatever is in flight for the thread (loop, gate, or
build) per the state machine in §2.

---

## 4. Hosted services and composition

ember registers four `IHostedService`s. Start order matters once:
`RecoveryService` runs first so the database is consistent before any other
service acts on it.

```mermaid
flowchart TB
    subgraph BOOT["Hosted services — started in registration order"]
        direction TB
        H1["1 · RecoveryService<br/>stale PLANNING/BUILDING → FAILED, clean worktrees"]
        H2["2 · DiscordBotService<br/>connect gateway, register slash commands"]
        H3["3 · GateService<br/>boot reconcile + 20s countdown poll"]
        H4["4 · BuildQueue<br/>drain the FIFO build channel"]
        H1 --> H2 --> H3 --> H4
    end

    subgraph DI["Singletons resolved by DI"]
        direction TB
        S1["SessionStore · ThreadGateway"]
        S2["Planner · Critic · PlanningLoopRunner"]
        S3["BuilderRunner · PullRequest"]
        S4["IChatClient ×2 — keyed 'planner' / 'critic'"]
    end

    BOOT -.uses.-> DI
```

`BuildQueue` is registered both as a singleton (so `GateService` and
`AbortCommand` can reach it) and as the hosted service that drains it.

---

## 5. Tech stack

```mermaid
flowchart TB
    A["Control surface — Discord.Net 3.19 · gateway, slash commands, threads"]
    B["Runtime — C# / .NET 9 · Generic Host (Microsoft.Extensions.Hosting)"]
    C["Planning — Microsoft.Extensions.AI IChatClient · one OpenAI-compatible adapter"]
    D["Builder — headless Claude Code CLI · child process, stream-json"]
    E["State — SQLite (Microsoft.Data.Sqlite) · one sessions table, WAL"]
    F["Observability — OpenTelemetry ActivitySource + Meter → OTLP → Jaeger"]

    A --> B
    B --> C
    B --> D
    B --> E
    B --> F
```

| Layer | Choice | ADR |
|---|---|---|
| Control surface | Discord, thread-per-session | [1](adr/0001-discord-control-surface.md) |
| Runtime | C# / .NET 9 | [2](adr/0002-csharp-dotnet-agent-framework.md) |
| Model access | one OpenAI-compatible `IChatClient` | [3](adr/0003-openai-compatible-ichatclient.md) |
| Builder | headless Claude Code via CLI | [4](adr/0004-builder-headless-claude-code.md) |
| Loop + gate | critic-driven, resumable soft gate | [5](adr/0005-critic-driven-loop-and-soft-gate.md) |
| Build throttle | global FIFO queue | [6](adr/0006-global-fifo-build-queue.md) |

---

## 6. Data contracts

### 6.1 `sessions` — the SQLite table

The single source of truth. One row per session, keyed by Discord thread id.

```mermaid
erDiagram
    sessions {
        TEXT    thread_id      PK "Discord thread id = session id"
        TEXT    state             "PLANNING|AWAITING_GATE|BUILDING|PR_OPEN|ABORTED|FAILED"
        TEXT    gate_reason       "approved|round_cap|stalled · null pre-gate"
        TEXT    repo              "allowlist key"
        TEXT    brief             "the operator's request"
        INTEGER current_round     "planning round counter"
        TEXT    plan_snapshot     "frozen converged plan · set at gate entry"
        TEXT    open_issues       "JSON · critic issues open at the gate"
        INTEGER gate_expires_at   "epoch ms · drives boot reconcile"
        TEXT    branch_name       "the build branch · ember-slugged"
        TEXT    worktree_path     "host path · nulled when the worktree is removed"
        TEXT    pr_url            "draft PR url · set at PR_OPEN"
        TEXT    last_error        "failure detail"
        INTEGER created_at        "epoch ms"
        INTEGER updated_at        "epoch ms"
    }
```

### 6.2 Critic verdict — model output contract

The critic model is instructed to return **only** this JSON object:

```json
{
  "assessment": "<one-sentence overall read>",
  "issues": [
    { "severity": "blocking|major|minor", "summary": "<the problem>", "fix": "<concrete fix>" }
  ]
}
```

ember derives the verdict from it (`CriticVerdict`): `OpenIssues` =
issues at `blocking` or `major`; `Approved` = no open issues. A malformed
response is retried once, then treated as **not approved** — a parse failure
must never read as success. The loop continues while open issues remain.

### 6.3 Builder event stream — `claude` stdout contract

`BuilderRunner` parses the headless builder's JSONL stdout. It consumes four
event types and ignores the rest:

| `type` | ember reads | Used for |
|---|---|---|
| `system` | init marker | "starting the builder…" |
| `assistant` | `message.content[]` — `text`, `tool_use` (name + input) | tool-action count, last activity line |
| `result` | `is_error`, `subtype`, `num_turns`, `total_cost_usd` | success/fail decision, final summary |
| `user` | tool results | (ignored — digest only) |

The digest is rendered into **one** Discord status message, edited on a ~4 s
throttle (never a message per event — Discord rate limits).

### 6.4 Plan snapshot artifact

At gate entry the converged plan is frozen into `plan_snapshot`. It is the
single downstream source of truth:

- shown at the gate,
- written into the build worktree as `.ember/PLAN.md` — the builder consumes
  this, never Discord history,
- used verbatim as the draft-PR body.

`.ember/` is kept out of the target repo via its shared `.git/info/exclude`,
so no tracked `.gitignore` churn lands in the operator's repo.

### 6.5 Telemetry signals

| Signal | Name | Tags |
|---|---|---|
| span | `command /<name>` | — |
| span | `plan.session` | `ember.repo` |
| span | `plan.round` | `ember.round` |
| span | `gate.fire` | `ember.gate_reason` |
| span | `build.run` | `ember.repo`, `ember.thread_id`, `ember.branch`, `ember.build.outcome` |
| span | `pr.open` | `ember.branch`, `ember.pr_url` |
| counter | `ember.commands.handled` | `command` |
| counter | `ember.builds.completed` | `outcome` |
| histogram | `ember.build.duration` (s) | `outcome` |

---

## 7. Observability — trace shape

A session's lifecycle is emitted as **four separate traces**, not one. The
gate countdown and the build span minutes and can cross a restart, so a single
long-lived span is not viable — see ADR 7.

```mermaid
flowchart TB
    subgraph T1["trace · command"]
        C1["command /plan"]
    end
    subgraph T2["trace · planning"]
        P1["plan.session"]
        P2["plan.round · round 1"]
        P3["plan.round · round 2"]
        P1 --> P2
        P1 --> P3
    end
    subgraph T3["trace · gate"]
        G1["gate.fire"]
    end
    subgraph T4["trace · build"]
        B1["build.run"]
        B2["pr.open"]
        B1 --> B2
    end
```

The traces correlate by tag (`ember.repo`, `ember.thread_id`), not by a shared
parent span. `dotnet run -- demo` emits this exact shape synthetically — see
the [README](../README.md#observability).

---

## 8. Trust boundary

The builder executes model-written code on the host *by design*. Isolation is
structural, not sandboxed:

- **Per-build git worktree** — the builder works only inside a fresh worktree
  on its own branch; it never touches the operator's working tree or `main`.
- **ember owns the remote** — the builder gets no push or PR credentials.
  Commit happens inside the worktree; `push` and `gh pr create` are ember's.
- **Draft PR only** — output is always a draft PR, never an auto-merge.
- **Soft-gate veto** — nothing builds until the operator's countdown elapses
  without an abort.
- **Owner lock** — all three commands accept only the configured operator id.

True network sandboxing of the builder is a deliberate non-goal for v1 (it
would break dependency restore — `dotnet` / `npm` / `nuget`).
