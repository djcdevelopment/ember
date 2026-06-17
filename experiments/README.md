# experiments/ — the reflect lab notebook (and how to read it)

This is the apparatus + record half of the experiment-corpus practice
(`D:\work\gad\pm\experiment-corpus-plan.md`). Running an experiment is ~free now that the
dual-B70 hardware is dialed in, so we over-run them and keep the longitudinal record.

## How to read this corpus (for a reconstructor — human or agent)

You have the repo and the code graph but not us. To rebuild *why* reflect is shaped the way it
is, walk the chain backward:

```
decision (docs/adr/00NN, "Tested-by: EXP-####")
  -> experiment (experiments/EXP-####/)
       spec.md     the question, the arms, what was held constant
       run_*.py    the harness (re-runnable against the facade)
       inputs/     the frozen evidence/fixtures (reproducibility)
       results/    every arm's raw output + summary.json
       verdict.md  findings (quant vs judgment, labelled) + what was adopted
  -> contracts (../contracts/<name>/vN-*.md)  the exact prompt/schema each arm used
```

The code's active prompts are pinned to their contract versions by
`tests/Ember.Tests/ContractDriftTests.cs`, so the contract files are not stale docs — they are
enforced to match what runs.

## Authoring a new experiment

1. `cp -r EXP-0001-comparer-format/ EXP-000N-<slug>/`, rewrite `spec.md`.
2. Freeze inputs; keep arms factorial (isolate one variable — EXP-0001's wording-vs-format
   split is the standard).
3. Run the harness; write `verdict.md` (label quant vs judgment).
4. If it changes the system: an ADR cites the EXP and adopts named contract versions; add a row
   to `pm/experiments-ledger.md`.

*(Next apparatus step, deferred: a generic `run_experiment.py` that takes arms as data, so the
harness isn't re-authored each time. EXP-0001's `run_ab.py` is the bespoke seed.)*

## Index

| EXP | Question | Verdict | Decision |
|---|---|---|---|
| [0001](EXP-0001-comparer-format/) | XML vs JSON for the comparer + recap grounding | Improved prompt dominant; XML-cite killed the recap hallucination | ADR-0016 |
