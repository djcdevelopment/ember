# EXP-0001 — XML vs JSON for the reflect comparer + recap grounding

Date: 2026-06-17 · Status: complete · Decision: ADR-0016

## Question

Two of Derek's hypotheses, after the first live recap (night zero) showed the comparer
over-fired on emphasis and the 30B hallucinated a cross-repo dependency:

1. Does **XML** output beat **JSON** for the divergence comparer's structured discrimination?
2. Does an **XML evidence-cite scaffold** stop the recap author's cross-repo hallucination —
   and do larger models benefit more from the structure?

## Design (factorial — isolate wording from format)

Run on the live vllama facade (planner = Qwen3-30B-A3B, critic = Qwen2.5-14B), K=3.

**Test A — comparer** (on frozen Recap A + Recap B, so only the named variable moves):
- `A1` json + original prompt — the baseline (= ember before this).
- `A2` json + improved prompt (contradiction-not-tone, +kind) — isolates **wording**.
- `A3` xml + improved prompt — isolates **format** on top of A2.
A1→A2 measures wording; A2→A3 measures format. Comparer temp 0 (deterministic).

**Test B — recap grounding** (on the 30B, temp 0.6):
- `B1` plain markdown — the baseline.
- `B2` XML where every claim must carry a `<from>` citing evidence. Auto-metric: fraction of
  `<from>` citations that appear verbatim in the evidence (fabricated-citation detector).

## What is held constant

Same frozen evidence window (8,844 chars), same two frozen recaps for all Test-A arms, same
models, same K. Only the named variable (wording, format, or scaffold) changes per arm.

## Reproduce

`run_ab.py` (stdlib only) against the facade at `127.0.0.1:8090`. `inputs/` holds the captured
evidence window; `results/` holds every arm's raw output + `summary.json`. Smoke with
`ABTEST_SMOKE=1`. Generation arms are non-deterministic (temp 0.6); comparer arms are temp-0
stable. Directional (K=3), not publication-grade — "generating decisions, not papers."
