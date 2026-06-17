# Retro: Reflect made trustworthy, and ember re-aimed at its origin job

*Two builds in one session: (#2) made the Reflect recap trustworthy — glance-first evidence so it
stops being commit-blind, plus judge resilience so one 503 can't gut a recap; and (#3) re-aimed
ember at the job it was built for — an overnight backlog planner that authors a morning brief and
does the safe PM reconciliation while the operator sleeps.*
*Date: 2026-06-17 · Scope: working tree since `1508cb2` (all of this session's work, uncommitted)*

---

## What shipped

Two layers landed on top of the Reflect substrate, both following the same arc: a failure the
2026-06-17 live run / PM session exposed, a fix grounded in the constellation glance, and a
deterministic read-only acceptance artifact because the GPU judges couldn't be exercised (free-VRAM
constraint; both vllama slots reported `ready:false` all session).

**#2 — Reflect graph-first (RF1-RF3, ADR 18, EXP-0002).** The first real Reflect run lost a
head-to-head with a hand survey: it recapped only the 2 repos with fresh *commits* and missed all
uncommitted WIP (leopard 20, Tempo 12, raidui 27) plus ember's own 8-commit night. Root cause:
evidence was commit-led. The fix introduces `GlanceReader` (a soft-fail subprocess seam over
`constellation-glance.py --json`, mirroring `GraphContext`/`ManifestLoader`) and reworks
`EvidenceAssembler` to treat in-flight WIP / branch-unpushed / lifecycle / drift as the *primary*
read, with the commit-delta and code-graph symbols as detail. WIP paths are read locally via
`git status --porcelain` so they stay citable under the ADR-16 `<from>` contract. Judges gained
transient retry -> cross-endpoint failover -> a *loud* labelled degrade banner; both-down fails the
run. Re-run against the same night: 2 repos -> **8 in flight**, 15,049 / 16,000 evidence chars.
Logged as `experiments/EXP-0002-graph-first-evidence/`.

**#3 — ember overnight (E1-E3, ADR 19).** A whole PM session had been spent hand-reconciling the
board and books — work that evaporates when the session closes. The new `src/Ember/Overnight/`
subsystem makes it standing: `BriefAssembler` reads the objective state (glance + last recap +
board-sync delta) and categorises it into *changed / drifting / needs-your-call / next-slice
candidates*; `BriefAuthor` + `BriefCritic` (planner/critic roles on the local vllama judges) write
and review the brief; `BoardSyncReader` folds in the `board-sync-check.py --json` tiers
(propose-only); `SummaryDocWriter` applies only the one gated, in-repo, reversible auto-safe op
(drafting a missing `pm/repos/<name>.md` stub) and surfaces everything else. Standing plumbing
(scheduler, loopback `/brief` trigger, `/brief` command, `BriefStore`, journal, reaction labels,
`Start-Plan.ps1` launcher) mirrors Reflect.

Test suite: **92 passing, 0 warnings** (+9 from the overnight build; the Reflect build added the
WIP/glance/resilience tests within the prior 83). Both builds' judge arms are deferred to a warmed
run; the deterministic console dry-runs are the acceptance artifacts. Nothing is committed yet —
this retro and the commit are the close.

A cross-repo note: the read-side dependencies live in `D:\work\gad\pm\scripts\` — this session also
added a `--json` mode to `board-sync-check.py` and updated the two `pm/` plans + the experiments
ledger there. Those are separate gad-repo commits; this retro and commit cover the ember repo.

---

## Engineering Lead perspective

The load-bearing architectural decision in #2 was *where the in-flight truth comes from*. The plan
said "consume the glance as primary," but the glance reports WIP as a count, not paths — and the
recap's citation contract (ADR 16) needs paths that appear verbatim in the evidence. The resolution:
the glance drives *which repos are in-flight and how* (lifecycle, drift, branch state — things a
single repo's git can't compute), and local `git status --porcelain` provides the *citable paths*.
That split keeps the glance a small summary (context budget stays bounded) while preserving
grounding. It also means a fully-down glance degrades to commit-led with a stated note rather than
losing WIP entirely — the soft-fail posture every seam in this codebase shares.

For #2's resilience, the honest design call was admitting that cross-endpoint failover on a two-card
rig produces two recaps from one model — not an independent second perspective. Rather than hide
that, `BuildDegrade` labels it loudly ("the two recaps are not fully independent"). The "single
judge" banner turns out to be reachable only when failover is *disabled*; with failover on, a slot
loss either recovers via the sibling or both are down and the run fails. That asymmetry is
documented in the ADR's alternatives section so the next reader doesn't re-derive it.

#3 is mostly an exercise in disciplined reuse. The overnight subsystem is a near-mirror of Reflect:
same soft-fail subprocess seam (`BoardSyncReader` copies `GlanceReader`), same journal/label
machinery (`JournalWriter` got a config-agnostic overload so both subsystems share it), same
launcher and loopback-trigger pattern (`Start-Plan.ps1` is `Start-Reflect.ps1` with the port and
endpoint swapped). The one genuinely new judgment was the propose/apply boundary: `SummaryDocWriter`
applies exactly one in-repo, reversible operation, gated off by default, and the LLM's narrative
proposals are kept *separate* from what code deterministically applies — the machine applies from
the structured board delta, never from model text. That separation is the safety property.

Test coverage delta: +9 overnight tests (objective-state categorization, board tiering,
`InRepoAutoSafe` isolation, critic JSON parsing, auto-safe gated-off-vs-on, runner loud-degrade),
plus the RF1/RF2 tests from #2. The pure logic — assemblers, tiering, auto-safe selection, parsing,
degrade — is all unit-covered against fakes; no git or models needed, so they run on any host. Debt
introduced: the overnight runner has its own inline retry rather than sharing ReflectRunner's
resilient-judge helper — a deliberate scope call to avoid refactoring passing code, noted for a
future extraction.

---

## Project / Program Manager perspective

Scope planned was RF1-RF3 (#2) and E1-E3 (#3); scope completed was all six phases' *code and
read-side acceptance*, with both builds' *judge arms explicitly deferred* for the same reason: the
authored output needs the 30B+14B resident on the cards, against the free-VRAM / manual-trigger
constraint, and the slots were cold all session. This is not slippage — it's the constraint working
as designed. The deterministic dry-run became the acceptance artifact in both cases (the pattern
EXP-0001 established and EXP-0002 reused), and the deferred arm is logged as a concrete next action
tied to "next time the judges are warmed via Start-Plan / Start-Reflect."

Dependencies resolved: #3 depended on #2 (the glance-first recap is one of the brief's inputs), and
#2 shipped first as instructed. A new soft external dependency was introduced — ember now prefers
two python scripts living outside the repo (`gad/pm/scripts/`). It's optional by construction
(empty `ScriptPath` degrades cleanly), but it's a coupling worth tracking: the ember build and the
gad scripts now co-evolve, and the `board-sync-check.py --json` contract is now load-bearing for
ember.

Schedule reality: faster than a from-scratch build would suggest, because #3 reused ~70% of
Reflect's machinery. The risk surface mostly *shrank* — the silent single-judge degrade (a real
trust bug) is gone, and the PM reconciliation that lived in a human session's working memory is now
a standing function with a journal trail. New risk: the context budget measured at 15,049/16,000 on
a busy night — thin headroom. Logged with the mitigation lever (tighten per-repo file caps before
raising the total) rather than pre-optimized.

Deferred with good reason: (1) both judge arms — free-VRAM; (2) credentialed auto-safe auto-apply
(ADO area paths) — needs `az` creds and a `--apply` flag the checker doesn't have yet, and the
principle is "earn auto-apply on a track record"; (3) extracting a shared resilient-judge helper —
avoid refactoring green code mid-session.

---

## QA / Verification perspective

What was verified by independent evidence: the #2 read side was proven by re-running
`reflect --dry-run --since-hours 24` against the *actual* 2026-06-17 working tree and confirming all
three previously-missed WIP repos (leopard 20, raidui 27, Tempo 12) plus ember's 9-commit arc and
lantern's unpushed branch now surface — a real before/after, not a claim. The #3 read side was
proven the same way: `brief --dry-run` against the live constellation produced correctly-categorized
sections and `board-sync` reporting `IN SYNC` (az/gh were authed on the host, so the board read ran
for real). Both artifacts were captured to disk (EXP-0002 results, and the dry-run output).

The regression surface is well-covered for pure logic: 92 tests, including the load-bearing
correctness properties — that WIP-only repos go in-flight, that a deprecating repo with churn is
flagged as needs-a-call and *excluded* from next-slice recommendations, that the auto-safe applier
surfaces everything when gated off and only drafts the in-repo subset when gated on, and that a
down author degrades loudly to raw objective state rather than silently. The one test bug caught
mid-flight (a fake reader that didn't set `ScriptPath`, so `ReadAsync` short-circuited before the
override) was itself a useful signal — it proved the seam's enable-guard works.

What is NOT covered, and why it's acceptable for now: the authored brief and the dual-judge recap
quality (needs warm GPUs — deferred by constraint, resilience covered by unit tests instead); the
E2 board proposals with *real* deltas (the board was IN SYNC live, so the tiering was exercised only
against canned JSON in tests — acceptable because the tiering is pure parsing); and the E3
git-commit path (tests run with `CommitArtifacts=false` in a temp tree — the commit path mirrors the
already-proven `JournalWriter` pattern). A verification method worth naming as transferable: *the
read-only dry-run as deterministic acceptance* lets a GPU-gated feature be accepted on its
deterministic half today and its model half later, without blocking the ship.

---

## Operator perspective

*(first-person, Derek)*

The thing I keep coming back to: the machine has to be honest about what it can and can't see, and
honest when it's degraded. The first Reflect run looked fine and was quietly wrong — it recapped two
repos and felt complete, while the actual night's work was sitting uncommitted in five others.
That's the worst failure mode for a tool whose whole job is to re-assemble the objective view I lose
in a vertical slice. So #2 wasn't really "add a feature," it was "stop the tool from lying by
omission." Glance-first is the fix because the glance is built on `git status`, which is where the
truth actually is — commit-age lies, and I've been bitten by that enough to have made it a memory.

The free-VRAM constraint is real and it shaped both builds. I'm not going to let an overnight job
fight my late-night build or stream for the B70s, and I'm not going to pretend the judges were warm
when they weren't. The right call was to accept the deterministic read-side as the acceptance bar
for this session and defer the authored output to when I deliberately warm the cards via the
launcher. That's the same posture as ADR 17 — I bring the inference substrate up on purpose, through
its gates, and the tools respect that.

The judgment call on #3 was the propose-vs-apply boundary, and the plan had already answered it:
start propose-only, earn auto-apply, scope it to auto-safe, never touch editorial or Discord. I'm
glad the build held that line hard — `AutoApplyAutoSafe` defaults off, and even on it only drafts an
in-repo stub. The board-sync playbook already encodes "who decides" as three tiers; ember just had
to consume that, not re-litigate it. The one thing that felt right the whole way: this is ember
*going back to its origin job*. It was always meant to groom the backlog while I sleep. The
substrate finally exists, so I pointed it home.

What felt off: I left the bot running the whole session, which locked the build output and forced a
workaround. Small, but it's the kind of process slip that compounds.

---

## How we worked together (human <-> AI)

### What worked well

- **The mission briefs were self-resolving on the hard questions.** Both #2 and #3 arrived as dense
  specs that pre-answered the load-bearing decision — #3's "start propose-only; earn auto-apply
  scoped to auto-safe; never editorial/Discord" meant the AI never had to guess the safety boundary
  or stop to ask. That's the highest-leverage thing the operator did: front-load the judgment into
  the brief so execution can run end-to-end.
- **Soft-fail seam as a reusable template.** `GraphContext`/`ManifestLoader` set a posture (every
  subprocess failure returns null + a logged warning, never blocks the run). The AI applied it
  verbatim to `GlanceReader` and again to `BoardSyncReader`. Recognising "this is the third instance
  of one pattern" kept the new code boringly consistent instead of novel.
- **The read-only dry-run as the acceptance artifact.** When the GPUs were cold, instead of stalling,
  the AI captured `reflect --dry-run` and `brief --dry-run` against live data as the deterministic
  proof — surfacing the actual 8-in-flight-repos result. This turned a blocked acceptance into a
  shipped-and-verified one, and the operator could read the real output in the transcript.
- **The redirected-build workaround.** When the running bot locked `src/Ember/bin`, the AI built and
  tested to `D:\tmp\ember-*` rather than killing the operator's live bot or stalling — a
  non-destructive path that kept the whole session moving. (It also correctly flagged "restart the
  bot to pick up these changes" at the end.)
- **Reuse over invention on #3.** Rather than design an overnight subsystem from scratch, the AI
  mirrored Reflect file-for-file (assembler, runner, executor, service, trigger, store, launcher),
  which made the diff reviewable and the behaviour predictable.

### What didn't

- **The bot was left running the whole session**, locking the build output. Every build/test had to
  be redirected to a temp dir, and the live bot is now running stale code until it's restarted. An
  operator-side slip — the bot should have been stopped before a build-heavy session.
- **The GPU judges were cold the entire session**, so neither build's headline model output (the
  authored brief, the dual-judge recap on fixed evidence) was actually exercised. Both were deferred.
  This is the constraint, not a mistake — but it means two "shipped" features have an unverified half
  until a warmed run happens.
- **A test fake was miscalibrated** — `FakeGlance` didn't set `ScriptPath`, so `GlanceReader.ReadAsync`
  short-circuited to empty before the overridden subprocess method ran, and two tests failed on the
  first pass. Caught and fixed within the session, but it's the kind of seam-guard subtlety that the
  AI should have anticipated from having *written* that guard minutes earlier.
- **E2's board tiering never met a real delta.** The live board was IN SYNC (a prior PM session had
  reconciled it), so the proposal tiering was exercised only against canned JSON. Correct behaviour,
  but the real-world path is still unproven end-to-end.

### Patterns to repeat

- Front-load the safety/scope judgment into the mission brief so execution runs uninterrupted.
- When a feature is GPU-gated, split acceptance into a deterministic read-side (ship + verify now)
  and a model-side (defer to a warmed run, logged as a concrete next action).
- Mirror an existing proven subsystem when building a sibling; keep the diff boring.

### Patterns to change

- **Stop the long-running bot before a build-heavy session** (or run it from a separate published
  build, not `bin/Debug`). The lock workaround is a tax paid every build.
- When writing a test fake for a seam with an enable-guard, set the guard's preconditions in the
  fake's constructor by default — the same lesson the `FakeBoard` fake already encoded but `FakeGlance`
  initially didn't.

---

## Lessons learned

1. **A tool that re-assembles "objective state" must read from where the truth lives.** Commit-age
   lies; `git status` (working-tree state) is the truth for in-flight work. Any overview built on
   commit recency will be confidently incomplete. This generalises past Reflect to any status/digest
   tool.
2. **Degradation must be loud or it's a lie.** A silent fallback to one judge, or to commit-led
   evidence, produces output that looks complete and isn't. The fix is always a labelled banner at
   the top, never a buried footnote — and the label must be honest about *what* was lost (e.g. "these
   two recaps are not independent").
3. **GPU-gating doesn't have to block a ship.** Separate the deterministic half (assembly, tiering,
   parsing, degrade) from the model half (authored prose), accept the deterministic half on real
   data today, and defer the model half to a warmed run. The dry-run is the acceptance artifact.
4. **Encode the safety boundary as a default-off gate, and apply from structured data, not model
   text.** `AutoApplyAutoSafe=false` plus "apply from the board delta, propose from the LLM" makes the
   blast radius a deliberate, reviewable choice rather than an emergent property of a prompt.
5. **A second instance of a pattern is a template; a third is a law.** The soft-fail subprocess seam
   (graph -> glance -> board-sync) and the manual-trigger launcher (Start-Reflect -> Start-Plan)
   both reached three instances this session. Naming them as patterns kept the new code consistent
   and fast to write.

---

## Next moves

- **Run both judge arms on a warmed rig.** `Start-Reflect` then `dotnet run -- reflect` (or `/reflect`)
  to capture the graph-first *recap* into `experiments/EXP-0002-graph-first-evidence/results/recap-{a,b}.txt`
  and react to label it; `Start-Plan` then `/brief` to capture the authored *brief*. Closes both
  before/after pairs. (Tracked in memory: `reflect-graph-first`, `ember-overnight`.)
- **Restart the running ember bot** so it picks up #2 and #3 (it's on stale code, PID 23748).
- **Commit the gad-side changes separately** — `board-sync-check.py --json`, the two updated `pm/`
  plans, the experiments ledger row, and the EXP-0002 frozen inputs live in `D:\work\gad`.
- **Enable the overnight planner when ready** — `Ember:Overnight:Enabled` + `ChannelId` +
  `LocalTriggerPort=8092`; leave `AutoApplyAutoSafe` off until the proposals earn trust. See
  `D:\work\ember\docs\overnight-enable-runbook.md`.
- **Watch the evidence budget** — 15,049/16,000 on a busy night; tighten `MaxFilesPerRepo` /
  `MaxEvidenceCharsPerRepo` before raising the total if active-repo count climbs.
- **Consider extracting a shared resilient-judge helper** when next touching either runner — the
  retry/transient-classification logic is now duplicated across ReflectRunner and OvernightRunner.

---

## Acceptance gates met

- [x] **RF1** — Reflect evidence is glance-first; in-flight WIP surfaces (re-run: 2 repos -> 8 in flight)
- [x] **RF2** — judge retry -> failover -> loud labelled degrade; both-down fails; unit-tested
- [x] **RF3 (read arm)** — re-run vs 2026-06-17 logged as EXP-0002 + ledger row; parity-or-better evidence
- [ ] **RF3 (judge arm)** — dual-judge *recap* re-run deferred (free-VRAM); capture on a warmed run
- [x] **E1** — morning brief assembles objective state (changed/drifting/needs-call/next-slice), verified read-only on live data
- [x] **E2** — tiered board proposals (auto-safe/decision/editorial), propose-only; `--json` added to the checker
- [x] **E3** — gated in-repo auto-safe apply (summary-doc draft), default off; rest surfaced; never editorial/Discord
- [ ] **E1/E2/E3 (judge arm)** — authored brief deferred (free-VRAM); capture via Start-Plan on a warmed run
- [x] **Tests green** — 92 passing, 0 warnings
- [x] **Docs caught up** — ADR 18 + 19, PLAN.md, README, two runbooks, EXP-0002, ledger
- [ ] **Bot restarted on new code** — pending operator (lock held all session)
