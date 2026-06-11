# 12. Windows-phase local inference uses llama.cpp Vulkan on native Windows; vLLM defers to the Linux migration

- Status: Accepted
- Date: 2026-06-11
- Supersedes: [ADR 9](0009-local-inference-on-intel-arc.md) for the Windows phase

## Context

ADR 9 (Proposed, 2026-05-16) chose vLLM (Intel LLM-Scaler) as the serving
stack for the two Arc Pro B70s, running under WSL2 because vLLM-XPU is
Linux-only. It closed with an explicit caveat: "on paper from research, not
yet validated against the hardware. Revisit on first contact with the B70s."

First contact happened — but not through ember. The dual-B70 bring-up ran
2026-05-21 → 2026-06-05 through the `D:\work\battlemage` workspace (a
filesystem-only research notebook, not a git repo), and reality diverged from
ADR 9 on its central assumption:

- **The WSL2 bridge is dead on this host.** Two distinct upstream kernel bugs
  were characterized to source line — a `dxgkrnl` mutex deadlock at
  `dxgglobal_acquire_process_adapter_lock` during dual-adapter open, and a
  NEO driver abort at `wddm_memory_manager.cpp:914`. Both live in WSL2's
  paravirtualization shim: upstream-filable, not user-fixable. Any
  Linux-only stack reached through WSL2 — vLLM-XPU included — is blocked.
- **Native Windows Vulkan works.** llama.cpp release **b9305** (Windows
  Vulkan x64 prebuilt) drives both cards natively. Validated operating
  points:
  - **Two-process pipeline** — one `llama-server` per card (`--device
    Vulkan0` / `Vulkan1`, ports `8080` / `8081`), independent workloads at
    full per-card throughput.
  - **Layer-split single model** — Llama-3.3-70B-Instruct Q4_K_M across both
    cards at **11.28 t/s**, within 2% of the published Linux SYCL baseline.
  - **Gaming-coexistent inference** (2026-06-05) — a large-context model
    serving from card 1 while a game runs on card 0, no measurable impact.
- **The planner/critic loop ran on the box** (2026-06-05) against two local
  Ollama servers — and hit the **32 GB system-RAM ceiling**: Ollama's
  resident host-RAM overhead is materially higher than `llama-server` for
  the same models. `llama-server` reclaims enough headroom to run the loop
  under 32 GB today; Workstation issue #99 (32 → 64 GB) is the real unblock.

The decision record for the stack is **battlemage ADR-021** (ratified
2026-05-24), promoted to
`D:\World of Warcraft\Tempo\docs\adr\adr-021-dual-b70-inference-path.md`;
the durable summary is `D:\work\gad\pm\repos\battlemage.md`. Because
battlemage is not git-tracked, those absolute paths are the citation.

## Decision

For the **Windows phase**, ember's local planner and critic are served by
**llama.cpp `llama-server` (release b9305, Vulkan, native Windows)** — one
endpoint per card, the planner and critic each owning a whole B70
(`--device Vulkan0` → `:8080`, `--device Vulkan1` → `:8081`). The server is
OpenAI-compatible, so per ADR 3 this is a `BaseUrl` + model-id config
change: no code change, and **no swap proxy** — with one model resident per
card there is no contention to mediate.

**vLLM is deferred, not dead.** ADR 9's reasoning — Intel's actively
optimized B70 path is vLLM/LLM-Scaler, with multi-GPU tensor parallelism —
still holds *on Linux*. What fell was the WSL2 bridge ADR 9 assumed would
carry it on Windows. vLLM is re-queued for the native-Linux migration that
follows the daily-driver rebuild; revisit this decision there.

Operational notes carried over from battlemage ADR-021: on the 32 GB host,
`--no-mmap` + direct-io keep a large GGUF load from saturating system RAM,
and `GGML_VK_DISABLE_COOPMAT=1` defends against an Arc driver TDR. The
layer-split variant additionally needs `-fit off` on `llama-server` — the
default auto-fitter silently spills to shared GPU memory. The full verified
recipe and flag rationale live in ADR-021; ember does not duplicate them.

## Consequences

- **ember is unchanged — again.** This is the third consecutive serving
  stack (Ollama + swap proxy → dual Ollama → `llama-server`) reached through
  ADR 3's adapter with configuration only. The "which local server" question
  has still never touched the code.
- **The swap proxy retires from the Arc path.** `tools/OllamaSwapProxy`
  solved single-card residency contention on 12 GB; with a card per model
  there is nothing to mediate. It remains valid for the 4070 Ti rehearsal
  procedure and as a record.
- **Host RAM, not VRAM, is now the binding constraint.** 64 GB of VRAM
  retired the model-size problem, and the 2026-06-05 run showed the 32 GB of
  system RAM is what binds the loop. `llama-server`'s leaner residency is
  the mitigation that keeps the loop runnable today; Workstation #99 is the
  fix.
- **The acceptance check is still open.** The loop has run on the Arc box
  only against Ollama endpoints (RnD/07). The first end-to-end loop run
  against the `llama-server` pair validates this ADR's wiring claim and
  should be logged as the next RnD entry.
- **The Windows phase has no Linux/WSL2/container dependency** — ADR 9's
  "real setup cost" consequence is cancelled rather than paid. That cost
  returns, deliberately, at the Linux migration.
- The builder stays headless hosted Claude Code (ADR 4); "all-local" still
  means the planning loop, not the build.
- **The bring-up evidence lives outside this repo.** battlemage is
  filesystem-only and gad pm holds the narrative; ember's records (this ADR,
  RnD/07) deliberately cite rather than duplicate them.

## Related

- battlemage ADR-021 —
  `D:\World of Warcraft\Tempo\docs\adr\adr-021-dual-b70-inference-path.md` —
  the stack decision, verified configs, load-bearing flags, and the cost
  accounting.
- `D:\work\gad\pm\repos\battlemage.md` — durable workspace summary.
- `D:\work\gad\pm\build-log.md` — entry "2026-06-05 · the dual-Arc bring-up
  fortnight".
- [RnD/07-arc-dual-card.md](../../RnD/07-arc-dual-card.md) — the backfilled
  pointer entry for the 2026-06-05 run.
- [docs/local-loop-arc-bringup.md](../local-loop-arc-bringup.md) — the
  pre-arrival bring-up plan, preserved as a record and marked overtaken by
  events.
- ADR 9 — superseded for the Windows phase; its vLLM reasoning resumes at
  the Linux migration.
- ADR 3 — the OpenAI-compatible `IChatClient` posture that makes all of this
  a config change.
