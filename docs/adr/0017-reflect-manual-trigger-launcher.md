# 17. Reflect is operator-triggered, not scheduled: a desktop launcher through the safety gates

Date: 2026-06-17

## Status

Accepted

Supersedes the unattended-schedule posture of [ADR 14](0014-reflect-dual-judge-recap.md) on this rig.

## Context

ADR 14 shipped Reflect as a nightly 03:00 auto-run. On a single-operator rig whose dual B70s are
*also* the machine the operator builds, tests, and streams on late into the night, an unattended
run at a fixed hour is wrong twice over:

- **Contention.** A recap that wakes at 03:00 while the operator is mid-build or streaming fights
  for VRAM and host RAM (the 32 GB ceiling, #99). It can stutter foreground work — a `--no-mmap`
  staging spike has stuttered audio and, historically, pushed this rig to non-POST.
- **Unreliability with a silent failure mode.** The judges run on `vllama` (:8090), a manually
  started process with no auto-start (Ollama's startup was disabled by the battlemage harness).
  If `vllama` is down at 03:00, the judge call throws and `ReflectExecutor` still writes a
  `Failed` row dated today — which *claims the day*. A scheduled run during any backend-down
  window is a silently lost recap with no same-day retry.

The operator is awake and present when recaps should run anyway, and the deliberate bring-up the
substrate already requires (`vllama up`, through its 22 GB host-RAM + b70tools verdict gate) is
exactly the safety check we want in front of a run.

## Decision

**Reflect is manual-only on this rig, driven by a desktop double-click.**

- **`ScheduleEnabled=false`** idles the nightly auto-run while keeping Reflect *enabled* —
  `/reflect` and the launcher trigger still work (`ReflectService.ScheduleDisabled`; a blank
  `RunAtLocalTime` means the same thing). The scheduler re-arms any time with `ScheduleEnabled=true`.
- **`scripts/Start-Reflect.ps1`** (Desktop shim `Reflect Now.cmd`) brings the pieces up *in order,
  through their gates*, then fires one run:
  1. `vllama serve` — the facade on :8090, started if absent.
  2. `vllama up --model` for both judges — this runs the **22 GB host-RAM + verdict gate**. If the
     rig is loaded, vllama *refuses*; the launcher reports and stops. No forced load, no non-POST
     risk — the substrate's own safety gate becomes the run's preflight.
  3. Ensure the bot is up and Discord-connected (polls the trigger's `/ready`).
  4. `POST /reflect` on the loopback trigger — **synchronous**: it returns when the judges finish.
  5. `vllama kill-all` to free the judges' VRAM (skip with `-KeepWarm`); the facade stays up.
- **`ReflectTriggerService`** is a loopback-only (`127.0.0.1:LocalTriggerPort`) endpoint on the
  long-running bot that starts the *same* run as `/reflect`. Raw `TcpListener`, so a non-elevated
  process binds without an HTTP.sys urlacl. `GET /ready` (enabled + Discord connected) lets the
  launcher wait out a freshly-started bot; `POST /reflect` runs the recap **synchronously** and
  returns the summary when done, so the launcher frees the GPUs only after the judges finish (409
  if a run is already in progress — then it leaves the GPUs alone).

## Consequences

- A recap never competes for the GPUs by surprise — the operator picks the moment the rig is free.
  The "Failed claims the day, no retry" trap is sidestepped: you only trigger when the backend is up.
- The launcher cannot bulldoze the rig: it calls vllama's documented CLI and inherits its refusal
  under host-RAM pressure rather than bypassing it.
- **Symmetric teardown.** The run hands the GPUs back: when the judges finish, the launcher runs
  `vllama kill-all` to release VRAM (the facade, holding none, stays up to speed the next run);
  `-KeepWarm` skips it. A recap leaves the rig as it found it — free for the next build or stream.
- The nightly cadence is gone. The seven-night label gate (ADR 14) advances per *triggered* run, not
  per calendar night — acceptable when one operator runs it deliberately.
- `/reflect` in Discord remains an identical manual path; nothing about the recap pipeline, grounding,
  or label gate changes — only what starts a run.
- The loopback trigger is a new (tiny) network surface, bound to 127.0.0.1 and gated on `Enabled` +
  a live Discord link; `LocalTriggerPort=0` (the default) turns it off entirely.
