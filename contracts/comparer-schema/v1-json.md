---
contract: comparer-schema
version: v1-json
status: superseded
superseded-by: v2-xml (EXP-0001 / ADR-0016)
---
The original divergence-comparer output schema: a JSON object requested via JSON-object mode
with a parse-and-retry guard.

```
{ "agreements": ["..."], "divergences": [ { "topic": "...", "aSays": "...", "bSays": "..." } ] }
```

Superseded by v2-xml: JSON-object mode parsed fine at temp 0 / 500 tokens in EXP-0001 (no
robustness gap *there*), but the escaping overhead on prose values and the lack of a
reasoning-friendly shape made XML the cleaner choice — and the floor benefit returns at higher
temp / longer output / weaker models. Kept as a record.
