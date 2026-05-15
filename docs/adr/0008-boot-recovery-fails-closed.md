# 8. Boot recovery fails interrupted work closed

- Status: Accepted
- Date: 2026-05-15

## Context

Two of ember's active states are driven by in-memory work: `PLANNING` by a
background planning-loop task, `BUILDING` by the FIFO build queue and a child
process. A process restart kills both. The session row, however, persists — so
after a restart the database can hold a session in `PLANNING` or `BUILDING`
whose driver no longer exists.

`AWAITING_GATE` is different: the gate is just a persisted deadline, so it is
genuinely resumable and `GateService` re-arms it on boot (ADR 5). `PLANNING`
and `BUILDING` are not. A planning loop has no checkpoint — it cannot be
resumed mid-flight. A build leaves a half-populated git worktree that is not
idempotent to re-enter.

## Decision

On startup, `RecoveryService` marks every interrupted `PLANNING` and
`BUILDING` session `FAILED` — it never resumes them. It then removes the
orphaned build worktree (the branch and any commits the builder made survive —
only the working directory is deleted) and posts a notice to the thread. It
also applies a retention policy, removing worktrees of long-finished
`FAILED` / `ABORTED` sessions.

`RecoveryService` is registered as the first hosted service, and its
state-recovery pass runs synchronously in `StartAsync`, so the database is
consistent before `GateService` or `BuildQueue` start — a freshly-fired gate
can never be mistaken for a stale one.

## Consequences

- No attempt to resume non-idempotent work; no corrupt half-build is ever
  continued. The system fails closed.
- The operator re-runs `/plan` to retry — cheap, deterministic, and explicit.
- Committed builder work is not lost: it survives as the `ember/<slug>` branch
  even after its worktree directory is cleaned.
- `AWAITING_GATE` remains the one active state that survives a restart by
  design — the contrast with `PLANNING` / `BUILDING` is intentional and rests
  on whether the state is checkpointable.
