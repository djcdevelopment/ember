# 18. Reflect evidence is glance-first, and judges degrade loudly

Date: 2026-06-17

## Status

Accepted

Refines the evidence assembly of [ADR 14](0014-reflect-dual-judge-recap.md); pairs with the
vllama serve-readiness fix (vllama ADR-0007). Tested-by: EXP-0002.

## Context

Reflect's first real run (night of 2026-06-17) **lost a head-to-head with a hand survey**, and
the way it lost named two distinct defects:

1. **Commit-blindness.** Evidence was assembled commit-led — per allowlisted repo, the git delta
   since the last recap. A repo appeared only if it had *committed* something. So the run
   recapped 2 repos (`b70tools`, `ember`) and missed the in-flight majority: leopard's planner
   (20 uncommitted files), Tempo (12), raidui (27), and the framing of ember's own 8-commit
   night. Commit-age lies — the working tree is where the night's work actually was. This is the
   exact failure the constellation-glance practice exists to kill (`silence-is-synthesis`), and
   Reflect was carrying it.

2. **Silent single-judge degrade.** One judge `503`'d (a slot still loading behind the vllama
   facade — see vllama ADR-0007) and Reflect quietly dropped to a one-perspective recap with a
   buried footnote. A recap you can't tell is degraded is worse than one that shouts it.

The constellation glance (`pm/scripts/constellation-glance.py --json`, shipped the same day) is
the read that fixes #1: per repo it reports uncommitted WIP, recent commits, branch/unpushed
state, lifecycle, and drift — assembled from `git status` + the manifest, not commit recency.

## Decision

**Evidence is glance-first, and judge failure is loud.**

### Glance-first evidence (RF1)

- A new `GlanceReader` shells the glance (`{python} constellation-glance.py --json`) behind the
  same soft-fail seam as `GraphContext` / `ManifestLoader`: any IO / parse / subprocess problem
  returns an empty read with a logged warning, and Reflect falls back to the commit-led path
  with a **stated** note (`Constellation glance unavailable — evidence is commit-led only`). The
  glance is read-only.
- `EvidenceAssembler` consumes the glance as the **primary** read — lifecycle, branch/unpushed,
  drift — and a repo is **in flight** when it has uncommitted WIP **or** unpushed commits **or**
  a committed delta (no longer committed-delta alone). Quiet-but-drifting repos surface in a
  dedicated drift line so they don't vanish.
- WIP **paths** are read locally (`git status --porcelain`), not from the glance — the glance
  carries only a count, and the recap's `<from>` citation contract (ADR 16) needs paths that
  appear verbatim in the evidence. The glance's unique contribution is the manifest-derived
  framing (lifecycle, drift) a single repo's git cannot produce. Symbols (code graph) still
  enrich, now over committed **and** in-flight files.

### Loud, resilient judges (RF2)

- Each judge retries its own endpoint on transient errors (5xx/429/408/timeout) with backoff
  (`JudgeMaxAttempts`, `JudgeRetryBaseSeconds`), then — if still down — fails over once to the
  sibling judge's endpoint (the other card; `JudgeFailover`). A truly-down judge still lets the
  survivor run on **full** evidence.
- Degradation is rendered **loudly at the top of the post**, never a silent one-bullet recap:
  a `⚠️ Degraded — single judge` banner when a slot is lost, or `⚠️ Degraded — failover` when a
  recap came from the sibling endpoint (and is therefore **not** an independent second
  perspective — divergence is weakened, and the post says so). Both-judges-down fails the run
  (no invented recap), preserving the baseline for a same-state retry.

## Consequences

- **The miss is fixed (EXP-0002).** Re-run against the same 2026-06-17 night: 2 repos → **8 in
  flight**; leopard/raidui/tempo WIP, lantern's unpushed branch, and ember's 9-commit arc all
  surface, citable by path.
- **Context budget is real but bounded.** The busy-night bundle measured **15,049 / 16,000**
  chars. The glance summary is tiny; the bulk is local WIP + committed file lists. If active-repo
  count climbs, tighten `MaxFilesPerRepo` / `MaxEvidenceCharsPerRepo` before raising the total —
  keep every repo's framing, trim the per-repo file dumps.
- **A new external dependency, softly.** Reflect now prefers a python script living outside ember
  (`gad/pm/scripts`). It is optional: empty `Glance.ScriptPath` or a missing interpreter degrades
  cleanly to commit-led with a stated note.
- **Failover is honest, not magic.** With only two endpoints, a cross-failover means both recaps
  can come from one model; the banner makes that explicit so the operator doesn't read false
  agreement into it.

## Alternatives considered

- **Pure-glance WIP (count only, no local git).** Rejected: the count isn't citable, breaking
  the grounding contract. Local `git status` gives paths for ~free.
- **Drive evidence entirely off the glance's repo list.** Rejected: the allowlist is the security
  boundary; the glance frames the allowlisted repos, it doesn't widen them.
- **Configured third failover endpoint.** Deferred: there are two cards; the sibling endpoint is
  the only real alternate today. Revisit if a cloud fallback is ever added.
