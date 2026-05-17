# 10. Rehearse the local planning loop on the NVIDIA card before the Arc hardware

- Status: Accepted
- Date: 2026-05-16

## Context

ADR 9 commits the all-local planning loop to vLLM on two Intel Arc Pro B70s.
That hardware was ordered 2026-05-15 and has not arrived; ADR 9 is itself only
Proposed — "on paper from research, not yet validated against the hardware."

Waiting for the Arc box to touch local inference at all bundles two unrelated
unknowns into one first-contact event:

- **ember-side** — how the planner and critic behave against small local
  models: JSON-mode parse reliability, prompt portability, loop dynamics,
  two-models-on-one-device co-residency.
- **Arc-side** — the LLM-Scaler XPU backend, WSL2 + Docker + GPU passthrough,
  and Arc quantization fragility.

A 4070 Ti (12 GB) is already on the host, vLLM has a mature CUDA backend, and
per ADR 3 the planner and critic reach any model through one OpenAI-compatible
`IChatClient` whose endpoint is chosen by config.

## Decision

Stand up the local planning loop now on the 4070 Ti, served by **vLLM on CUDA
under WSL2**. vLLM is the same server family as the Arc target (ADR 9), so this
rehearses the loop *and the server*, not just the loop.

- Two vLLM processes, one model each, co-resident on the single 12 GB card —
  planner on `:8000`, critic on `:8001` — each capped with
  `--gpu-memory-utilization`. The loop is strictly sequential: the planner and
  critic never run concurrently (`PlanningLoopRunner`), so co-residency is a
  VRAM question, not a compute one.
- An asymmetric, q4-class pairing sized to 12 GB: a ~7–8B planner and a ~3–4B
  critic. The critic does structured JSON review and tolerates being smaller.
  The goal is the wiring and the loop, not output quality.
- ember reaches both through an explicit `BaseUrl` in
  `appsettings.Development.json`. No `vllm` provider is added — vLLM *is* the
  OpenAI-compatible endpoint ADR 3 already targets. No code change.

This is the development configuration only. Base `appsettings.json` keeps the
hosted planner and critic.

## Consequences

- The Arc migration (ADR 9) collapses to swapping the CUDA vLLM image for the
  LLM-Scaler XPU image, adding Arc passthrough, and scaling up model size —
  `BaseUrl` and model ids only. Everything ember-side is validated first.
- "First contact" with the B70s narrows to the XPU backend and passthrough
  alone; the loop, JSON mode, and prompt portability are already proven.
- vLLM's quantization options (AWQ / GPTQ) are narrower than GGUF and its
  per-process overhead is real — two models on 12 GB is tight. Context length
  (`--max-model-len`) competes directly with weights for VRAM; size it for the
  largest `Revise` round — `MaxPlanRounds = 6` rounds of accumulating plan plus
  critique — not for the first round.
- The planner and critic system prompts were written for frontier models and
  will need tightening for ~3–8B models. That prompt tuning carries forward to
  the 27B-class Arc models unchanged.
- The critic's JSON-object mode maps to vLLM guided decoding; the parse-and-
  retry in `Critic.cs` remains the backstop. The retry rate is a quality
  signal — a critic that retries constantly is data for the Arc model-size
  decision.
- The builder is unaffected — it remains hosted Claude Code (ADR 4). "All-
  local" still means the planning loop, not the build.

## Update — 2026-05-17: first rehearsal runs on Ollama, not vLLM

The first local-loop rehearsal runs on **Ollama (CUDA)**, not vLLM. Ollama
loads models on demand and releases the GPU when idle, rather than pinning VRAM
the way two co-resident vLLM processes would — that made an immediate run
possible while the card was otherwise in use.

This still delivers ADR 10's *first* goal — exercising planner/critic behaviour
on small local models: JSON-mode reliability, prompt portability, loop
convergence. It does **not** deliver the second: the vLLM server itself is not
exercised, so the Arc migration does not get its server rehearsal here. That is
deferred — the Arc box's "first contact" reabsorbs the vLLM/XPU surface.

First-run pairing: planner `qwen3:8b`, critic `llama3.2` (3B). The critic is
deliberately *not* a Qwen3 model — Qwen3 emits `<think>` blocks by default,
which would sit ahead of the critic's JSON verdict; a plain instruct model
avoids that with no code change. Note for ADR 9: Ollama's silent CPU-fallback
problem is specific to Intel Arc XPU — on the NVIDIA card it is a normal CUDA
server, so this is not a deviation from ADR 9.
