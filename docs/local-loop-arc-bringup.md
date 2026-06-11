# Arc Pro B70 bring-up — Windows phase

> **⚠ Overtaken by events — preserved as a record (2026-06-11).**
> This is the pre-arrival plan, written before the cards landed. The bring-up
> itself ran through the `D:\work\battlemage` workspace (filesystem-only, not
> git), and the validated Windows-phase stack is **native Windows Vulkan via
> llama.cpp release b9305** — not the IPEX-LLM / Ollama-XPU path prescribed
> below. Decision records: battlemage ADR-021 (ratified 2026-05-24, promoted
> to `D:\World of Warcraft\Tempo\docs\adr\adr-021-dual-b70-inference-path.md`)
> and ember [ADR 12](adr/0012-windows-phase-llamacpp-vulkan.md). WSL2 SYCL was
> deprecated outright — two upstream kernel bugs, characterized to source
> line, live in WSL2's paravirtualization shim.
>
> The `RnD/07-arc-single-card.md` ember run planned below never happened; the
> first logged Arc loop run is [RnD/07-arc-dual-card.md](../RnD/07-arc-dual-card.md)
> (2026-06-05, dual-endpoint, found the 32 GB system-RAM ceiling). What this
> plan did get right: the phased one-card-at-a-time install, the gaming check
> (validated 2026-06-05 — inference on card 1 coexists with a game on card 0),
> ReBAR, and "the builder always stays hosted Claude Code." Treat everything
> stack-specific below as historical. Current procedure:
> [local-loop-runbook.md](local-loop-runbook.md).

Staged bring-up checklist for the 2× Intel Arc Pro B70 32GB (64 GB total VRAM)
that replaces the 4070 Ti as ember's local-loop engine. Cards expected
**2026-05-21** (UPS `1ZE569864210988053`). This is the **Windows phase**;
native-Linux migration follows the daily-driver rebuild and is out of scope here.

The plan is one card at a time, with a gaming check between cards, so a bad
revision-1 doesn't leave the desk dark.

- **Phase 1** — pull 4070 Ti, install first B70, prove the planner↔critic loop
  on one card.
- **Gaming check** — confirm daily-driver titles are tolerable on the Arc Pro
  driver before committing.
- **Phase 2** — install the second B70, run the first 64 GB rehearsal that
  night, capture the data that decides Approach A vs B (see ADR 9 / 10 and
  `project_ember` memory).

The builder always stays hosted Claude Code. Only the planner and critic move.

## Pre-arrival prep (do tonight)

Don't touch hardware until these are all green.

- [ ] **Intel Arc Pro driver bundle** downloaded — the **Pro/workstation
      driver**, not the consumer Arc gaming driver. They are separate
      installers; start with Pro for IPEX-LLM stability.
- [ ] **oneAPI Base Toolkit runtime** downloaded — IPEX-LLM needs the Level
      Zero runtime that ships with it.
- [ ] **IPEX-LLM Windows release** downloaded (or Intel's current Ollama-XPU
      build — check which is newer on the day). Confirm it advertises
      Battlemage / Arc Pro support, not just Alchemist.
- [ ] **DDU** (Display Driver Uninstaller) downloaded and put on the desktop.
- [ ] **Candidate planner GGUFs** pre-pulled so the first night isn't a 40 GB
      download:
  - `qwen3:14b` (≈ 9 GB) — first single-card target
  - `qwen3:32b` (≈ 20 GB) — second single-card target
  - keep `gpt-oss:20b` as the critic (already on disk from the 4070 Ti rehearsal)
- [ ] **BIOS check** — boot into BIOS, confirm **Resizable BAR / Above 4G
      Decoding = Enabled**. Arc cards crater without ReBAR; if it's been off
      this whole time on the 4070 Ti, you won't notice until the Arc is in.
- [ ] **Display cabling** — confirm the B70's outputs (likely 4× mini-DP) match
      your monitor cables. If not, have adapters on hand *before* you pull the
      4070 Ti. Don't get stuck with no display.
- [ ] **PSU check** — one B70 is ~200 W. Two is ~400 W of GPU before CPU. Note
      the 8-pin (or 12VHPWR / EPS, whichever the B70 uses) cable count your PSU
      can deliver.
- [ ] **PCIe slot layout** — confirm the board does **x8/x8** across the two
      primary slots, not x16/x4. Matters less for Approach A (independent
      endpoints), critical for Approach B (tensor-parallel).
- [ ] **Keep the 4070 Ti accessible** — anti-static bag on the desk, not at the
      bottom of a box in the closet. You want a 10-minute revert path.

## Phase 1 — single B70

### Driver swap

1. **DDU in Safe Mode** with the 4070 Ti still installed. Remove NVIDIA cleanly.
   Don't try to coexist NVIDIA + Intel drivers across the swap — it works in
   theory and fails weirdly in practice.
2. Power down, **pull the 4070 Ti**, **install the first B70** in the primary
   PCIe slot.
3. Boot. Confirm display works on the B70.
4. Install the **Intel Arc Pro driver**, then the **oneAPI Base Toolkit
   runtime**. Reboot.
5. Sanity: open Device Manager → Display adapters → "Intel Arc Pro B70" with no
   yellow bang. `Get-CimInstance Win32_VideoController | Select Name, DriverVersion`.

### IPEX-LLM (or Ollama-XPU) endpoint

6. Install the IPEX-LLM Windows release (or Ollama-XPU). Whichever you chose
   above.
