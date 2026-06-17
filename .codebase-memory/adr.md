## PURPOSE
ember is a single-operator dev tool. `/plan <brief> <repo>` runs a Claude(planner)/GPT(critic) loop, a resumable soft gate, then a headless Claude Code builder in a git worktree that opens a draft PR. Post-v1 it also hosts **Reflect**: a scheduled dual-judge recap of the constellation's committed work (the constellation-awareness vision's reflective layer). Not a product.

## STACK
.NET 9 Generic Host; Discord.Net (control surface); Microsoft.Extensions.AI `IChatClient` (one OpenAI-compatible adapter for every provider, selected by config — ADR 3); Microsoft.Data.Sqlite (sessions + recaps); OpenTelemetry→Jaeger. Local inference is the dual Intel Arc Pro B70 box via the vllama OpenAI facade (127.0.0.1:8090); the validated serving stack is native-Windows llama.cpp Vulkan (ADR 12), not vLLM. Subprocess seams (never libraries): git, the constellation-manifest CLI, the codebase-memory-mcp CLI, and the `claude` CLI.

## ARCHITECTURE
Planning: PlanningLoopRunner → GateService (resumable soft gate) → BuildQueue (FIFO throttle) → BuilderRunner → PullRequest. Reflect: EvidenceAssembler (git delta since per-repo baseline + code-graph symbol enrichment) → two RecapJudges on different cards → DivergenceComparer → ReflectExecutor (post + persist + journal). Round-1 plan context = manifest summary + code-graph architecture/symbols + the git-ls-files tree (the path-authority). State in one SQLite file; boot recovery fails interrupted work closed.

## PATTERNS
Soft-fail-to-null at every subprocess seam — a manifest/graph/git failure degrades context, never blocks the run. Protected-virtual process methods so seams are unit-tested with canned output. Autonomous features ship **disabled by default** (Reflect) with a read-only console taste first. Re-index the graph before reading it (the watcher is not trusted for freshness — ADR 15). Automated git writes are additive and by-name, never `git add .`. Structured-output parsing retries once then soft-fails.

## TRADEOFFS
Local recap quality vs cost: lean evidence (commit subjects + file lists, not diffs) suits a small grounded model; rich evidence would favor the larger model — a deliberate fork (EXP-0001). XML output grounds the recap (mandatory `<from>` citations kill cross-repo hallucination) but a local model doesn't entity-escape, so XML needs `&`-sanitization before a strict parser (ADR 16). System RAM (32GB) is the binding constraint on the judge tier until #99, so the lean llama.cpp path is mandatory over Ollama.

## PHILOSOPHY
Two judges, not one, because a single judge asserts its hallucinations with confidence — divergence between independent judges is the signal (proven night zero: it caught a fabricated dependency). Cite-or-don't-claim: a recap claim without an evidence citation should not be written. Decisions are tied to experiments and contracts are versioned and drift-enforced (the experiment-corpus practice; ADR 15/16) — the development process is itself a research corpus meant to be reconstructable by an outsider. Validations must pass *for real* (build → tests → live run), not just in unit tests — the live runs keep catching what the units miss. Never launch a model server unattended (the rig has gone to non-POST). History is part of the product; never rewrite it.
