# 9. Local inference on the Intel Arc hardware uses vLLM, not Ollama

- Status: Superseded for the Windows phase by
  [ADR 12](0012-windows-phase-llamacpp-vulkan.md) (2026-06-11; was Proposed)
- Date: 2026-05-16

## Context

The all-local goal is to run the planner and critic on a local model instead
of hosted Claude / GPT. ADR 3 already makes the *model* a config change — the
planner and critic reach models through one OpenAI-compatible `IChatClient`.
What was open is the local *serving* stack.

Two Intel Arc Pro B70 GPUs (32 GB GDDR6 each, 64 GB total, 608 GB/s) were
ordered 2026-05-15. Intel Arc changes the calculus from the NVIDIA assumption:

- The standard Ollama binary has no Intel Arc acceleration — it silently runs
  on CPU regardless of what a GPU monitor shows.
- IPEX-LLM, the historical Ollama-on-Arc path, was reported archived in early
  2026.
- Intel's active, optimized path for the B70 is **vLLM** via the LLM-Scaler
  project, with official B70 support and multi-GPU tensor parallelism.

## Decision

When the hardware arrives, serve the local planner and critic from **vLLM
(Intel LLM-Scaler)**, not Ollama. vLLM exposes an OpenAI-compatible `/v1`
endpoint, so ember reaches it through the same adapter (ADR 3) — a `BaseUrl`
config change, no code change. vLLM-XPU is Linux-only, so on the Windows host
it runs under WSL2 + Ubuntu + Docker with Arc GPU passthrough. Use dense models
(~27B class, e.g. Qwen3.6-27B); the Arc stack's MoE support is immature.

The builder stays headless Claude Code on a hosted Anthropic model — it is not
localizable (ADR 4). "All-local" therefore means the planning loop, not the
build.

## Consequences

- ember is unchanged: the inference server is swappable behind the
  OpenAI-compatible `IChatClient`; "which local server" never reaches the code.
- The local stack is Linux / WSL2 / container-shaped — a real setup cost, and a
  dependency the Windows host did not previously have.
- 64 GB of VRAM retires the model-size and model-swap constraints entirely; the
  planner and critic can each be a large dense model, co-resident or one per
  card.
- Status is **Proposed**, not Accepted: the decision is on paper from research,
  not yet validated against the hardware. Revisit on first contact with the
  B70s — quantization fragility and tooling rough edges on the Arc stack are
  expected.

## Update — 2026-06-11: superseded on first contact (Windows phase)

The "revisit on first contact" caveat resolved against this decision. The
dual-B70 bring-up (2026-05-21 → 2026-06-05, run through the
`D:\work\battlemage` workspace rather than ember) found the WSL2 bridge this
ADR leaned on is blocked by two upstream kernel bugs in WSL2's
paravirtualization shim, while llama.cpp Vulkan on native Windows works —
validated to a 70B-class layer-split across both cards.
[ADR 12](0012-windows-phase-llamacpp-vulkan.md) records the Windows-phase
decision: `llama-server` per card, vLLM deferred. The reasoning above —
Intel's optimized path is vLLM/LLM-Scaler — was not refuted; it applies on
native Linux and resumes at the Linux migration.
