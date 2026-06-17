# EXP-0002 — graph-first evidence: does the glance surface in-flight work the commit-led read missed?

Date: 2026-06-17 · Status: complete (evidence arm); judge arm deferred · Decision: ADR-0018

## Question

Reflect's first real run (night of 2026-06-17) **lost a head-to-head with a hand survey**. It
recapped only `["b70tools","ember"]` — the two repos with fresh *commits* — and surfaced ~1
fact (b70tools' LICENSE). It missed:

- ember's own **8-commit "Reflect-goes-live" night** (its headline), and
- **all** uncommitted WIP: leopard's planner (20 files), Tempo (12), raidui (27).

Root cause: evidence was **commit-led** (git delta since the last recap). In-flight work that
hasn't been committed is structurally invisible to a commit delta. The constellation glance
(`pm/scripts/constellation-glance.py`, shipped the same day) reads the *working tree* — the
signal commit-age lies about.

**Q: If Reflect assembles evidence glance-first — uncommitted WIP + branch/unpushed + lifecycle
+ drift as the primary read, then the commit delta + code-graph symbols for detail — does it
surface the in-flight work the commit-led read missed, without blowing the judge's context
budget?**

## Design (before/after on the same night's data)

The independent variable is the **evidence recipe**; the working tree is held at the 2026-06-17
state (the frozen glance JSON in `inputs/` pins what the tree looked like).

- **Before (arm A) — commit-led.** Reflect ≤ this change: a repo appears only if it has a
  committed delta since baseline. (Reconstructed from the production run's transcript — recapped
  2 repos.) The behaviour is pinned by the *old* `EvidenceAssemblerTests` ("N repo(s) with
  changes", quiet-unless-committed).
- **After (arm B) — glance-first.** This change: the glance is the primary read; a repo is
  *in flight* when it has WIP **or** unpushed commits **or** a committed delta. WIP file paths
  are read locally (so they are citable); lifecycle + drift come from the glance.

Captured read-only with `dotnet run -- reflect --dry-run --since-hours 24` (no judges, no DB, no
Discord). Full dual-judge re-run is **deferred** — it loads the 30B+14B onto the cards and the
operating constraint is free-VRAM / manual-trigger; the evidence arm is the deterministic part
of the acceptance and needs no GPU.

## What is held constant

Same night (2026-06-17), same repo allowlist, same 24h baseline window, same per-repo / total
char caps (40 files, 3 000/repo, 16 000 total). Only the evidence recipe changes.

## Reproduce

```
# frozen working-tree read (the primary input)
python D:\work\gad\pm\scripts\constellation-glance.py --json   # → inputs/glance-2026-06-17.json

# the after bundle (this change)
cd D:\work\ember
dotnet run --project src/Ember -- reflect --dry-run --since-hours 24   # → results/evidence-after-glance-first.txt
```

`inputs/glance-2026-06-17.json` is the frozen glance read; `results/evidence-after-glance-first.txt`
is the assembled bundle. The before arm is described from the production transcript (the
commit-led recap of 2 repos) — see `verdict.md`.
