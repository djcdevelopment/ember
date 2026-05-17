# Local planning-loop runbook

Running the planner and critic on local models instead of hosted Claude / GPT
(ADR 9, ADR 10). This is the whole flow, top to bottom — start here.

## One-time setup

- WSL2 + Ubuntu, with the NVIDIA driver on Windows and CUDA visible inside
  WSL2 — confirm with `nvidia-smi` *in the WSL2 shell*.
- `pip install vllm` in the WSL2 environment.
- Pull two AWQ models (a ~7–8B planner, a ~3–4B critic) and set `PLANNER_MODEL`
  / `CRITIC_MODEL` at the top of `scripts/serve-local.sh`.

That is the only place model ids are configured. ember talks to fixed aliases
(`Qwen3.6-8B-AWQ`, `Qwen3.6-4B-AWQ`) the script serves under — so swapping the
actual weights never touches `appsettings.Development.json`.

## Each run

1. **WSL2** — `bash /mnt/d/work/ember/scripts/serve-local.sh`
   Brings up both vLLM servers; blocks until each reports ready.
2. **Windows** — `dotnet run --project src/Ember`
   The `Ember` launch profile sets `DOTNET_ENVIRONMENT=Development`, so this
   picks up the local vLLM config automatically.
3. **Discord** — drive `/plan` as normal.
4. **Done** — `bash /mnt/d/work/ember/scripts/serve-local.sh stop`

## If something is off

- **A server won't start** — read `~/.ember-vllm/planner.log` /
  `critic.log`. An out-of-memory error there means the budget is too tight:
  lower `--gpu-memory-utilization` or `--max-model-len` for that server in
  `serve-local.sh`. vLLM fails loudly at startup — it never silently falls
  back to CPU.
- **A turn fails ~5 min in** — `Models:*:RequestTimeoutSeconds` (default 300)
  is shorter than a cold model-load plus generation. Raise it.
- **The plan looks truncated late in the loop** — raise `--max-model-len`;
  it competes with model weights for VRAM, so give back some
  `--gpu-memory-utilization` headroom.
- **The gate auto-proceeds while you sleep** — expected. The soft gate
  (`Ember:GateCountdownSeconds`, default 300) counts down and proceeds with no
  human veto when unattended (ADR 5). This is by design for an overnight run.
