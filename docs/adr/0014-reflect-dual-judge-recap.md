# 14. Reflect: a scheduled dual-judge recap on the local cards

Date: 2026-06-11

## Status

Accepted

## Context

R2 of the constellation-awareness plan (`D:\work\gad\pm\constellation-awareness-plan.md`):
a nightly, locally-inferenced recap of the constellation's committed work, written by two
independent models whose disagreements are the interesting output. The operator's reaction
to each recap is ground-truth labelling that a later adaptation loop (R4) trains against.

ember already owns every piece of plumbing this needs — the always-on host, Discord posting,
the OpenAI-compatible `IChatClient` seam (ADR 3), SQLite, OTel — and the serving substrate
is proven: vllama's facade exposes `vllama-planner` (Qwen3-30B-A3B, dual-split) and
`vllama-critic` (Qwen2.5-14B) on `127.0.0.1:8090` (battlemage ADR-021 stack). One hard
operational constraint from that workspace: **inference servers are never launched
unattended** — vllama's RAM-preflight gates exist because an OOM cascade once took the
machine to non-POST.

## Decision

A `Reflect` subsystem inside ember, **disabled by default** (`Ember:Reflect:Enabled`):

- **Evidence is git-first, graph-enriched.** Per allowlisted repo: `git diff --name-only
  <last-sha>..HEAD` and the commit log are the authority on what changed (the per-repo
  baseline sha lives in a new `repo_reflect_state` table); the knowledge graph adds the
  symbols behind the changed files. `detect_changes` was measured during R0 to cover
  *uncommitted* work only, so committed deltas come from git — which also makes the recap's
  baseline explicit and replayable instead of dependent on watcher timing.
- **Two judges, never seeing each other.** Keyed `IChatClient`s `reflectA`/`reflectB`
  default to the vllama facade aliases — different models on different cards. A structured
  comparison pass (Critic-style JSON mode with one retry, ADR 5's parse posture) extracts
  agreements and divergences; divergence is signal, not error. One judge failing degrades to
  a single-perspective recap; both failing fails the run.
- **One thread per day, reactions as labels.** The recap posts to a dedicated channel as a
  `reflect: yyyy-MM-dd` thread; ✅/✏️/❌ on the label-request message persist to the
  `recaps` table via the existing reaction handler. That table — recaps, models, divergences,
  labels — is the R4 evaluator corpus.
- **Failed runs do not advance baselines.** A broken night re-reports the same delta rather
  than losing it. A run of any status claims the calendar day (no retry storms against a
  down endpoint); `/reflect` is the manual override, and a missed night self-heals on the
  next boot past the scheduled time.
- **The pipeline never starts a model server.** Endpoints down → the run fails soft and
  says so. Launching models stays a deliberate operator action through vllama's gates.
- **Console-first validation.** `dotnet run -- reflect --dry-run [--since-hours N]` runs
  the real pipeline read-only (git-resolved baseline, no Discord, no persistence) — the
  same posture as ADR 10's rehearse-before-hardware.

## Consequences

- ember grows a second autonomous behaviour beyond `/plan`. The blast radius is bounded:
  off by default, owner-locked command, one Discord thread per day, no git writes, no
  builder involvement.
- Recap quality at the current model tier is unproven — that is exactly what the
  seven-night R2 gate and the label corpus measure. The judge aliases are config; battlemage
  Q2's outcome upgrades them without code.
- The `recaps` table starts accumulating the dataset everything in R3/R4 (graph write-back,
  harness adaptation) feeds on.
- Evidence quality has known v1 roughness: symbol enrichment matches on file stems and can
  pull in same-named noise; markdown files appear as Module symbols. Judges see slightly
  noisy-but-grounded evidence; tighten later if labels show it matters.
