# Ollama local-loop rehearsal — runs 01–04

The ADR-10 loop rehearsal, run on **Ollama / CUDA** on the 4070 Ti instead of
vLLM (see ADR 10's update note). Goal: exercise the planner↔critic loop on small
local models and find where it breaks. It broke in three distinct places — each
run isolated one.

## Setup

- Path: `dotnet run -- plan "<brief>" ember` — the console runner (`PlanCli`):
  no Discord, no gate, no build, no API keys.
- Brief (fixed across all runs): add a build-verify gate to the headless builder
  — research stream 03's top recommendation, a real ember change.
- Repo: ember itself.

## Run log

### Run 01 — planner qwen3:8b, critic llama3.2 (3B)
Round 1, critic **approved** — but 5 of the 6 file paths in the plan did not
exist (`EmberOptions.cs`, `Builders/HeadlessBuilder.cs`, …). Cause: `RepoContext`
gave the planner only a README excerpt + the *top-level* directory listing — no
file tree — so the planner invented the layout, and the critic, given no repo
context at all, could not catch it. A false approval.

**Fix:** `RepoContext.Gather` now appends the `git ls-files` tracked-file tree;
`Critic.ReviewAsync` now receives repo context and treats the file list as
ground truth.

### Run 02 — same models, after the fix
Three rounds, **stalled**. Every existing file path now correct — the fix worked,
and it is model-agnostic (an 8B planner grounds fine once it can see the tree).
New bottleneck: the 3B critic. It engaged but produced low-information issues
that degraded to bare noun-phrases by round 3 ("Invalid Configuration
Validation") and missed concrete bugs (a non-existent `Process.Exists` API). The
loop stalled on critic noise.

### Run 03 — planner qwen3:8b, critic gpt-oss:20b
Three rounds, **stalled**. A strong critic is transformative: gpt-oss raised 7
specific, grounded issues in round 1 — real compile errors, undefined symbols.
But round 2 nearly converged (1 issue) and round 3 *regressed* (4), the planner
emitting a corrupted file path (`src/Ember/Disc,`). Unclear: was the regression
the 8B planner, or VRAM contention from co-running a 13.6 GB model with the
planner on a 12 GB card?

### Run 04 — same as 03, through the swap proxy
Three rounds, **stalled**. `tools/OllamaSwapProxy` enforced sole residency and
logged each load:

| Model | Cold-load | Residency |
|---|---|---|
| `qwen3:8b` | 5–27 s | 7.3 GB — 100% GPU |
| `gpt-oss:20b` | 13–16 s | 13.6 GB — 67% GPU (4.5 GB on CPU) |

`qwen3:8b` ran with the whole card to itself, 100% on GPU — a clean,
deterministic environment — and the round-3 regression **reproduced** (round 2:
3 issues → round 3: 5). The environment is ruled out: the regression is the 8B
planner's own quality ceiling.

## Findings

1. **Wiring (ADR 3) holds.** Ollama, the `ollama` provider path, the
   OpenAI-compatible adapter, the console runner, the JSON-mode critic — all
   worked with no code change. JSON parsing is reliable even at 3B.
2. **The loop needs repo ground truth.** Both planner and critic must see the
   file tree, or the planner invents paths and the critic cannot check them.
   Fixed this session.
3. **Critic capability is a large lever.** llama3.2 (3B) → vague noun-phrases;
   gpt-oss:20b → specific, grounded, catches real compile errors.
4. **qwen3:8b is the planner ceiling.** It cannot hold quality across a third
   round of accumulating revision — corrupted output, reintroduced bugs —
   confirmed across runs 03 and 04 with the environment controlled.
5. **Latency is environmental, not model.** gpt-oss:20b at 67% GPU ran ~180–230 s
   per call; on the Arc box (co-resident, 100% GPU) that disappears.

## Built this session

- `RepoContext.Gather` — tracked-file tree via `git ls-files`.
- `Critic.ReviewAsync` — repo context + ground-truth file-path checking.
- `ChatClientFactory` — per-model `RequestTimeoutSeconds`.
- `tools/OllamaSwapProxy` — sole-residency model swapper with residency logging.

## Next

A stronger planner. The swap proxy makes it cheap to try one — sole residency
means a ~14B runs nearly fully on-GPU. The open question no run has answered:
with a planner that does not regress, does the loop finally reach a true
`approved`?
