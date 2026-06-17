# contracts/ — versioned prompt & schema contracts

A **contract** is something Reflect commits to that evolves: a prompt, an output schema. Each
version is a frozen record; the *active* version is the one the code uses now. The discipline
(experiment-corpus plan, `D:\work\gad\pm\experiment-corpus-plan.md`):

- A **decision** (an ADR) adopts a specific contract version *because* a named experiment
  showed something. A contract version's frontmatter names its `introduced-by` (EXP + ADR).
- The live prompt constants in code are pinned to their active contract by
  `tests/Ember.Tests/ContractDriftTests.cs` — the file body and the code constant must match
  (whitespace-normalized), so neither can drift from the other without failing the build.
  (This is the "embed + assert" enforcement from the plan; an embedded-resource loader is the
  possible next step if duplication ever chafes.)

| Contract | Active | History |
|---|---|---|
| recap-prompt | [v2-xml-cite](recap-prompt/v2-xml-cite.md) | v1-plain (superseded — let the 30B hallucinate) |
| comparer-prompt | [v2-contradiction-or-omission](comparer-prompt/v2-contradiction-or-omission.md) | v1-original (superseded — over-fired on emphasis) |
| comparer-schema | [v2-xml](comparer-schema/v2-xml.md) | v1-json (superseded) |

All three v2s were adopted together by **ADR-0016**, citing **EXP-0001**
(`experiments/EXP-0001-comparer-format/`).
