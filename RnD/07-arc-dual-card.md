# Arc dual-card — first planner/critic run on the B70s

**Run date 2026-06-05 · logged 2026-06-11.** This entry is a backfill — the
run was never logged here at the time, and the evidence lived only in gad pm.
It is a pointer entry, not a full run log; the narration lives at the paths
below.

A numbering note: the bring-up plan
([docs/local-loop-arc-bringup.md](../docs/local-loop-arc-bringup.md)) reserved
`07-arc-single-card.md` for an ember single-card smoke that never happened —
the single-card validation ran through battlemage's own harnesses instead of
ember, so the dual-card entry takes 07.

## What ran

Both Arc Pro B70s installed; the serving stack validated 2026-05-24 in the
`D:\work\battlemage` workspace as **native Windows Vulkan via llama.cpp
release b9305** — two-process pipeline, one endpoint per card (`--device
Vulkan0`/`Vulkan1`, ports `8080`/`8081`), Llama-3.3-70B Q4_K_M layer-split at
11.28 t/s (battlemage ADR-021).

On 2026-06-05, off-game, the **planner/critic loop ran locally against two
separate Ollama servers** on the dual-B70 box — the first planner/critic loop
on the Arc hardware. The same day produced the other durable operating point:
**gaming-coexistent inference** — a large-context model serving from card 1
(`--device Vulkan1`, its own port) while a game ran on card 0, with no
measurable gaming impact.

## Finding — the 32 GB system-RAM ceiling

The loop worked, but Ollama's resident **system-RAM** overhead — materially
higher than `llama-server`/llama.cpp for the same models — put the 32 GB DDR4
host right at its ceiling. Two takeaways:

1. **Workstation Step 4 (issue #99, 32 → 64 GB) is the binding constraint** —
   empirically confirmed, not a nice-to-have.
2. **`llama-server` is the lean mitigation**: it reclaims enough headroom to
   run the loop under 32 GB today, and it is the Windows-phase serving
   decision anyway ([ADR 12](../docs/adr/0012-windows-phase-llamacpp-vulkan.md)).

VRAM — the constraint the entire 4070 Ti rehearsal
([06-ollama-rehearsal-runs.md](06-ollama-rehearsal-runs.md)) was shaped
around — is retired at 64 GB; host RAM is what binds now.

## Where the full narration lives

- `D:\work\gad\pm\build-log.md` — entry "2026-06-05 · the dual-Arc bring-up
  fortnight" (the run, the finding, and the honest product-flat accounting).
- `D:\work\gad\pm\repos\battlemage.md` — durable summary of the workspace,
  hardware, stack, and Q1–Q4 gate state.
- `D:\World of Warcraft\Tempo\docs\adr\adr-021-dual-b70-inference-path.md` —
  battlemage ADR-021: the serving-stack decision, verified configs, and the
  load-bearing flags.
- [ADR 12](../docs/adr/0012-windows-phase-llamacpp-vulkan.md) — what this
  means for ember.

## Next

Re-run the loop against the `llama-server` pair — one endpoint per card,
`:8080`/`:8081` — and log it as the next entry here. That run is ADR 12's
acceptance check: it validates the config-only wiring claim and measures the
loop with the RAM ceiling mitigated.
