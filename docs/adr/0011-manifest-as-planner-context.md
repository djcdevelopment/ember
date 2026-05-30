# 11. Constellation manifest as round-1 planner context

- Status: Accepted
- Date: 2026-05-30

## Context

The constellation-manifest framework (`D:\work\manifest`, v1.5.0 — see ADR-011
in that repo) shipped a stable consumer surface this morning: a single CLI
command, `framework load <constellation.yaml> --json`, that emits the validated
manifest as canonical JSON on stdout. Errors go to stderr, the exit code
conveys outcome, additive-only changes within a `schema_version`. It is the
framework's first true integration test — every prior framework version (v0.1
through v1.4) operated on the framework's own outputs.

ember is the reference consumer. As a Discord-driven planner-builder pipeline,
ember has always been blind to constellation context: `RepoContext.Gather`
gives the planner README + top-level entries + the tracked-file tree, but
nothing about *what the repo is for in the larger system* — its role, its
neighbors, its archetype, its place in any active saga. A constellation
manifest names exactly those facts in a structured, validated form. Folding
them into the round-1 prompt is the smallest change that turns "ember plans
against a directory" into "ember plans against a directory inside a known
constellation."

A separate but adjacent question: **how** does ember reach the manifest? The
manifest framework is Python; ember is .NET 9. ember could have:

- Imported the Python validator. Excluded by .NET / Python boundary plus a
  Pydantic version dependency.
- Parsed the raw YAML on the consumer side. Excluded because that
  re-implements the validator (defaults, cross-field invariants, the
  `intent`-field semantics) on the consumer side, where it would silently
  drift from framework truth.
- Stood up an HTTP service against the framework. Excluded — adds a daemon,
  an auth surface, and a long-lived process for what is fundamentally a
  startup-time configuration read.

The framework's ADR-011 ratifies the JSON-via-CLI shape. ember follows it.

## Decision

ember consumes constellation manifests by shelling out to
`framework load <path> --json` (or `python -m constellation_manifest.cli load
<path> --json` when the console-script isn't on PATH), parsing stdout as JSON
via `System.Text.Json`, and folding a 5-to-10-line prose summary into the
round-1 planner prompt.

Concretely:

- **Configuration shape.** The `Ember:Repos` allowlist record gains an
  optional `constellation` field, expressed as an object form alongside the
  preserved legacy string form. Both parse simultaneously:
  ```jsonc
  "Ember:Repos": {
    "ember": "D:\\work\\ember",                  // legacy string — still valid
    "tempo": {
      "path": "D:\\World of Warcraft\\Tempo",
      "constellation": "D:\\work\\gad\\constellation.yaml"
    }
  }
  ```
  An `IPostConfigureOptions<EmberOptions>` (`EmberOptionsPostConfigure`)
  walks `Ember:Repos` once at startup and detects each child's shape.
- **What the round-1 fold contains.** From the manifest's JSON projection:
  - Constellation `name`, `archetype`, `topology`, `description`.
  - `intent` if set (one line).
  - The consumed repo's own record (matched case-insensitively against
    `name` then `aliases`): `role`, `producer_type`, `lifecycle`,
    `surfaces[]` when non-empty.
  - Neighbor repos — one line each: `name — role`.
  - Active saga `active_epic_ids` when present.
  Skipped for v1.5 Slice B: `contracts`, `data_sources`, `pipeline_tools`,
  the `discord` block, telemetry config, `agents`, `build_handoff`. They
  rejoin only when planner signal demands them.
- **When ember reads the manifest.** Each `/plan` invocation. Not cached
  across invocations — invocations are minutes apart and the operator may
  have edited the YAML between them. Inside one invocation the manifest is
  read exactly once and reused for any planner-and-critic rounds that come
  out of that invocation. This follows the consumer-pattern doc's
  "validate-once-at-consumer-startup" guidance, adapted to ember's
  per-invocation rather than per-process model.
