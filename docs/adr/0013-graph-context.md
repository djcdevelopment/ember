# 13. Code knowledge graph as planner/critic context

Date: 2026-06-11

## Status

Accepted

## Context

The planner and critic see a repo through `RepoContext.Gather` — a README excerpt, the
top-level listing, and the `git ls-files` tree — plus, since ADR 11, the constellation
manifest summary. The file tree stops path hallucination but says nothing about *structure*:
what calls what, where the entry points are, which languages and packages make up the repo.

The constellation now runs [codebase-memory-mcp](https://github.com/DeusData/codebase-memory-mcp)
(R0 of the constellation-awareness plan, `D:\work\gad\pm\constellation-awareness-plan.md`):
all 11 manifest repos indexed into local SQLite knowledge graphs, kept fresh by a git
watcher, with a standalone CLI (`codebase-memory-mcp cli <tool> <json>`) alongside the MCP
server. Token cost is the motivating number — structural questions answered from the graph
instead of file reads.

## Decision

A `GraphContext` seam in the loop, mirroring ADR 11's `ManifestLoader` exactly:

- **Subprocess CLI, not an MCP client.** ember already shells out to git, the manifest
  framework, and Claude Code; the graph CLI is the same pattern (`Ember:Graph` options:
  command, timeout, cache dir, master switch). No new protocol dependency in C#.
- **Round-1 weave order: manifest → graph → file tree.** Constellation orientation first,
  repo structure second, ground truth last. The graph section carries `get_architecture`
  (languages, scale, packages, entry points) plus `search_graph` hits for the brief's terms,
  capped at `Ember:Graph:MaxChars`.
- **The `git ls-files` tree stays.** Its anti-hallucination role (ADR 11's "tree is ground
  truth") is load-bearing; the graph section explicitly defers to it for path existence.
- **Uniformly soft failure.** Missing exe, timeout, bad JSON, unindexed repo — every failure
  logs a warning and returns null; planning proceeds with the pre-R1 context. The graph can
  never block a run.
- **The builder needs no code change.** With `Builder:Bare` off (the default), headless
  Claude Code inherits the operator's user-scope MCP registration and gets the same graph
  through the MCP server. If `--bare` is ever enabled, a worktree `.mcp.json` becomes
  necessary — revisit then.

## Consequences

- Round-1 plans can cite real symbols, entry points, and call relationships, and the critic
  can check claims against them — at ~1.5k chars of context instead of file dumps.
- A new external dependency, mitigated by the soft-fail posture and the master switch. The
  exe path is machine-specific config (the npm install ships no real exe on PATH), which is
  consistent with the absolute repo paths already in `appsettings.json`.
- The graph reflects its last index, not necessarily this instant; the watcher plus
  `auto_index` keep drift small, and the file tree remains the authority on paths.
- Test seam: `RunToolAsync` is protected-virtual, pinned by canned-JSON tests like the
  manifest loader's.
