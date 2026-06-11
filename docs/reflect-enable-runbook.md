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

---

## Phase 1 — bring the judges up (vllama, through its gates)

One terminal, leave it open when done — `serve` runs in the foreground.

```powershell
cd D:\work\vllama\src\Vllama
$v = '.\bin\Release\net9.0\vllama.exe'        # dotnet build -c Release first if missing

& $v status                                    # see what's already resident
& $v up --model qwen3-30b-a3b-128k             # judge A backing (vllama-planner, dual-split)
& $v up --model qwen2.5-14b-q4                 # judge B backing (vllama-critic, card 1)
& $v serve                                     # the OpenAI facade on 127.0.0.1:8090 — stays in foreground
```

Notes:
- `up` runs the full safety chain itself (preflight → launch → /health → warmup →
  gate → register). The 22 GB host-RAM floor is enforced for you — if it refuses,
  believe it and free RAM first.
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

This is the real pipeline, read-only: git resolves each repo's 48h baseline, the
graph enriches it, both judges write, divergences get extracted — printed to the
console, nothing persisted, no Discord.

**Checkpoint:** output ends with `status: Ran`, and you can read Recap A, Recap B,
and an Agreement/Divergences section. This is the moment you find out what a
30B + 14B judge pair actually sounds like — worth reading slowly.

If the endpoints are down you'll see judge failures and `status: Failed` — that's
the fail-soft path working; fix Phase 1 and rerun.

---

## Phase 3 — enable in ember

1. **Create the channel** on ember's server (suggestion: `#reflect`).
2. **Copy its id:** Discord → User Settings → Advanced → Developer Mode ON →
   right-click the channel → *Copy Channel ID*.
3. **Set the two switches** (user-secrets — machine-local, never committed):

   ```powershell
   cd D:\work\ember\src\Ember
   dotnet user-secrets set "Ember:Reflect:Enabled" "true"
   dotnet user-secrets set "Ember:Reflect:ChannelId" "<paste the id>"
   ```

4. **Start the bot** (its own terminal; Discord secrets are already set):

   ```powershell
   dotnet run --project D:\work\ember\src\Ember
   ```

**Checkpoint:** the log shows `Reflect scheduler armed for 03:00 local daily`, and
Discord shows ember online with `/reflect` in the command list (commands re-register
on startup).

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

- **Patient:** leave the bot running; tonight at 03:00 it recaps whatever you
  committed today after the baseline run.
- **Impatient:** do some work, commit it, run `/reflect` again — the recap thread
  (`reflect: 2026-06-xx`) appears in the channel.

When the thread appears: read both recaps, then **react on the label message** —
✅ accurate · ✏️ partially · ❌ wrong. The reaction persists to the `recaps` table;
that's the evaluator corpus accruing. Anything you type in the thread is kept as
correction context.

This night starts the **seven-night R2 gate** from the constellation-awareness plan.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `/reflect` replies "Reflect is disabled" | Secrets not set, or bot started before they were — set them, restart the bot. |
| Judges fail with 503 | Alias's model not resident — `vllama status`, re-`up` the missing model, keep `serve` running. |
| `Reflect channel ... not found` in log | ChannelId wrong or bot lacks access — re-copy the id, check channel permissions. |
| A night fails entirely | By design it claims the day and does **not** advance baselines — tomorrow re-reports the same delta. `/reflect` is the manual retry. |
| Host RAM pressure | Don't run Ollama alongside the vllama pair on 32 GB; the lean llama.cpp path is the point until Workstation #99 (64 GB) lands. |
| Recap quality is rough | Expected at first — that's what the seven nights and your labels measure. Judge models are config (`Models:ReflectA/B`), upgradeable after battlemage Q2 without code. |

## What you deliberately do NOT need to do

- No API keys — both judges are local.
- No graph commands — R0's watcher and ember's evidence assembly handle it.
- No board updates — R-phase items go through the digest flow.
- Nothing in this runbook launches a model outside vllama's gates.
