# 19. ember overnight: the morning brief + safe PM reconciliation

Date: 2026-06-17

## Status

Accepted

Builds on [ADR 14](0014-reflect-dual-judge-recap.md) (Reflect), [ADR 17](0017-reflect-manual-trigger-launcher.md)
(manual-trigger launcher), and [ADR 18](0018-graph-first-evidence-and-judge-resilience.md)
(glance-first evidence). Implements `pm/ember-overnight-planner-plan.md`.

## Context

ember's *original* purpose was an overnight planner: groom the backlog while the operator sleeps
so he wakes ready to build. The substrate it needed now exists — the constellation glance
(objective state), Reflect (the recap), the vllama judges, the planner/critic loop. Meanwhile the
2026-06-17 PM session laid bare the cost of *not* having it: an entire human-triggered session
spent hand-reconciling the board and books after a build wave — dedupe, state truth-ups, the
pivot capture, stale-repo triage — work that **evaporates when the session closes**. With 3–5
constellations coming, operator-as-integration-layer doesn't scale.

The reconciliation already has a written playbook — `pm/board-sync.md` — with a load-bearing
idea: reconciliation work splits by **who decides** into three tiers — *auto-safe* (additive,
reversible), *decision* (a human structural call), *editorial* (drafts through the approval loop).

## Decision

**An operator-triggered overnight run that produces a morning brief and does the *safe* PM
reconciliation — propose-first, tiered exactly like `board-sync.md`.**

### E1 — the morning brief (read-only)

`BriefAssembler` assembles the **objective state** deterministically from the glance (in-flight
truth), the latest Reflect recap (last night's narrative), and the board-sync delta, categorising
the constellation into **what changed · what's drifting · needs your call · next-slice
candidates**. `BriefAuthor` (the planner role, on the local `vllama-planner`) writes the brief;
`BriefCritic` (the critic role, on `vllama-critic`) checks it against the objective state and the
author revises once — *planner/critic applied to planning*. Every input is soft: a missing
glance/recap/board degrades to a stated gap, never a crash, and an author-down run still ships the
raw objective state under a loud banner (the read-only state is useful without synthesis).

### E2 — tiered proposals (propose-only)

`BoardSyncReader` shells `board-sync-check.py --json` (a `--json` mode added for this) and folds
its tiers into the brief's **Proposals** section, labelled `[auto-safe] / [decision] / [editorial]`.
When ADO is unreachable (not authed) the checker still returns the filesystem/manifest tiers and
flags `board_available:false`; the brief says so and proposes nothing it can't stand behind.

### E3 — auto-apply the auto-safe tier (gated, scoped)

`SummaryDocWriter` applies **only** the one genuinely-safe, in-repo, reversible reconciliation:
drafting a missing `pm/repos/<name>.md` stub from the manifest/glance (additive, by-name commit,
mirrors `JournalWriter` — never `git add .`). It is gated behind `Ember:Overnight:AutoApplyAutoSafe`
(**off by default** — start propose-only, earn auto-apply on a track record). Everything else —
credentialed auto-safe ops (ADO area paths), every decision, and the entire editorial/Discord
tier — is *surfaced*, never auto-run. What the machine did is always reported in the post.

### Standing + manual-trigger + free-VRAM

The brief posts as a `brief:` thread, journals to `pm/journal/brief/`, and is labelled by the same
✅/✏️/❌ reaction machinery as Reflect. It is disabled by default and **manual-only**: a
`Start-Plan.ps1` launcher (sibling of `Start-Reflect`) warms the judges through vllama's gate,
fires one run via a loopback `/brief` trigger, and frees VRAM after. `dotnet run -- brief
--dry-run` prints the objective state read-only, no models.

## Consequences

- **The PM load becomes a standing machine function.** The work the 2026-06-17 session did by
  hand — read the objective state, tier the reconciliation, apply the safe part — runs on a
  trigger and leaves a durable journal trail, instead of living in a session's working memory.
- **Honest by construction.** Propose-first; the only writes are gated, additive, in-repo, and
  reported. The editorial/Discord tier is never auto-touched — the gift-not-broadcast rule holds.
- **Reuse over new surface.** The brief runs on the same vllama judges, the same journal/label
  machinery, and the same launcher pattern as Reflect; `board-sync.md`'s tiers are the playbook,
  not a re-invention.
- **Validated read-only (E1/E2).** `brief --dry-run` against the live constellation surfaced 7
  changed repos, the deprecating-with-churn and drift tensions as *needs-your-call*, ranked
  next-slice candidates, and `board-sync` `IN SYNC` — in ~1.9k chars. The authored brief (judges)
  is deferred to a warmed run (free-VRAM), exactly like ADR 18's judge arm.

## Alternatives considered

- **Auto-apply the full auto-safe tier (incl. ADO area paths).** Rejected for now: area-path
  creation needs `az` creds and is harder to reverse than an in-repo file; `board-sync.md` itself
  keeps it human-run. It graduates to auto-apply when a `--apply` lands in the checker and a track
  record is earned.
- **Use the cloud planner/critic loop.** Rejected: the free-VRAM / warm-judges constraint and the
  "vllama judges" substrate point to the local cards; the cloud loop stays for *building*.
- **One combined Reflect+brief run.** Kept separate: Reflect recaps what happened (evidence,
  dual-judge divergence); the brief plans what's next (synthesis, proposals). Different jobs,
  different cadence, different acceptance.
