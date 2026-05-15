# ember — build retrospective

_Written 2026-05-15, after Phases 0–3, the trace demo, and the observability
wiring._

## What was built

The full `/plan → draft PR` pipeline, in four phases plus a telemetry demo:

| Phase | Scope | Commit |
|---|---|---|
| 0 | Host, SQLite session store, owner-locked commands, OpenTelemetry | initial |
| 1 | Claude/GPT planning loop, resumable soft gate | initial |
| 2 | Builder — headless Claude Code in a git worktree | `f630f37` |
| 3 | PR handoff, boot recovery, retention cleanup | `12c1911` |
| — | Synthetic OTel trace demo, portmap registration | `69b11b3` |
| — | Wire telemetry to the shared local Jaeger | `d27f524` |

Every phase compiled clean (0 warnings) and the host smoke-tested live —
connects to Discord, registers `/plan /status /abort`, all hosted services
resolve and start.

## What went well

- **The phase boundaries held.** Each phase was independently buildable and
  left the system in a runnable state. Phase 1 ended at a gate stub, Phase 2
  ended at a build that rested in `BUILDING`, Phase 3 closed the loop — no
  phase required reaching back into a prior one's design.
- **The session row as the single source of truth** paid off immediately in
  Phase 3: boot recovery is just "scan rows by state", because every state
  transition was already persisted.
- **Failure modes were designed in, not bolted on.** Worktree collisions,
  missing `claude`/`git`/`gh`, builder hangs, abort-mid-build, abort-while-
  queued, restart-mid-build — each has an explicit, tested-by-construction
  path. The rigorous-engineer review reflex ("what happens on restart?") was
  applied up front rather than discovered later.
- **The demo stayed honest.** It emits exactly the spans ember ships — same
  names, same tags, same four-separate-traces shape — rather than a prettier
  fiction. Verified end-to-end into Jaeger.

## Deviations from PLAN.md

| PLAN.md said | What shipped | Why |
|---|---|---|
| First-party MAF connectors per provider | One OpenAI-compatible `IChatClient` adapter | Lower surface area; provider-swap goal preserved. ADR 3. |
| One `ember.session` root span per `/plan` | Four separate per-stage traces | A span can't survive the ~5-min gate countdown or a restart. ADR 7. |
| Phase 2: "gate firing runs a real build" | A successful Phase-2 build rested in `BUILDING` | No `PR_OPEN` transition until Phase 3 — a known, documented intra-phase gap, closed in Phase 3. |
| OTLP to "Aspire dashboard / Jaeger / App Insights" | The machine's existing shared Jaeger v2 | An idempotent local Jaeger already existed; no reason to stand up a second collector. |

None of these changed the product; they are recorded so the plan and the code
do not silently disagree.

## Decisions made mid-build (not in PLAN.md)

- **Builder prompt over stdin**, not as a CLI argument — sidesteps every
  command-line quoting hazard for a multi-line prompt on Windows.
- **Executable resolution via `PATH` × `PATHEXT`** — `Process.Start` with
  `UseShellExecute=false` only finds `claude.exe`, not the npm `claude.cmd`
  shim; ember resolves it and wraps `.cmd` in `cmd.exe /c`.
- **ember commits the builder's leftover work.** If the builder changed files
  but did not commit, `PullRequest` stages and commits them rather than
  opening an empty PR or dropping the work.
- **`RecoveryService` runs first and recovers synchronously** in `StartAsync`,
  so the database is consistent before `GateService` / `BuildQueue` start —
  closing a race between recovery and a freshly-fired gate.
- **Base-ref resolution** — `origin/HEAD → main/master → HEAD` — so a build
  branch cuts from a sensible default whether or not the repo has a remote.

## What is NOT yet verified

This is the load-bearing section. The pipeline **compiles and the host
starts**; it has **not been run end to end**.

- **No live `/plan` run.** The planning loop, a real build, and a real PR
  handoff have never executed together. A live run needs Anthropic + OpenAI
  API keys (`dotnet user-secrets`) and an authenticated `gh`.
- **The `claude -p` invocation is unproven.** The flags (`--output-format
  stream-json`, the `--verbose` requirement, `--dangerously-skip-permissions`)
  and the stream-json field names (`is_error`, `num_turns`, `total_cost_usd`,
  the content-block shapes) come from knowledge of the CLI, not from a run
  against the installed version (2.1.142). Parsing is defensive, but the
  contract is assumed.
- **`gh pr create` is unproven** — the flag set and URL extraction are untested
  against a real repo.
- **The Discord status throttle** under a genuinely long build is designed but
  not observed against live rate limits.
- ~~**The critic on a local (Ollama) model.**~~ Verified 2026-05-15: the
  console loop (`dotnet run -- plan`) runs the critic on Ollama with
  JSON-object mode; `qwen3.5:9b` verdicts parsed cleanly on the first attempt.

## Risks carried forward

- The biggest risk is the gap between "compiles + starts" and "works" — every
  integration seam with an external process (`claude`, `git`, `gh`) is a
  plausible first-run failure point. They fail *loudly* (clear `FAILED`
  messages, worktree kept for inspection), which is the mitigation, but the
  first live run should be a small throwaway brief.
- The build queue is in-memory; a restart mid-`BUILDING` is handled (recovery
  → `FAILED`), but a restart between "gate fired" and "build dequeued" drops
  the queue entry — also caught by recovery, also surfaced as `FAILED`.
- `Otel:Endpoint` is set for the Development environment; a live run with
  Jaeger down produces a steady trickle of OTLP connection-error logs
  (harmless — the console exporter still works).

## Lessons

- **"Independently testable" meant "compiles and starts", not "exercised".**
  That is a fine phase boundary, but the distinction should be explicit — the
  carried risk lives entirely in that gap.
- **An OpenTelemetry span cannot model a process that pauses and restarts.**
  PLAN.md's single-`ember.session`-span sketch was aspirational; the
  implementation correctly diverged. Cross-stage correlation by tag (or, later,
  persisted trace context + span links) is the realistic pattern.
- **Launching a CLI on Windows is not free.** `PATHEXT`, `.cmd` shims, and
  argument quoting each cost real code. Worth budgeting for next time.

## Next

- A first live `/plan` run with a trivial brief against the `ember` repo
  allowlist entry — the real integration test.
- Optionally: persist W3C trace context on the session row so the four traces
  link across stages (ADR 7 names this).
- Deferred for v1 (unchanged): per-repo concurrent builds, a localized builder,
  network sandboxing.
