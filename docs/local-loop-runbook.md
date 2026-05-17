# Local planning-loop runbook

Running the planner and critic on local models instead of hosted Claude / GPT.
There are two paths — **Ollama** (the current rehearsal, ADR 10) and **vLLM**
(the Intel Arc target, ADR 9). The builder always stays hosted Claude Code.

## Ollama — the current path

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

## vLLM — the Intel Arc target path

ADR 9 commits the production local loop to vLLM on two Intel Arc Pro B70s; ADR
10 keeps the option of rehearsing the *vLLM server itself* on the host's 4070 Ti
under WSL2. `scripts/serve-local.sh` brings up that pair.

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
