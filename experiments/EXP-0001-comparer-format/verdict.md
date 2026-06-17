# EXP-0001 — verdict

*Quantitative where the harness measured it; qualitative where a human read the raw outputs.
Both are labelled. Raw outputs in `results/`.*

## Test A — comparer (divergence count, temp-0 stable)

| Arm | Divergences | Read of them (qualitative) | Parse | Latency |
|---|---|---|---|---|
| A1 json + original | 5, 5, 5 | **5/5 spurious** — emphasis/theme pairings, no real contradictions | 3/3 | ~12s |
| A2 json + improved | 4, 4, 4 | 2 false "contradictions" + **2 genuine omissions** | 3/3 | ~10s |
| A3 xml + improved | 3, 3, 3 | 1 weak + **2 genuine omissions** | 3/3 | ~8s |

- **Wording was the dominant win** (A1→A2): from 5-all-noise to a list with 2 real omissions.
- **XML was a real but junior add-on** (A2→A3): shed one more false positive, ~30% faster,
  cleaner output.
- **Parse robustness: no difference** — JSON parsed 3/3 at temp 0 / 500 tokens. XML's "more
  robust" floor benefit did not manifest *at this scale*. (It returns at higher temp / longer
  output / weaker models — and the inverse bit us in production: see the postscript.)

## Test B — recap grounding (the headline)

- **Plain markdown hallucinated cross-repo dependencies in 3/3 runs** — including night zero's
  exact bug reproduced: *"ember's Reflect feature relies on leopard's Explorer... GraphContext"*
  (fabricated; `GraphContext` is ember's own class).
- **XML-cite: 12/12 citations valid across 3 runs, 0 fabricated.** And the mechanism is the
  prize: the 30B did not stop noticing cross-repo links — it **downgraded** them from asserted
  dependencies to explicitly-hedged hypotheses (*"suggests integration... though no direct code
  linkage is evident"*). Citation discipline disciplined the confabulation rather than
  suppressing the reasoning.
- This is also the evidence for "larger models benefit more from XML" — the benefit was the
  *affordance* (mandatory citation) the XML structure enabled, and the model with the most
  reasoning to discipline gained most.

## Adopted (ADR-0016)

- comparer-prompt → `v2-contradiction-or-omission` (the dominant win)
- comparer-schema → `v2-xml`
- recap-prompt → `v2-xml-cite` (kills the hallucination class)
- The `<from>`-in-evidence check shipped as a per-recap **grounding score** in the post.

## Limits / residual

- K=3, one window, one fixture pair — directional.
- Comparer still occasionally mislabels emphasis as a divergence and sometimes echoes the
  schema's `shared claim` placeholder — residual quality the seven-night label corpus measures.

## Postscript — production caught what the experiment missed

Adopting v2 in ember, the **first live run's comparer XML failed to parse**: the recaps contain
"Threads & risks", and the 14B emits tags but not entity-escaping, so a bare `&` broke the
strict XElement parser — the *opposite* of "XML is more robust," because here the failure mode
is entity-escaping, not brace-counting (JSON would have tolerated it). Fixed with a pre-parse
bare-`&` sanitizer (`RecapXml.SanitizeForParse`); re-run produced the comparison cleanly.
Lesson: XML output on a local model needs entity-sanitization before a strict parser. Recorded
in ADR-0016.
