# Overnight planner enable runbook — the morning brief

*Operator guide for ember's overnight backlog planner (ADR 19) — the sibling of the Reflect
recap. It reuses the same vllama judges, so Phase 1 here is the same bring-up as
`reflect-enable-runbook.md`; this doc covers only what's different.*

The planner reads the **objective state** (the constellation glance + the latest Reflect recap +
the `board-sync` delta) and authors a **morning brief**: what changed · what's drifting · needs
your call · recommended next slice — plus PM proposals tiered like `pm/board-sync.md`. It applies
only the gated, in-repo auto-safe reconciliation and surfaces the rest. Disabled by default,
manual-only, free-VRAM.

## Phase 0 — read-only taste (no GPU, do anytime)

The brief grounds on the glance + `board-sync` + the last recap. Check the inputs first:

```powershell
# the objective state the brief is built from — no Discord, no models, no writes
dotnet run --project D:\work\ember\src\Ember -- brief --dry-run
```

**Checkpoint:** you see `## Changed`, `## Drifting / sitting`, `## Needs your call`,
`## Next-slice candidates`, and a `## Board reconciliation` line. If the glance is missing it says
so (provisional); if ADO isn't authed, board-sync reports `board_available: NO` and the brief
proposes only the filesystem/manifest tiers. Both are non-fatal.

Prereqs for the read: `python` on PATH, and the two scripts in `gad/pm/scripts/`
(`constellation-glance.py`, `board-sync-check.py`) at the paths in `Ember:Overnight` config. The
board tier additionally needs `az` (logged into `steppeintegrations/GAD`) and `gh` (authed) — but
those are optional; without them the brief is glance-fed and says the board tier was skipped.

## Phase 1 — judges up (same as Reflect)

The author (`vllama-planner`) and critic (`vllama-critic`) are the same two judges Reflect uses.
Bring them up exactly as in `reflect-enable-runbook.md` Phase 1 (`vllama serve`, `vllama up` for
both through the 22 GB host-RAM gate, then `vllama ready`). If Reflect already runs on this rig,
nothing new is needed here.

## Phase 2 — taste the authored brief on the console

```powershell
dotnet run --project D:\work\ember\src\Ember -- brief
```

**Checkpoint:** the author writes the four sections + a tiered Proposals section, the critic
reviews and the author revises once, and it ends `status: Ran`. If an endpoint is slow/loading the
call retries; if the author is down the run still prints the raw objective state under a loud
`⚠️ Degraded — author unavailable` banner; if the critic is down the brief prints `⚠️ Degraded —
unreviewed`. Never a silent gap.

## Phase 3 — enable in ember

```powershell
cd D:\work\ember\src\Ember
dotnet user-secrets set "Ember:Overnight:Enabled" "true"
dotnet user-secrets set "Ember:Overnight:ChannelId" "<paste the brief channel id>"
dotnet user-secrets set "Ember:Overnight:ScheduleEnabled" "false"   # manual-only (ADR 17)
dotnet user-secrets set "Ember:Overnight:LocalTriggerPort" "8092"   # the Start-Plan trigger
```

`ChannelId` may be the same channel as Reflect or a separate `#brief`. Leave
`Ember:Overnight:AutoApplyAutoSafe` at its default (`false`) until you trust the proposals — then
turn it on to let ember auto-draft missing `pm/repos/<name>.md` stubs (the only auto-applied op;
additive, by-name commit when `CommitArtifacts=true`).

**Checkpoint:** on bot start the log shows `Overnight schedule disabled … manual-only` and
`Overnight local trigger listening on http://127.0.0.1:8092/`, and `/brief` appears in Discord.

## Daily driver — the launcher

Double-click **`Plan Now.cmd`** (or run `scripts/Start-Plan.ps1`). It warms the judges through
vllama's gate, fires one brief via the `/brief` loopback trigger (waits for author + critic), and
frees VRAM after (`-KeepWarm` to leave them resident). Read the brief in the channel and react
✅ accurate · ✏️ partially · ❌ wrong — the label corpus the planner is graded against. `/brief`
in Discord does the same run without freeing VRAM afterward.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `brief --dry-run` says glance unavailable | `python` not on PATH, or `Ember:Overnight` (and `Ember:Reflect:Glance`) `ScriptPath` wrong. The brief still runs, provisional. |
| Board section says `board_available: NO` | `az`/`gh` not authed. Filesystem/manifest tiers still report; ADO area/epic tiers skipped. |
| `⚠️ Degraded` banner on the brief | A judge endpoint was down after retry. Bring vllama up (Phase 1) and re-run. Expected, never silent. |
| Trigger not answering on :8092 | `Ember:Overnight:LocalTriggerPort` unset or the bot predates it — restart the bot. |
| Want the nightly auto-run | `Ember:Overnight:ScheduleEnabled=true` + `RunAtLocalTime` (default 06:00) + `LocalTriggerPort=0`. |
