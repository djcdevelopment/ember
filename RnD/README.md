# RnD — ember research notebook

Speculative research, not committed plans. Each report below was produced by a
dedicated research agent grounded in ember's actual architecture (`docs/adr/*`,
`src/Ember/**`) and tasked with surveying the **2025–2026** state of the art for
one layer of the stack.

- **Started:** 2026-05-17
- **Why now:** the Arc B70 hardware and the 4070 Ti rehearsal were both
  unavailable; instead of running the local-loop rehearsal (ADR 10) we explored
  avenues that the build itself opens up.
- **Method:** 5 parallel research agents (Sonnet for breadth/speed; Opus
  synthesis below). Each was given ember's design, the operator profile, and
  read access to the repo, then told to report findings, surprises, unique-fit
  observations, references, and recommendations.

## Streams

| # | File | Goal | Status |
|---|------|------|--------|
| 01 | [01-local-inference-landscape.md](01-local-inference-landscape.md) | Model + quant + serving stack for the planner/critic, on the 2×Arc B70 target and the 4070 Ti rehearsal | complete |
| 02 | [02-critic-loop-and-evaluation.md](02-critic-loop-and-evaluation.md) | Does the planner↔critic loop converge — and how do we measure a plan improving | complete |
| 03 | [03-headless-builder-frontier.md](03-headless-builder-frontier.md) | Headless coding-agent frontier: Agent SDK vs `claude -p`, pre-PR verification, fleets | complete |
| 04 | [04-agent-observability-and-evals.md](04-agent-observability-and-evals.md) | From per-stage traces to trace-driven evals and regression detection | complete |
| 05 | [05-discord-control-surface.md](05-discord-control-surface.md) | Discord as a richer ops/control surface for an agent system | complete |

## Synthesis

Five independent agents, one striking convergence: **the architecture holds up.**

### 1. Every ADR under review was independently validated
Streams 02, 03, and 05 each tested an existing decision against current
literature and confirmed it — without coordination:
- **ADR 5** (asymmetric author/critic loop) — stream 02 calls it "the single
  most well-validated decision in the codebase": Huang et al. (ICLR 2024) show
  intrinsic self-correction *fails* on reasoning tasks, while CriticGPT shows a
  *separate* critic model beats humans 63% of the time. Do not consolidate.
- **ADR 4** (builder = headless `claude -p`, not the Agent SDK) — confirmed:
  the Agent SDK has no C# binding; Anthropic now explicitly endorses `claude -p`
  for scripted contexts.
- **ADR 1** (Discord as the control surface) — confirmed: thread-per-task and
  native interactive components beat Slack/Telegram for this operator.

This is a real result, not a to-do list: the bones are sound.

### 2. The recurring gap — trusting an LLM's self-report — appears at two layers
The single highest-value cross-stream theme:
- **Critic layer** (02): the critic can *hallucinate* issues. Fix: require an
  `Evidence` quote from the plan for every issue raised.
- **Builder layer** (03): `SucceedAsync` trusts the builder's claim that it
  succeeded. Fix: run an external `dotnet build` (a `VerifyCommand`) before
  `PullRequest.OpenAsync`.
Same failure mode — unverified self-assessment — at two stages. Closing both is
the highest-leverage work in the whole notebook.

### 3. A cluster of sub-one-day wins
Cheap, independent, low-risk: `.UseOpenTelemetry()` one-liner on the chat
clients (04, unlocks token + cost telemetry for free) · `--max-turns` and
`--bare` on the builder (03) · `--permission-mode auto` instead of
`--dangerously-skip-permissions` (03) · button gate + `<t:>` live countdown
(05) · `MaxPlanRounds` 6→4 and prior-round issue history to the planner (02).

### 4. The strategic gap — ember can run the loop but cannot measure it
Streams 02 and 04 converge independently on the same missing layer: a
**golden-set replay runner** (≈20 `brief` + `rubric` cases) plus a rubric
judge, so a prompt edit or model swap is caught when it regresses quality,
latency, or cost. Today nothing would catch that.

### 5. Recommended order of attack
1. **Quick wins** — the builder flags and the `.UseOpenTelemetry()` one-liner.
   Hours of work, immediate safety and visibility.
2. **Close the self-report gap** — `Evidence` field on critic issues; external
   `VerifyCommand` build gate before the PR.
3. **Build the measurement layer** — golden-set runner + rubric judge. This is
   what turns every later change into a measured change.
4. **Park the inference specifics (01)** until the Arc hardware lands — but note
   stream 01's two landmines now: vLLM issue #18819 (Qwen3 + `enable_thinking=
   False` + `guided_json` emits malformed JSON) and AWQ being broken on Arc XPU.

The full reports carry the references and the detail behind each point.

## Rehearsal log

Beyond the research streams, the local planning loop was actually run — on
Ollama/CUDA, four times — fixing two bottlenecks and building a tool along the
way. See [06-ollama-rehearsal-runs.md](06-ollama-rehearsal-runs.md).

The first run on the Arc hardware — 2026-06-05, dual-endpoint on the two
B70s, and the 32 GB system-RAM ceiling it found — is backfilled as a pointer
entry in [07-arc-dual-card.md](07-arc-dual-card.md).
