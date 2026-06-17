---
contract: comparer-schema
version: v2-xml
status: active
introduced-by: EXP-0001 / ADR-0016
supersedes: v1-json
---
The divergence comparer's output schema. XML, parsed by `DivergenceComparer.TryParse` into
`ComparisonResult { Agreements: string[], Divergences: { Topic, Kind, ASays, BSays }[] }`.

```
<comparison>
  <agreements><item>...</item></agreements>
  <divergences>
    <divergence>
      <topic>...</topic>
      <kind>contradiction|omission</kind>
      <a>...</a>
      <b>...</b>
    </divergence>
  </divergences>
</comparison>
```

Why XML over the v1 JSON (EXP-0001): the comparer's values are prose full of quotes,
backslash paths, and markdown — every one a JSON escape hazard. XML frees that attention
budget for the discrimination task, runs ~30% faster on the 14B, and shed one more false
positive than improved-JSON. `kind` is the new field that lets the post mark contradiction
vs omission. Parse stays soft (retry once, then null -> recap posts without the comparison).
