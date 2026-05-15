# 5. Critic-driven loop termination and a resumable soft gate

- Status: Accepted
- Date: 2026-05-15

## Context

The planning loop needs a stop condition, and there must be a human
checkpoint before any code is built. A symmetric loop — two models both
"planning" — tends toward bland consensus. A fixed round count is arbitrary.
A hard approval gate is safe but not hands-off; no gate at all is unsafe.

## Decision

**Asymmetric roles.** Claude authors the plan; GPT is a red-team critic that
returns a structured verdict — issues tagged blocking, major, or minor.

**Critic-driven termination.** The loop runs until the critic raises no
blocking or major issue (`Approved`), with two backstops: a round cap
(`RoundCap`) and a no-progress check (`Stalled`). A `gate_reason` records
which path ended the loop.

**Soft gate.** The converged plan is frozen as a snapshot, a countdown opens,
and it proceeds unless the operator vetoes it (a stop reaction or `/abort`).
The gate deadline is persisted; a gate that elapsed while ember was down is
re-armed on boot, never auto-fired.

## Consequences

- Termination adapts to the brief instead of obeying a fixed count.
- The operator keeps a real veto without having to babysit the loop.
- "Pause before damage" survives a process restart.
- A converged plan can cost several model round-trips.
- The interface must distinguish `Approved` from `RoundCap` / `Stalled` so a
  forced-forward plan is never mistaken for an endorsed one.