- **When the framework errors.** Soft-fail uniformly. Any failure (missing
  YAML, framework not on PATH, validation error, schema_version newer than
  this consumer knows how to read, malformed JSON, timeout) is logged at
  warning level and planning continues *without* manifest context. The
  planner's behavior in the unconfigured case (no constellation in the
  allowlist record) and the failure case (constellation set but unreadable)
  is identical — no manifest section in the prompt, no thrown exception.
  Manifest read failures must never block planning.
- **Where it lives in code.** `src/Ember/Manifest/`:
  - `ManifestDocument.cs` — the subset of fields the summary reads,
    annotated for `System.Text.Json` against the framework's snake_case
    serialization.
  - `ManifestSummary.cs` — pure parse-and-format. Tests drive it directly.
  - `ManifestLoader.cs` — the subprocess shell-out + error handling.
    `LoadJsonAsync` is `protected virtual` so tests substitute canned JSON.

## Consequences

- **The substrate claim becomes observable from outside the framework.** Until
  now the framework consumed its own outputs only. The first `/plan tempo`
  invocation whose round-1 plan mentions Tempo's role in GAD, its neighbors,
  or the active saga epics is the first evidence — external to the framework —
  that the manifest is a substrate.
- **Builder is unaffected.** The plan snapshot (`plan_snapshot`) is the build
  seam. The builder reads `.ember/PLAN.md`, not the manifest, not the round-1
  prompt. This change is upstream of the snapshot — it changes what the
  planner *sees in round 1*, not the snapshot shape, the worktree contract,
  or the PR body.
- **Schema version is the compatibility seam.** The consumer pattern's
  contract delegates upgrade-compatibility to the consumer's
  `schema_version` check. ember caps at `MaxSchemaVersion = 1`
  (`ManifestOptions`); a v2 manifest would be treated like any other read
  failure — warn, proceed without manifest context — until ember is updated.
- **The legacy-string `Ember:Repos` shape keeps working.** No operator with
  an existing `appsettings.json` needs to edit it. Adding a constellation is
  opt-in per repo.
- **Slice B intentionally folds only a thin slice.** Contracts, data sources,
  pipeline tools, telemetry config, and the discord block are all in the
  JSON; the fold deliberately leaves them out. Slice C decides what to add
  back based on what planner rounds reveal is missing, not speculation. Three
  similar lines in the prompt is better than a premature contracts block.
- **`/plan` invocations against repos with no `constellation` configured are
  unchanged.** The planner sees only the file-system context, as before.
- **Failure mode discipline is explicit.** Soft-fail is the design, not an
  oversight: a flaky framework install, a YAML that fails revalidation, a
  schema bump — none of these should keep the operator from kicking off a
  `/plan`. The warning is the operator-actionable signal; the planner runs
  regardless. Boot recovery doesn't apply (no persisted state from the
  manifest), so there's no analogue of the ADR-008 "fails closed" posture.
- **Acceptance test is a real `/plan tempo`.** A unit-test summary that
  contains the right tokens is necessary but not sufficient — the
  load-bearing check is that the round-1 prompt the planner actually receives
  contains those tokens in production wiring. The `PlanCli` console runner
  (`dotnet run -- plan "<brief>" tempo`) is the smoke harness; the
  integration test in `tests/Ember.Tests/ManifestLoaderIntegrationTests.cs`
  pins the live-subprocess path against `D:\work\gad\constellation.yaml`.

## Related

- `D:\work\manifest\docs\MANIFEST-CONSUMER-PATTERN.md` — the contract this
  consumer follows.
- `D:\work\manifest\docs\adr\011-manifest-consumer-contract.md` — the
  framework-side ADR that ratifies the JSON-via-CLI shape.
- ADR 3 — the OpenAI-compatible `IChatClient` posture, which ember keeps
  unchanged: the manifest summary is just text the planner sees, not a
  protocol change.
- ADR 8 — boot-recovery fails closed. The manifest read is intentionally not
  in that scope: a missing or broken manifest is a degraded read, not a
  stale active session.
