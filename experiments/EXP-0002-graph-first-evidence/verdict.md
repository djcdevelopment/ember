# EXP-0002 — verdict

*Quantitative where the bundle is countable; qualitative where it's a judgment call. Both
labelled. Raw after-bundle in `results/evidence-after-glance-first.txt`; frozen input in
`inputs/glance-2026-06-17.json`.*

## Headline (quantitative)

| Metric | Before (commit-led, production run) | After (glance-first) |
|---|---|---|
| Repos surfaced | **2** (`b70tools`, `ember`) | **8 in flight**, 2 quiet |
| In-flight (uncommitted) repos surfaced | **0** | **5** (ember 10, leopard 20, raidui 27, tempo 12, hearth 3) |
| ember's commit arc | present but not the headline | **9 commits since baseline**, framed as the night's headline |
| Unpushed-but-quiet repos flagged | 0 | lantern `[unpushed]` (3 ahead, 0 WIP) |
| Lifecycle / drift framing | none | per repo (e.g. raidui `deprecating`) |
| Evidence size | ~8.8k chars (night zero window) | **15,049 chars** (under the 16k cap) |

## Read of it (qualitative)

- **The miss is fixed.** Every item the hand survey caught and the production recap dropped —
  leopard's planner WIP, Tempo 12, raidui 27, ember's own night — is now first-class evidence,
  cited by concrete file paths (the WIP paths are read locally so the judge's `<from>` citation
  contract still holds; the glance only carries a count).
- **The glance earns its keep beyond WIP.** lantern surfaces as *unpushed-but-otherwise-quiet*
  (3 commits ahead, no WIP) — a state neither a commit-since-baseline delta nor a WIP scan would
  raise on its own. raidui carries its `deprecating` lifecycle, so 27 dirty files in a
  wind-down repo reads differently than 27 in an active one.
- **Parity-or-better vs the hand survey: met** for the evidence layer. The bundle now contains a
  superset of what the hand survey assembled (it adds branch/lifecycle/drift framing the survey
  did it by feel).

## Caveat — context budget (the open question from the plan, now measured)

15,049 / 16,000 chars with 8 in-flight repos. The cap **held** (b70tools' file list truncated
cleanly), but the headroom is thin on a busy night. The glance summary itself is tiny; the bulk
is the local WIP + committed file lists. If active-repo count climbs, the right lever is
tightening `MaxFilesPerRepo` / `MaxEvidenceCharsPerRepo` before raising the total — keep the
glance framing for every repo and trim the per-repo file dumps. Logged for the next iteration.

## Deferred — the judge arm

The dual-judge re-run (does the *recap* now read parity-or-better, both judges or a loud labelled
degrade?) is deferred: it loads the 30B+14B onto the cards, against the free-VRAM / manual-trigger
constraint. Resilience is covered by unit tests (`ReflectRunnerResilienceTests`: retry → failover
→ loud degrade → both-down-fails). When the operator next warms the judges (`Start-Reflect`),
capture both recaps here as `results/recap-{a,b}.txt` and label the run via the thread reaction
(✅/✏️/❌) — that closes the before/after pair the corpus wants.

## Adopted (ADR-0018)

- Evidence assembly is **glance-first**: `GlanceReader` (soft-fail subprocess seam, mirrors
  `GraphContext`/`ManifestLoader`) + `EvidenceAssembler` consuming WIP/branch/lifecycle/drift as
  primary, commit-delta + symbols as detail.
- A repo is *in flight* on WIP **or** unpushed **or** committed delta — not committed-delta alone.
- Judge resilience: transient-retry → cross-endpoint failover → **loud** labelled degrade,
  never a silent single-bullet recap.

## Label

Production miss of 2026-06-17 = the corpus's first ✏️ (partially-right: it recapped real commits
but missed the in-flight majority). This experiment is the **after** of that pair. Grade the next
live recap via the thread reaction to confirm the fix lands in the *recap*, not just the evidence.