7. Point it at the pre-pulled GGUFs. Bring up the endpoint on the standard
   Ollama port (`http://localhost:11434/v1`) so existing ember config keeps
   working unchanged — the swap proxy can stay out of the picture for now
   (single card, no contention to mediate).
8. Smoke-test the endpoint directly before involving ember:
   ```powershell
   curl http://localhost:11434/v1/chat/completions `
     -H "Content-Type: application/json" `
     -d '{ "model": "qwen3:14b", "messages": [{"role":"user","content":"say hi"}] }'
   ```
9. Confirm the model is **on-GPU** — IPEX-LLM logs should show XPU device, and
   Task Manager → Performance → GPU should light up during generation. If the
   work lands on CPU, stop and fix the runtime install before going further.

### Loop smoke test

10. Update `appsettings.Development.json` if model ids changed (`qwen3:14b`
    instead of `qwen3:8b`). Critic stays `gpt-oss:20b`.
11. **Console runner** — fastest signal:
    ```powershell
    dotnet run --project src/Ember -- plan "<brief>" <repo>
    ```
12. Watch the first round complete end-to-end. If it does, run the loop to
    `approved` or to the round cap. Note times.
13. Log the run in `RnD/` (next file in the `06-ollama-rehearsal-runs.md`
    sibling sequence — call it `07-arc-single-card.md`).

### Gaming check

Before committing to the second card, prove daily-driver gaming is tolerable
on this hardware + the Arc Pro driver.

- Pick **one game you actually play** and run it for 20 minutes. Note any
  crashes, perf cliffs, or rendering artifacts.
- Expect edge cases on older DX9 / DX11 titles — the Pro driver is
  workstation-tuned, not gaming-tuned.
- If gaming is unacceptable: try the **unified Arc consumer driver** instead
  (uninstall Pro, install consumer). IPEX-LLM may or may not be happy on it;
  rerun the loop smoke-test after.
- If both drivers fail gaming hard, that's a signal to **leave the second B70
  in the box**, reinstall the 4070 Ti as the display GPU, and run the single
  B70 as a compute-only card alongside it. Revisit at Linux migration time.

## Phase 2 — add the second B70

Only do this once Phase 1 *and* the gaming check are both green.

1. Power down. Install the second B70 in the second x8 slot.
2. Boot. Confirm both cards appear: Device Manager and
   `Get-CimInstance Win32_VideoController`.
3. **No driver reinstall needed** in principle — same driver covers both.
   Reboot once for cleanliness.
4. Decide endpoint topology:
   - **Approach A (recommended for Windows)** — two independent endpoints, one
     per card. Pin the planner to GPU 0, the critic to GPU 1, via whatever
     device-selector env var IPEX-LLM exposes (`ONEAPI_DEVICE_SELECTOR`
     historically; verify against current release notes). Each card hosts its
     own model resident; no swap proxy needed.
   - **Approach B (defer)** — single endpoint, model split across both cards
     via llama.cpp SYCL `--split-mode row`. Possible on Windows but fragile;
     save for Linux migration unless Approach A surprises you.
5. Update `appsettings.Development.json` so planner and critic point at the two
   endpoints (e.g. `:11434` and `:11435`).
6. **First 64 GB rehearsal run** — console runner, end to end.

## First 64 GB rehearsal — what to learn

Treat the first night as a measurement, not a finish line. Open questions:

- **Real planner-quality ceiling.** Run `qwen3:14b` planner end-to-end, then
  `qwen3:32b`. Does the loop reach a true `approved` on `qwen3:32b`? (No prior
  run has — the 4070 Ti could not host it on-GPU.)
- **Concurrent residency.** With Approach A, do the planner and critic both
  stay GPU-resident across rounds, or does something cause a swap? `nvidia-smi`
  equivalent for Intel is `xpu-smi` — watch it during a run.
- **Round time.** Compare median round wall-clock against the 4070 Ti +
  swap-proxy baseline in `RnD/06-ollama-rehearsal-runs.md`.
- **Critic groundedness on a stronger planner.** Does `gpt-oss:20b`'s critique
  still catch real issues when the plans are sharper, or does it start
  rubber-stamping?

Log all of this in `RnD/07-arc-single-card.md` and `RnD/08-arc-dual-card.md`.
The data is what makes the Approach A vs B call real instead of speculative.

## If something is off

- **B70 not detected after install** — power cable seating; ReBAR not enabled
  in BIOS; PCIe slot not running at expected width (check BIOS).
- **Model loads but inference falls back to CPU** — IPEX-LLM device selector
  not set, or the GGUF quant isn't supported by the XPU path. Try a different
  quant (Q4_K_M is the safe baseline).
- **Endpoint returns garbage tokens** — quant + runtime mismatch, or the
  template isn't being applied. Check IPEX-LLM logs for the chat template it
  actually used.
- **Loop times out mid-round** — raise `Models:*:RequestTimeoutSeconds` in
  `appsettings.Development.json`. First-load times on cold XPU can be long.
- **Second card present but only one used** — device selector. With Approach A
  the planner and critic each need their own selector value pointing at a
  different XPU index.
- **Gaming unacceptable** — see Phase 1 gaming-check fallback.

## Revert path

If anything in Phase 1 goes sideways and a fix isn't obvious within an hour:

1. Power down.
2. Pull the B70.
3. Reinstall the 4070 Ti.
4. Boot, DDU the Intel driver, reinstall NVIDIA.
5. Loop is back on the 4070 Ti + swap proxy per `local-loop-runbook.md`.

You lose a night, not the project. The 4070 Ti stays the safe harbour until
the daily-driver rebuild is done.
