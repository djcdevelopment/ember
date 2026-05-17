# ember — local-loop rehearsal retrospective

_Written 2026-05-17, after the RnD research, the four-run Ollama rehearsal, the
swap proxy, and the cheap-wins hardening pass._

## What this chapter was

The build retrospective (`retrospective.md`) closed with Phases 0–3 done and the
project paused, waiting on Intel Arc B70 GPUs to stand up local inference.
Rather than wait, this chapter rehearsed the planner↔critic loop on the local
models already on the host — exercising the loop ember-side (ADR 10's first
goal) ahead of the hardware.

It produced more than a rehearsal: a research notebook, two loop fixes, a new
tool, and a builder-hardening pass.

## What was done

| Work | Where |
|---|---|
| Five research streams — inference, the loop, the builder, observability, Discord | `RnD/01`–`RnD/05` |
| Four-run rehearsal of the loop on Ollama | `RnD/06`, commit `7606bff` |
| Fix: `RepoContext` feeds the planner the real file tree; the critic gets repo context | `7606bff` |
| `OllamaSwapProxy` — sole-residency model swapper, with a JSONL run-log and nvidia-smi snapshots | `7606bff`, `ca93967` |
| Builder hardening — `--bare`, `--max-budget-usd`; planner/critic OpenTelemetry | `e9e6533`, `af55ab9` |

## What went well

- **One variable per run.** Each rehearsal run changed one thing and isolated
  one finding — the repo-context gap, then critic capability, then the planner
  ceiling. No run's result was confounded by the previous run's change.
- **The research validated the architecture.** Three of the five streams
  independently confirmed an existing decision against current literature —
  ADR 5 (separate critic), ADR 4 (CLI builder), ADR 1 (Discord). The bones held.
- **ADR 3 paid for itself.** The entire Ollama rehearsal — three model pairings
  and an inserted swap proxy — was *config only*. The loop's model access never
  changed: one OpenAI-compatible `IChatClient`, endpoint by config.
- **The swap proxy earned its keep on its first instrumented call** — the
  nvidia-smi snapshot showed the GPU at 11/12 GB used by other work, turning
  "is it the model or the box?" from a guess into a logged number.

## What the rehearsal found

- **The loop was blind to repo ground truth.** `RepoContext` gave the planner
  only a top-level directory listing, so it invented file paths; the critic got
  no repo context at all and could not catch them. Fixed — both ends now see the
  `git ls-files` tree.
- **Critic capability is a large lever.** A 3B critic produced vague
  noun-phrases and missed real compile errors; a 20B critic produced specific,
  grounded issues and caught invented APIs.
- **`qwen3:8b` is the planner-quality ceiling.** It regresses on the third round
  of accumulating revision — corrupted output, reintroduced bugs — and the swap
  proxy proved this reproduces with the GPU environment ruled out.

## Decisions made

- **Rehearse on Ollama, not vLLM** (ADR 10 update). Ollama loads on demand and
  releases the GPU when idle; two pinned vLLM processes would not. That made an
  immediate run possible on a contended card — at the cost of not rehearsing the
  vLLM server, which is deferred to the Arc box.
- **A standalone swap proxy, not in-ember logic.** Keeping residency control in
  a separate process in front of Ollama left ember's provider-agnostic core
  (ADR 3) untouched.
- **Kept `--dangerously-skip-permissions`** for the builder. Research suggested
  `--permission-mode auto`; but the builder is headless with no human to answer
  a denial, runs in an isolated worktree, and only ever opens a draft PR — a
  mode that can deny mid-build would just stall it.

## What is NOT verified

- **`qwen3:14b` as a stronger planner** — the obvious next experiment, blocked
  while the GPU is occupied by other work on the card.
- **The builder hardening** (`--bare`, `--max-budget-usd`) — build-verified, not
  run; the builder needs `ANTHROPIC_API_KEY` and an authenticated `gh`.
- **The vLLM server path** — deferred to the Arc hardware (ADR 10).
- **A live Discord `/plan` → build → PR** — still never run end to end (carried
  over from the build retrospective).

## Lessons

- **Verify generated claims against reality.** A research agent recommended
  `--max-turns`, a flag Claude Code does not have; a small planner invented an
  entire file layout. `claude --help` and `git ls-files` are cheap; trusting the
  generated text is not. This is the same failure mode the loop's own critic
  exists to catch.
- **Instrument the environment, not just the program.** The loop's residency
  numbers were ambiguous until the proxy logged `nvidia-smi` alongside Ollama's
  own accounting. A measurement that cannot separate a model fault from an
  environment fault is not yet a measurement.
- **De-risk by isolating, not by waiting.** ADR 10's instinct — split the
  first-contact event in two — is what let a paused project make four runs of
  real progress with no new hardware.

## Next

- `qwen3:14b` as planner when the GPU frees — does a stronger planner stop the
  round-3 regression and reach a true `approved`?
- The build-verify gate (RnD stream 03) — GPU-free, implementable now, and it
  closes the "unverified self-report" gap the research flagged across streams.
- The vLLM / Arc path when the B70s arrive (ADR 9, ADR 10).
