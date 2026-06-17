# 15. Reflect re-indexes before it reads, and journals to git

Date: 2026-06-17

## Status

Accepted

## Context

Two findings landed in one session, both from *using* the perception layer
rather than trusting it.

1. **The graph goes stale.** A `search_graph` for the reflect apparatus
   returned **0** results — ember's graph had not been re-indexed since
   2026-06-11, six days and the entire R1/R2 feature ago. `auto_index` did not
   refresh it across sessions. This matters beyond discovery: Reflect's
   `EvidenceAssembler` enriches each changed repo with symbols from that graph
   (`GraphContext.SymbolsForFilesAsync`), so a stale graph means silently stale
   enrichment — a correctness defect smuggled in through the evidence, the very
   failure class the dual-judge design exists to catch.
2. **Reflect is about to become a cron / on-demand job**, and its history
   should be durable in git — not only in Discord and SQLite.

## Decision

- **Re-index before read.** `GraphContext.ReindexAsync` reuses the same
  `cli index_repository` subprocess seam; `EvidenceAssembler` calls it for each
  *changed* repo (only those reach enrichment, so cost is bounded) just before
  reading symbols. Gated by `Ember:Graph:ReindexBeforeRead` (default on),
  soft-fail like every graph call. The indexer reads the working tree, so the
  trigger is "this repo changed," not "someone committed." Correctness does not
  depend on the background watcher.
- **Journal to git.** `JournalWriter` writes each real recap to
  `Ember:Reflect:JournalDir`/`<date>.md` and, when `Ember:Reflect:CommitArtifacts`
  is set, commits it **additively and by name** — never `git add .`, never a
  history-rewriting op (the repo rule). Writing is on by default once a
  `JournalDir` is set; committing is off until the unattended run is trusted.
  The console `reflect` taste stays read-only and journals nothing.

## Consequences

- Reflect's enrichment is correct-by-construction: it never reads a graph it
  has not just refreshed for the repos in play.
- Every scheduled or on-demand run leaves a versioned artifact, so the
  constellation-awareness corpus (the publishable record) accrues
  automatically — the experiment-corpus plan's "history in git" requirement,
  met early.
- Re-indexing adds latency (seconds per changed repo; b70tools ~15s). Fine for
  a batch/nightly job; `ReindexBeforeRead=false` restores fast interactive
  runs. `Graph:TimeoutSeconds` was raised 30 → 60 to cover a large-repo
  re-index.
- The freshness discipline generalizes: **any consumer that must trust a graph
  read should re-index or verify freshness first.** Recorded as the standing
  lesson, not just a Reflect detail.
