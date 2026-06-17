# Reflect enable runbook — the operator evening

The step-by-step path from "Reflect is built and disabled" to "the first recap thread
is in Discord." Each phase ends with a checkpoint; if a checkpoint fails, stop there —
nothing later can break, because nothing later has started.

Total hands-on time: ~20 minutes, most of it watching models warm up.

---

## Phase 0 — two-minute wins (no GPU, do anytime)

1. **Restart the Claude Code CLI session** that showed `1 setup issue: MCP` (the fix
   landed in `.claude.json` on 2026-06-11; MCP servers spawn at session start, so the
   old session still holds the broken entry). Any project directory works.
2. In the fresh session, run `/doctor`.

   **Checkpoint:** no MCP setup issue.

3. Optional taste of R0: ask the session to use the `codebase-memory` tools —
   e.g. *"get_architecture of this repo"* in Tempo. It should answer from the graph
   (15k nodes) without grepping files.

4. **Check the glance** (Reflect's primary evidence since ADR 18). It must be on PATH for
   python and the path in `Ember:Reflect:Glance:ScriptPath`:

   ```powershell
   python D:\work\gad\pm\scripts\constellation-glance.py --json | Select-Object -First 3
   ```

   **Checkpoint:** prints JSON. If it can't run, Reflect still works — it degrades to a
   commit-led recap and says so in the evidence header — but you lose the in-flight (uncommitted)
   WIP that is the whole point of the graph-first fix.

---

## Phase 1 — bring the judges up (vllama, through its gates)

One terminal, leave it open when done — `serve` runs in the foreground.

```powershell
cd D:\work\vllama\src\Vllama
$v = '.\bin\Release\net9.0\vllama.exe'        # dotnet build -c Release first if missing

& $v status                                    # see what's already resident
& $v up --model qwen3-30b-a3b-128k             # judge A backing (vllama-planner, dual: spans both cards)
& $v up --model qwen2.5-14b-q4 --slot slot-b   # judge B backing (vllama-critic) — --slot is required, see note
& $v serve                                     # the OpenAI facade on 127.0.0.1:8090 — stays in foreground
```

Notes:
- **`--slot slot-b` is required on the second `up`.** `up` with no `--slot` defaults to
  slot-a / port 8080, which the first model already holds — so the critic collides on the
  port without it. (Confirmed the hard way 2026-06-17.)
- `up` runs the full safety chain itself (preflight → launch → /health → warmup →
  gate → register). The 22 GB host-RAM floor is enforced for you — if it refuses,
  believe it and free RAM first. Caveat the floor does *not* catch: `--no-mmap` stages a
  model's full file through system RAM during load (a 17 GB model ≈ a 17 GB transient spike
  before it lands in VRAM), so load from a low RAM base and one at a time on 32 GB. Do not
  stage the 70B this way until the 64 GB upgrade (#99).
- Keep this terminal open. `serve` in the foreground IS the facade.

**Checkpoint** (second terminal):

```powershell
curl http://127.0.0.1:8090/v1/models
```

Expect a model list containing `vllama-planner` and `vllama-critic`. A 503 later
means an alias's model isn't resident — re-run the matching `up`.

---

## Phase 2 — taste the recap on the console (no Discord, no database)

```powershell
dotnet run --project D:\work\ember\src\Ember -- reflect --since-hours 48
```

This is the real pipeline, read-only: the constellation glance + git resolve each
repo's in-flight WIP and 48h committed baseline, the graph enriches it, both judges
write, divergences get extracted — printed to the console, nothing persisted, no Discord.

**Checkpoint:** the evidence header reads `N repo(s) in flight … Primary read: constellation
glance` and lists *uncommitted* WIP per repo, not just committed deltas (ADR 18). Output ends
with `status: Ran`, and you can read Recap A, Recap B, and an Agreement/Divergences section.
This is the moment you find out what a 30B + 14B judge pair actually sounds like.

If one endpoint is slow/loading, the run **retries then fails over** to the other card and the
recap carries a loud `⚠️ Degraded` banner naming what happened (ADR 18) — not a silent
single-bullet. If *both* endpoints are down you'll see `status: Failed` — the fail-soft path
working; fix Phase 1 and rerun. To see the glance unavailable-path, the header says
`Constellation glance unavailable — evidence is commit-led only`.

---

## Phase 3 — enable in ember

1. **Create the channel** on ember's server (suggestion: `#reflect`).
2. **Copy its id:** Discord → User Settings → Advanced → Developer Mode ON →
   right-click the channel → *Copy Channel ID*.
3. **Set the switches** (user-secrets — machine-local, never committed):

   ```powershell
   cd D:\work\ember\src\Ember
   dotnet user-secrets set "Ember:Reflect:Enabled" "true"
   dotnet user-secrets set "Ember:Reflect:ChannelId" "<paste the id>"
   dotnet user-secrets set "Ember:Reflect:ScheduleEnabled" "false"   # manual-only (ADR 17)
   dotnet user-secrets set "Ember:Reflect:LocalTriggerPort" "8091"   # desktop-launcher trigger
   ```

   `ScheduleEnabled=false` keeps Reflect enabled for `/reflect` and the launcher but idles the
   nightly 03:00 auto-run; `LocalTriggerPort` arms the loopback trigger the launcher pokes. To
   return to the unattended nightly, set `ScheduleEnabled=true` and `LocalTriggerPort=0`.

4. **Start the bot** (its own terminal; Discord secrets are already set):

   ```powershell
   dotnet run --project D:\work\ember\src\Ember
   ```

**Checkpoint:** the log shows `Reflect schedule disabled (Ember:Reflect:ScheduleEnabled=false);
manual-only` and `Reflect local trigger listening on http://127.0.0.1:8091/`, and Discord shows
ember online with `/reflect` in the command list (commands re-register on startup). (With
`ScheduleEnabled=true` the first line instead reads `Reflect scheduler armed for 03:00 local daily`.)

---

## Phase 4 — first run = baselines (expect silence)

Run `/reflect` in any channel on the server.

The **first run posts nothing** — there are no per-repo baselines yet, so it records
HEAD for every repo and stores a `Skipped` row. The ephemeral reply and the log line
say `baselines recorded`. This is correct, not broken.

**Checkpoint:** log line `Reflect run finished: No committed changes since the last
recap; baselines recorded.`

---

## Phase 5 — the first real recap

Two ways to get there:

- **Launcher (the daily driver, ADR 17):** double-click **`Reflect Now.cmd`** on the Desktop.
  It warms vllama through its gates, ensures the bot, and fires one run — the recap thread
  (`reflect: 2026-06-xx`) appears in the channel.
- **By hand:** do some work, commit it, run `/reflect` — the same run, started from Discord.

When the thread appears: read both recaps, then **react on the label message** —
✅ accurate · ✏️ partially · ❌ wrong. The reaction persists to the `recaps` table;
that's the evaluator corpus accruing. Anything you type in the thread is kept as
correction context.

Each real recap also writes a markdown artifact to `Ember:Reflect:JournalDir`
(`D:\work\gad\pm\journal\reflect\<date>.md`) — the git trail (ADR 15). Committing it is
gated by `Ember:Reflect:CommitArtifacts`, **off by default**: the file is written but not
committed until you flip that on for the unattended cron run. Note too that a real run now
**re-indexes each changed repo before reading its symbols** (ADR 15), so the first seconds
of a run are reindex latency (a large repo like b70tools can take ~15 s) — expected, not a
hang.

This night starts the **seven-night R2 gate** from the constellation-awareness plan.

---

## Daily driver — the manual launcher (ADR 17)

Day to day, Reflect is **manual-only**: nothing fires at 03:00, so a recap never competes with a
late-night build, test, or stream for the B70s. To run one:

> **Double-click `Reflect Now.cmd` on the Desktop.**

It (`scripts/Start-Reflect.ps1`) does, in order, stopping at the first failure:

1. **vllama facade** — starts `vllama serve` (:8090) if it isn't already up.
2. **judges resident** — `vllama up` for planner + critic *through the 22 GB host-RAM gate*. If
   the rig is loaded, vllama refuses and the launcher stops and says so — free RAM / end the
   stream and re-run. It never forces a load.
3. **bot** — ensures ember is up and Discord-connected (starts it if down).
4. **runs the recap** via the loopback trigger and **waits** for the judges to finish (a few minutes).
5. **frees the GPUs** — `vllama kill-all` releases the judges' VRAM so the rig is yours again (the
   facade stays up to speed the next run). Pass `-KeepWarm` to leave the judges resident.

