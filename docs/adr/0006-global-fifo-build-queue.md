# 6. A global FIFO build queue as a safety throttle

- Status: Accepted
- Date: 2026-05-15

## Context

When a gate elapses, a session moves to `BUILDING` and a build must run. The
build is heavy: a full headless coding agent (Claude Code) plus whatever
build, test, and dependency-restore commands the plan calls for (`dotnet`,
`npm`, `nuget`). Multiple gates can elapse close together, so without a
throttle several builds could run at once.

Concurrent builds contend for host CPU and IO, push against model-provider
rate limits, and — if two ran against the same target repo — could race over
git state. ember is a single-operator personal tool; throughput is not a
goal, predictability is.

## Decision

All builds run through one global FIFO queue — exactly one build at a time,
across all repos. `BuildQueue` is a hosted service that drains an in-memory
channel; `GateService` enqueues a thread id when a gate fires. The queue also
owns the in-flight build's `CancellationTokenSource`, so `/abort` can kill a
running builder or drop a still-queued one. The worker re-reads the session
row on dequeue, so a session aborted while queued is simply skipped.

## Consequences

- Predictable host load and no contention between builders; a deliberate
  safety throttle, not a scaling decision.
- A queued build waits behind a long-running one. Acceptable for one operator.
- Per-repo concurrency — running builds for different repos in parallel — is a
  clean future option behind the same interface, and is deferred for v1.
- The queue is in-memory: a restart loses queued entries. This is covered by
  boot recovery (ADR 8) — a session stuck in `BUILDING` with no live build is
  marked `FAILED`, not silently lost.
