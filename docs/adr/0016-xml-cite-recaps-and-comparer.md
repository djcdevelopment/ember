# 16. XML-cite recaps and the contradiction-or-omission comparer (from EXP-0001)

Date: 2026-06-17

## Status

Accepted

Tested-by: EXP-0001 (`experiments/EXP-0001-comparer-format/`)
Adopts: recap-prompt/v2-xml-cite, comparer-prompt/v2-contradiction-or-omission, comparer-schema/v2-xml

## Context

Night zero (the first live reflect, ADR 14) exposed two quality problems: the divergence
comparer over-fired — pairing emphasis differences as "divergences" — and the 30B recap author
hallucinated a cross-repo dependency (`ember's Reflect relies on leopard's Explorer`, false).
Derek hypothesized XML would beat JSON, especially for the larger model. Rather than assert it,
we ran **EXP-0001** — a factorial A/B isolating prompt-wording from output-format — on the local
judges. This is also the first decision recorded under the experiment-corpus practice.

## Decision

Adopt the experiment's winners:

- **Recap author → `recap-prompt/v2-xml-cite`.** Every claim must carry a `<from>` citing a
  hash/path present in the evidence. In EXP-0001 this took the 30B from hallucinating cross-repo
  dependencies in 3/3 plain runs to 0/3 — not by suppressing cross-repo reasoning but by forcing
  it to hedge what it cannot cite. `RecapXml` renders the XML to markdown for the post and scores
  grounding (the `<from>`-in-evidence fraction) as a per-recap trust signal shown in the post.
- **Comparer → `comparer-prompt/v2-contradiction-or-omission` + `comparer-schema/v2-xml`.** The
  prompt rewrite ("report only contradiction/omission, not tone") was the dominant win against
  over-firing; XML was the cleaner, ~30%-faster add-on, and adds a `kind` field.

## Consequences

- Recaps are grounded-by-construction and carry a visible citation score; the hallucination
  class is closed at the source.
- Provenance is complete: the prompts live as versioned contracts, pinned to the code by
  `ContractDriftTests`, and this ADR points at the experiment that justified them. This is the
  worked example of the corpus loop — decision → experiment → contracts.
- **Production correction (same day):** the comparer's XML parse failed on the first live run
  because recaps contain "Threads & risks" and the 14B does not entity-escape — a bare `&` broke
  the strict parser. The inverse of "XML is more robust": here the failure mode is
  entity-escaping, not brace-counting. Fixed with `RecapXml.SanitizeForParse` (escape bare
  ampersands) before parsing; both the recap render and the comparer use it. Standing lesson:
  XML output from a local model needs entity-sanitization ahead of a strict parser.
- Residual quality (the comparer still occasionally mislabels emphasis or echoes the schema
  placeholder) is deliberately left to the seven-night label corpus rather than over-tuned now.