Then react on the label message as in Phase 5. `/reflect` in Discord remains an identical manual
path when you're already in Discord — though it does **not** free the GPUs afterward; the launcher does.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `/reflect` replies "Reflect is disabled" | Secrets not set, or bot started before they were — set them, restart the bot. |
| Judges fail with 503 | Alias's model not resident — `vllama status`, re-`up` the missing model, keep `serve` running. |
| `Reflect channel ... not found` / `not a text channel` in log | ChannelId wrong, points at a voice channel, or bot lacks access — re-copy the id of the `#reflect` **text** channel. (Only surfaces on a real recap; the baseline run never posts.) |
| critic `up` fails `port 8080 already open` | You omitted `--slot slot-b` on the second `up` — it defaulted to slot-a's port. Add it. |
| A run fails entirely | By design it claims the day and does **not** advance baselines — the next run re-reports the same delta. `/reflect` or the launcher is the manual retry. |
| Launcher: "Ember.exe is running but the trigger isn't answering" | The bot predates the trigger or `LocalTriggerPort` isn't set — close the bot window and re-run the launcher to restart it. |
| Launcher stops at "vllama refused to bring up …" | The 22 GB host-RAM gate fired — free RAM / end the stream and re-run. Working as intended, not a bug. |
| Host RAM pressure | Don't run Ollama alongside the vllama pair on 32 GB; the lean llama.cpp path is the point until Workstation #99 (64 GB) lands. |
| Recap quality is rough | Expected at first — that's what the seven nights and your labels measure. Judge models are config (`Models:ReflectA/B`), upgradeable after battlemage Q2 without code. |

## What you deliberately do NOT need to do

- No API keys — both judges are local.
- No graph commands — R0's watcher and ember's evidence assembly handle it.
- No board updates — R-phase items go through the digest flow.
- Nothing in this runbook launches a model outside vllama's gates.
