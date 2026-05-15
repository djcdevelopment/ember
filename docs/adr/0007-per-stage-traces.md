# 7. Per-stage traces, not one long-lived session span

- Status: Accepted
- Date: 2026-05-15

## Context

PLAN.md's observability section sketches "one end-to-end trace per `/plan`"
with an `ember.session` root span covering the whole lifecycle, child spans
nested beneath it.

That lifecycle is not span-shaped. It includes a soft-gate countdown (~5
minutes of deliberate idle waiting) and a build that runs for minutes, and it
can cross a process restart. An OpenTelemetry `Activity` is an in-process,
in-memory object: it cannot stay open across a restart, and holding one open
through minutes of idle waiting is an anti-pattern that distorts every
duration and breaks if the host bounces.

## Decision

Emit a separate, short-lived trace per pipeline stage rather than one span for
the session:

- `command /<name>` — the slash command
- `plan.session` — the planning loop, with a `plan.round` child per round
- `gate.fire` — the gate elapsing
- `build.run` — the build, with a `pr.open` child for the PR handoff

Stages correlate by tag (`ember.repo`, `ember.thread_id`), not by a shared
parent span. Each trace begins and ends inside a single in-process operation.

## Consequences

- Every trace is short-lived and restart-safe; durations are honest.
- A session's telemetry is fragmented into ~4 trace trees rather than one.
  Correlation is by tag, which is weaker than parent/child linkage.
- If tighter correlation is wanted later, the clean path is to persist the W3C
  trace context on the session row and join stages with span **links** — this
  keeps each span short-lived while still relating them. Recorded as a future
  option, not built for v1.
- The implementation deliberately diverges from PLAN.md here; the divergence is
  recorded so the plan and the code do not silently disagree.
