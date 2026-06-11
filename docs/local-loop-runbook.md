# Local planning-loop runbook

Running the planner and critic on local models instead of hosted Claude / GPT.
Three paths, in status order — **llama.cpp Vulkan** (the validated dual-Arc
Windows target, [ADR 12](adr/0012-windows-phase-llamacpp-vulkan.md)),
**Ollama + swap proxy** (the 4070 Ti rehearsal path, ADR 10 — still what the
committed Development config points at), and **vLLM** (deferred to the Linux
migration, ADR 9 / ADR 12). The builder always stays hosted Claude Code.

## llama.cpp Vulkan — the dual-Arc Windows path

The validated Windows-phase stack
([ADR 12](adr/0012-windows-phase-llamacpp-vulkan.md); battlemage ADR-021):
**llama.cpp `llama-server`, release b9305 (Windows Vulkan x64 prebuilt),
native Windows** — no WSL2, no container. One server per card, so the planner
and critic each own a whole B70 and there is no residency contention for a
swap proxy to mediate:

- planner — `llama-server --device Vulkan0 --host 127.0.0.1 --port 8080 …`
- critic — `llama-server --device Vulkan1 --host 127.0.0.1 --port 8081 …`

Point `appsettings.Development.json` at `http://127.0.0.1:8080/v1` and
`:8081/v1` with the matching model ids.

Mind the load-bearing flags from battlemage ADR-021: on the 32 GB host,
`--no-mmap` + direct-io keep a large GGUF load from saturating system RAM, and
`GGML_VK_DISABLE_COOPMAT=1` defends against an Arc driver TDR. The layer-split
variant (one 70B across both cards) additionally needs `-fit off` on
`llama-server` — the default auto-fitter silently spills to shared GPU memory.
The full verified recipe lives in
`D:\World of Warcraft\Tempo\docs\adr\adr-021-dual-b70-inference-path.md`.

Status: the serving stack is validated (battlemage, 2026-05-24 → 2026-06-05),
but the loop itself has run on the dual-Arc box only against two Ollama
servers so far — the run that hit the 32 GB system-RAM ceiling and motivates
`llama-server` here (`RnD/07-arc-dual-card.md`). The first loop run against
this pair is ADR 12's acceptance check.

## Ollama + swap proxy — the 4070 Ti rehearsal path

The loop runs on local Ollama models reached through the **swap proxy**
(`tools/OllamaSwapProxy`). The proxy keeps exactly one model resident at a time,
so an 8B planner and a 20B critic can share a 12 GB card without contending —
and it logs every load's GPU/CPU split. See `tools/OllamaSwapProxy/README.md`.

### One-time setup

- Ollama installed and running (`ollama --version`).
- Pull the planner and critic models, e.g. `ollama pull qwen3:8b` and
  `ollama pull gpt-oss:20b`.
- `appsettings.Development.json` already points the planner and critic at the
  proxy (`http://localhost:11500/v1`); the model ids there must match what you
  pulled.

### Each run

1. **Proxy** — `dotnet run --project tools/OllamaSwapProxy` (leave it running).
2. **Loop** — either:
   - console: `dotnet run --project src/Ember -- plan "<brief>" <repo>`
   - full bot: `dotnet run --project src/Ember`, then drive `/plan` in Discord.
3. **Inspect** — `tools/OllamaSwapProxy/swaps.jsonl` records, per call: model,
   load time, Ollama and `nvidia-smi` VRAM, generate time, status, token counts.

### If something is off

- **A model loads partly on CPU (`gpu_pct` < 100)** — the card is contended.
  `swaps.jsonl`'s `smi_free_mib` shows how little VRAM was free; close other GPU
  work for a clean run, or accept the slower one.
- **A call fails several minutes in** — raise `Models:*:RequestTimeoutSeconds`
  (the per-call HTTP timeout; it also has to cover the proxy's swap-and-load).
- **The plan looks truncated late in the loop** — Ollama's context window.
  Raise it (`OLLAMA_CONTEXT_LENGTH`, or a `num_ctx` Modelfile).
- **The gate auto-proceeds while you sleep** — expected. The soft gate
  (`Ember:GateCountdownSeconds`) proceeds with no human veto when unattended.

## vLLM — deferred to the Linux migration

ADR 9 committed the production local loop to vLLM on the two Arc Pro B70s,
assuming WSL2 would carry the Linux-only vLLM-XPU stack on Windows. That
bridge is blocked — two upstream kernel bugs in WSL2's paravirtualization
shim ([ADR 12](adr/0012-windows-phase-llamacpp-vulkan.md)) — so vLLM waits
for the native-Linux migration; this section becomes current again there.
The original procedure is kept below as written: ADR 10 kept the option of
rehearsing the *vLLM server itself* on the host's 4070 Ti under WSL2, and
`scripts/serve-local.sh` brings up that pair.

### One-time setup

- WSL2 + Ubuntu, with the NVIDIA driver on Windows and CUDA visible inside WSL2
  — confirm with `nvidia-smi` in the WSL2 shell.
- `pip install vllm` in the WSL2 environment.
- Pull two AWQ models and set `PLANNER_MODEL` / `CRITIC_MODEL` at the top of
  `scripts/serve-local.sh`.

### Each run

1. **WSL2** — `bash /mnt/d/work/ember/scripts/serve-local.sh` (blocks until both
   vLLM servers report ready).
2. **Windows** — point `appsettings.Development.json` at the vLLM ports
   (`:8000` / `:8001`) and `dotnet run --project src/Ember`.
3. **Done** — `bash /mnt/d/work/ember/scripts/serve-local.sh stop`.

### If something is off

- **A server won't start** — read `~/.ember-vllm/planner.log` / `critic.log`.
  An out-of-memory error means the budget is too tight: lower
  `--gpu-memory-utilization` or `--max-model-len` for that server.
- **The plan looks truncated late in the loop** — raise `--max-model-len`; it
  competes with model weights for VRAM.
