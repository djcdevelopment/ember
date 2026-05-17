# Local inference landscape

## Goal

Recommend a concrete model + quantization + serving stack for ember's planner and critic across two hardware targets:

- **(a) Production:** 2× Intel Arc Pro B70, 32 GB GDDR6 each (64 GB total, 608 GB/s aggregate bandwidth), vLLM LLM-Scaler XPU, WSL2/Docker
- **(b) Rehearsal:** 1× RTX 4070 Ti, 12 GB GDDR6X, vLLM CUDA, WSL2

Ember's loop is strictly sequential (planner then critic, never concurrent), so co-residency is a VRAM budget question only. The critic must emit a fixed JSON schema reliably. `MaxPlanRounds = 6` means round 6 carries the full accumulated plan plus five prior critiques — the maximum context is the binding constraint, not round 1.

---

## Executive summary

- **Qwen3-8B-Instruct** is the clear pick for the rehearsal planner. At AWQ INT4 it fits in ~5.5 GB, leaving ~6 GB for the critic. Qwen3 leads all open-weight families on instruction following (IFBench 76.5, above GPT-5.2) and is purpose-built for structured authoring tasks. Its MTP head enables native speculative decoding in vLLM at no extra model-weight cost.
- **Qwen3-4B-Instruct** is the critic for the rehearsal. It fits in ~2.8 GB AWQ, leaving comfortable KV headroom. With xgrammar guided decoding the JSON constraint is hardware-enforced, bypassing reliability concerns about unconstrained small models.
- **There is a known vLLM bug (issue #18819)** with Qwen3 + `enable_thinking=False` + guided JSON: the output can be malformed. The workaround is to keep `enable_thinking=True` with `/no_think` appended to the critic prompt, or to disable the reasoning parser entirely. This must be handled in `Critic.cs` before the rehearsal is valid.
- **For production (2× B70)**, Qwen3.6-27B (dense, BF16 or FP8) is the planner — it fits in one B70 32 GB with room to spare and vLLM LLM-Scaler has explicit support for it. Tensor-parallel serving across both B70s is confirmed at ~140 tok/s for 27B-class models under 8-concurrent load. Qwen3-14B is the critic, pinned to the second card.
- **AWQ INT4 pre-quantized models do not work on the XPU stack as of early 2026** (torchao CUDA dependency, LLM-Scaler issue #269). Use online `sym_int4` or AutoRound W4A16 on Arc; reserve AWQ-Marlin for the CUDA rehearsal.
- **The single biggest Arc risk** is XPU kernel maturity: vLLM delivers only ~13 tok/s on Qwen3-27B single-card (vs 22+ tok/s with SYCL llama.cpp). This is adequate for ember (one request at a time, latency budget of minutes per round) but represents a real gap versus CUDA. The workaround is the rehearsal-first strategy already encoded in ADR 10.

---

## Findings

### 1. Model families — state of the art (late-2025 / 2026)

#### Qwen3 / Qwen3.5 / Qwen3.6 (Alibaba)

The Qwen3 family (released April 2025) dominates open-weight instruction-following benchmarks as of mid-2026. Key facts:

- **Architecture:** every dense size (0.6B, 1.7B, 4B, 8B, 14B, 32B) uses the same hybrid attention + MTP design; MoE variants (30B-A3B, 235B-A22B) use grouped-expert routing.
- **Instruction following:** Qwen3 series scores 76.5 on IFBench (Qwen3.5 version), surpassing GPT-5.2 (75.4) and significantly above Claude Sonnet variants (58.0). This is the key signal for ember's structured authoring.
- **Thinking / non-thinking modes:** all instruct variants support `enable_thinking` toggle. Thinking mode is better for multi-step planning; non-thinking saves tokens for the critic.
- **Benchmark inflection:** Qwen3-8B matches or exceeds Qwen2.5-14B on most benchmarks; Qwen3-4B matches Qwen2.5-7B. The effective size class is ~1 generation ahead of parameter count.
- **Qwen3.5 (Feb 2026):** flagship 397B-A17B MoE; Qwen3.5-27B is the dense mid-range.
- **Qwen3.6 (Apr 2026):** 35B-A3B MoE + 27B dense; Qwen3.6-27B fits one B70 32 GB at Q4 (15.65 GiB).
- **Fine-tuning leaderboard (May 2026):** Qwen3-4B-Instruct-2507 ranks #1 and Qwen3-8B ranks #2 among all fine-tuned base models tested.

**Verdict for ember planner:** Qwen3-8B-Instruct (rehearsal), Qwen3.6-27B-Instruct (production). Both optimized for long structured authoring and have first-class vLLM support.

**Verdict for ember critic:** Qwen3-4B-Instruct (rehearsal), Qwen3-14B-Instruct (production). The critic JSON schema is small; the constraint is reliable emission, not quality headroom. Guided decoding offloads reliability from the model to the engine.

#### Other families worth tracking

| Family | Status | Notes for ember |
|--------|--------|-----------------|
| **Mistral Small 3.2 (24B)** | Strong JSON / function-calling niche | ~55 GB BF16; too large for 12 GB even quantized; viable on B70 but no advantage over Qwen3-27B |
| **Gemma 4 (9B, 27B)** | April 2026; hybrid sliding-window attention → very small KV cache; strong structured output | No GGUF Q4 advantage over Qwen3-8B for dense tasks; XPU support landed May 2026 |
| **Phi-4 Mini (3.8B)** | Strong STEM/math; good JSON | Less instruction-following depth than Qwen3-4B for planning prose; worth testing as critic fallback |
| **DeepSeek-R1 distills (8B, 14B)** | Reasoning-focused | Thinking overhead wasteful for critic's short JSON verdict; heavy chain-of-thought eats tokens at round 6 |
| **LLaMA 3.1 8B** | Solid baseline | IFBench lower than Qwen3-8B; no thinking mode; inferior for planning prose |

#### MoE vs dense for ember

Ember's loop is single-request, sequential. MoE speed advantage appears under batch/concurrent load (many experts fire in parallel across requests). For single-user sequential inference:
- Dense 27B beats MoE 35B-A3B on *single-request* latency on Arc (20.6 tok/s vs 38.9 tok/s — but MoE VRAM is nearly the same on B70 so the 35B-A3B is not meaningfully faster at batch=1).
- ADR 9's recommendation of dense ~27B class is correct.

---

### 2. Quantization — vLLM specifics

#### Format hierarchy for CUDA (rehearsal)

| Format | VRAM vs BF16 | Quality delta (MMLU-Pro) | vLLM throughput | Notes |
|--------|-------------|--------------------------|-----------------|-------|
| BF16 | 1× | baseline | baseline | Too large for 12 GB at 8B+ |
| FP8 (online) | 0.5× | −0.4 pts | +1.4–1.7× | Requires Hopper (H100) or Ada (RTX 40-series) for hardware FP8; RTX 4070 Ti supports it |
| AWQ INT4 (Marlin) | 0.25× | −1.6 pts | +2.6–3.1× (Marlin kernel) | **Best choice for 4070 Ti rehearsal.** Marlin-AWQ is fastest throughput at 4-bit; pre-quantized Qwen3 AWQ models available on HuggingFace |
| GPTQ INT4 | 0.25× | −1.9 pts | Slightly below AWQ-Marlin | Inferior to AWQ on both quality and speed; skip |
| GGUF (Q4_K_M) | ~0.25× | similar to AWQ | Not supported by vLLM | Use only with llama.cpp; not applicable to ember's vLLM stack |

**Recommendation for rehearsal (CUDA):** Qwen3-8B-AWQ planner + Qwen3-4B-AWQ critic. Both have official pre-quantized checkpoints from Alibaba (`Qwen/Qwen3-8B-AWQ`, `Qwen/Qwen3-4B-AWQ`). vLLM serves them with `awq_marlin` backend automatically.

#### Format hierarchy for XPU (production Arc B70)

The XPU stack has a different quantization landscape:

| Format | Status on XPU | Recommendation |
|--------|--------------|----------------|
| BF16 | Fully supported | Use for planner if it fits (27B = ~54 GB; one B70 only has 32 GB — does not fit BF16) |
| FP8 (online, dynamic) | Supported via `--quantization fp8`; E5M2 default | Use FP8 for 27B on single B70 (reduces to ~27 GB, fits) |
| sym_int4 (online) | Supported via `--quantization sym_int4` | Use if FP8 still too tight; quality penalty ~same as AWQ |
| AWQ (pre-quantized, torchao path) | **Broken on XPU** — CUDA hard-dependency in torchao (issue #269, open as of May 2026) | Do NOT use pre-quantized HuggingFace AWQ models on Arc |
| AutoRound W4A16 | Supported on XPU; Intel's recommended INT4 path; `group_size=128` | Use AutoRound-quantized checkpoints when torchao-AWQ is blocked |
| MXFP4 | Only for specific Intel-internal models (gpt-oss-20b, 120B); not for Qwen3 | Not applicable |

**Recommendation for production (XPU):** Qwen3.6-27B with online FP8 (`--quantization fp8`) for the planner, or AutoRound W4A16 if available. Qwen3-14B in BF16 fits one 32 GB B70 comfortably (~28 GB).

#### FP8 on RTX 4070 Ti (rehearsal)

The RTX 40-series (Ada Lovelace) does support FP8 hardware acceleration. FP8 is not as advantageous as on Hopper (no transformer engine), but it is valid. For the 12 GB rehearsal, FP8 saves too little VRAM to change which models fit — AWQ INT4 remains the correct rehearsal quant.

---

### 3. Intel Arc — maturity of the XPU stack

#### LLM-Scaler status (May 2026)

- Intel released `intel/llm-scaler-vllm:0.14.0-b8.2.1` in May 2026 with **official Arc Pro B70 support**.
- The image bundles vLLM 0.14.0 + PyTorch 2.10 + oneAPI + xe driver support.
- Tensor parallelism (`-tp=2`) across two B70s is working: community benchmarks confirm ~140 tok/s for 14B-class models at 8-concurrent; ~13 tok/s for 27B-class single-user.
- `--enforce-eager` is required (no CUDA graphs on XPU).
- Inter-GPU communication requires `--privileged` Docker container for oneCCL.

#### Known sharp edges

1. **NDEBUG bug (fixed in later builds):** Early B70 benchmarks understated prefill by 50% due to missing `-DNDEBUG` compiler flags. Verify current LLM-Scaler image — v0.14.0-b8.2 should have this patched.
2. **AWQ pre-quantized models broken:** torchao dependency, CUDA assertion on XPU. Use online `sym_int4` or AutoRound instead.
3. **`--enforce-eager` mandatory:** disables CUDA graph optimization; small throughput penalty vs CUDA.
4. **Row-split dual-GPU mode crashes** in llama.cpp upstream (not in vLLM TP mode, but relevant if ever testing llama.cpp on Arc).
5. **Kernel 6.17+** and compute-runtime v26.09+ required for xe driver recognition on Ubuntu bare metal.
6. **`UR_L0_ENABLE_RELAXED_ALLOCATION_LIMITS=1`** environment variable essential for large model VRAM allocation.
7. **Module import path:** must run scripts from `/llm` root inside the container, not `/llm/vllm`.
8. **Qwen3.5 MoE gap (partially resolved):** As of March 2026 (b8.1) the 35B-A3B MoE was added. Confirm Qwen3.6-35B-A3B status before relying on it.

#### B70 bandwidth reality

The B70's 608 GB/s (per card) matches the RTX 3090 and substantially exceeds the RTX 4070 Ti (504 GB/s). Decode for 27B-class models is memory-bandwidth bound, so the B70 is roughly at parity with a 3090 per card. Two cards in TP=2 double effective bandwidth for the TP-sharded weight reads.

Community data: single B70, vLLM, Qwen3.5-27B Q4 → **13.43 tok/s**. With llama.cpp SYCL → **22.47 tok/s**. The vLLM XPU gap vs SYCL llama.cpp is real (~40% slower at single-user decode). This is a kernel maturity issue expected to narrow in subsequent releases.

For ember's use case (one request at a time, ~minutes per planning round), 13 tok/s for the planner is fine — a 2,000-token plan draft takes ~2.5 minutes at worst. The critic at 14B produces its ~200-token JSON verdict in under 20 seconds.

#### Dense vs MoE on Arc

- Dense 27B: best supported path on XPU; consistent kernel coverage.
- MoE 35B-A3B: faster per-token decode under batch load; expert routing adds XPU kernel complexity. MoE support on XPU is newer and has had gaps (e.g., Qwen3.5 MoE required a specific b8.1 fix). Prefer dense for production until MoE XPU kernels have more soak time.

---

### 4. Structured / guided decoding — JSON-mode reliability

#### vLLM's guided decoding backends

vLLM supports three backends: **xgrammar** (default, recommended), **outlines**, and **lm-format-enforcer**. xgrammar is the best current choice:
- Lowest time-per-output-token for repeated schemas (caching).
- Handles most JSON schemas without compilation failures.
- LLGuidance is faster on cold-start but has higher compilation failure rates on complex schemas.

Ember's critic schema is simple (3 fields, array of objects with 3 fields each) — xgrammar handles it trivially.

#### Critical Qwen3 bug with guided JSON + non-thinking mode

**vLLM issue #18819 (filed May 2025, still open as of this writing):** When `enable_thinking=False` is set AND `guided_json` is specified in the same request to a Qwen3 model, the output is frequently malformed — extra brackets, triple-backticks, or gibberish.

**Root cause:** The Qwen3 reasoning parser preprocessing interferes with the guided decoding token processor when thinking is disabled.

**Working configurations:**
- `enable_thinking=True` + `guided_json` → valid JSON (but wastes tokens on `<think>...</think>` output that ember must strip)
- Reasoning parser disabled entirely (`--reasoning-parser` omitted) + `guided_json` → valid JSON (loses thinking capability)
- `enable_thinking=True` + `/no_think` appended to user prompt + `guided_json` → valid JSON, thinking suppressed in output

**Recommended mitigation for ember:** Configure the critic vLLM instance with `--reasoning-parser qwen3 --default-chat-template-kwargs '{"enable_thinking": false}'` disabled (i.e., no reasoning parser flag at all), and use xgrammar guided decoding. This gives clean JSON without reasoning overhead. If reasoning quality is needed for the critic later, switch to the `/no_think` approach.

The existing `parse-and-retry` backstop in `Critic.cs` remains valid, but the target with guided decoding should be a retry rate near zero. If retries are high, it is a diagnostic signal (likely the bug above, or a model-size issue).

#### Does a 3–4B model reliably emit valid JSON with xgrammar?

With guided decoding (xgrammar), validity is **mechanically guaranteed** — the engine constrains the token logit distribution to always produce a valid JSON prefix at every step. The model cannot produce invalid JSON. What a small model *can* do is produce semantically shallow or repetitive content within the valid schema. For ember's critic, the schema is small enough that Qwen3-4B with guided decoding will produce structurally valid verdicts; the question is critique depth, which improves with size.

---

### 5. Co-residency and VRAM budgeting

#### Rehearsal: 4070 Ti 12 GB, two vLLM processes

vLLM allocates VRAM at startup based on `--gpu-memory-utilization` and `--max-model-len`. Two co-resident processes share the physical device but each reserves independently. The loop is sequential — only one process is active at any moment — so compute is not contended, only VRAM.

**VRAM budget for rehearsal:**

| Component | VRAM |
|-----------|------|
| Qwen3-8B-AWQ model weights | ~5.0 GB |
| Qwen3-8B KV cache at `--max-model-len 16384`, `--gpu-memory-utilization 0.45` | ~1.5 GB |
| vLLM process overhead (activations, CUDA context) | ~0.5 GB |
| **Planner subtotal** | **~7.0 GB** |
| Qwen3-4B-AWQ model weights | ~2.8 GB |
| Qwen3-4B KV cache at `--max-model-len 16384`, `--gpu-memory-utilization 0.35` | ~0.9 GB |
| vLLM process overhead | ~0.4 GB |
| **Critic subtotal** | **~4.1 GB** |
| **Total** | **~11.1 GB** |

This leaves ~0.9 GB slack on a 12 GB card. It is tight. Mitigations:
- Set planner `--max-model-len 12288` (round 6 max context ≈ 6× one round; at ~1,500 tokens/round for plan + critique = ~9,000 tokens — fits in 12K with headroom).
- Enable FP8 KV cache (`--kv-cache-dtype fp8`) to halve KV cache VRAM at modest quality cost.
- If OOM occurs, drop planner `--gpu-memory-utilization` to 0.42 and critic to 0.32.

**KV cache sizing for MaxPlanRounds = 6:**

Round 6 carries: accumulated plan (grows each round, ~2,000–4,000 tokens at peak) + 5 prior critiques (~200 tokens each = ~1,000 tokens) + system prompt (~300 tokens) + current user turn (~100 tokens). Conservatively: **~5,500 tokens** for the planner at round 6. A `--max-model-len 8192` is technically sufficient but gives no room for larger plans. Use **12288** (12K) as the safe floor for both processes.

#### Production: 2× B70, one model per card

With 32 GB per card and two cards:
- Planner card: Qwen3.6-27B FP8 (~27 GB) + KV cache at 32K context = ~3 GB headroom. Pin with `--gpu-memory-utilization 0.92` and `--max-model-len 32768`.
- Critic card: Qwen3-14B BF16 (~28 GB) + KV cache at 32K = fits with TP=1 on its own card.
- No tensor parallelism needed for the two-model case — TP=2 is only needed for 70B+ dense models on these cards. Running each model pinned to its own card via `CUDA_VISIBLE_DEVICES` (or XPU equivalent) is cleaner and avoids TP overhead.

---

## Surprising / novel

1. **Qwen3 instruction-following dominance is wider than expected.** Scoring above GPT-5.2 on IFBench is a strong signal. The "parameter-class generational lead" (Qwen3-8B ≈ Qwen2.5-14B) means ember can run a meaningful planner at 8B rather than needing 14B.

2. **AWQ pre-quantized models are silently broken on XPU.** The failure mode is not obvious — the container accepts the model, fails internally with a CUDA assertion. Someone following the LLM-Scaler docs for Arc without reading GitHub issues would hit this opaquely. The workaround (online `sym_int4` or AutoRound) works but requires deliberately-chosen model checkpoints, not just downloading AWQ variants from HuggingFace.

3. **Qwen3 + guided JSON + `enable_thinking=False` produces garbage.** This is a vLLM-side bug in the reasoning-parser/guided-decoding interaction, not a model deficiency. It affects the exact configuration (non-thinking critic + JSON schema) that ember wants. It has a clean workaround but must be explicitly applied. The existing `Critic.cs` retry backstop is the right safety net, but first-contact retry rates will be high unless the workaround is in place.

4. **The NDEBUG performance bug** means early community B70 benchmarks are unreliable. Actual prefill performance is 51–180% higher than initially reported. The decode-bound numbers (~13 tok/s vLLM, ~22 tok/s SYCL) are unaffected (decode is not compute-bound).

5. **vLLM on XPU is 40% slower than SYCL llama.cpp at single-user decode** on the B70. For a batch-serving use case this would be significant. For ember (single operator, sequential requests) it is irrelevant — but it means benchmarks from llama.cpp community tests do not translate to the vLLM serving numbers ember will see.

6. **AutoRound W4A16 is Intel's preferred INT4 path** for XPU (not AWQ). Pre-quantized Qwen3-8B AutoRound checkpoints exist on HuggingFace (e.g., `Qwen3-8B-W4A16-G128-AutoRound`). This is a first-class Intel-supported path, not a workaround.

---

## Where this uniquely aligns with ember

**ADR 9's "dense ~27B class" instinct was correct.** Qwen3.6-27B exists, is explicitly supported by LLM-Scaler, fits a single B70 at FP8, and is the community's tested model on that hardware. The ADR was written before Qwen3.6 shipped; the hardware and model are now matched.

**ADR 10's sequential-loop guarantee is the key enabler for co-residency.** Because the planner and critic never run concurrently, two vLLM processes on one GPU is purely a VRAM arithmetic problem, not a scheduling problem. The `PlanningLoopRunner` architecture enforces this. The budget above shows it fits on 12 GB if `--max-model-len` is sized to the actual loop maximum (~12K), not the model's training maximum (32K–128K).

**ADR 3's config-only provider swap** means the quantization change (AWQ on rehearsal → sym_int4/AutoRound on Arc) requires no code changes. Only the `BaseUrl`, `ModelId`, and `--quantization` flag in the Docker command change. The rehearsal is genuinely rehearsing the same ember wiring.

**The `Critic.cs` parse-and-retry is correctly designed** but will be noisy on first contact if the Qwen3 thinking-parser / guided-JSON bug is not mitigated. The retry rate will be a live diagnostic of whether the workaround was correctly applied. This is exactly the kind of "retry rate as signal" dynamic ADR 10 anticipated.

**`MaxPlanRounds = 6` makes the context budget specific and manageable.** Round 6's worst-case context (planner sees 5 critiques + growing plan) is ~5,500–7,000 tokens. `--max-model-len 12288` covers this with meaningful margin. vLLM pre-allocates KV cache for the stated `max-model-len`; setting it to 128K on a 12 GB card would blow the VRAM budget before a single request fires.

**The XPU rehearsal-decoupling strategy (ADR 10) is validated by the NDEBUG bug and AWQ breakage.** Both of these would have been hit as part of "first contact" with the B70. Running the loop on CUDA first means the loop's JSON-mode and prompt-portability issues will already be understood before encountering Arc-specific quantization failures. This is the correct risk-decomposition.

---

## Recommendations

### Priority order

#### 1. Rehearsal stack (RTX 4070 Ti 12 GB, CUDA)

**Planner:** `Qwen/Qwen3-8B-AWQ` via `vllm serve` with:
```
--quantization awq_marlin
--max-model-len 12288
--gpu-memory-utilization 0.45
--reasoning-parser qwen3
--default-chat-template-kwargs '{"enable_thinking": true}'
```

**Critic:** `Qwen/Qwen3-4B-AWQ` via `vllm serve` with:
```
--quantization awq_marlin
--max-model-len 12288
--gpu-memory-utilization 0.35
# No --reasoning-parser: avoids issue #18819
# Use xgrammar guided decoding (vLLM default backend)
```

**Context:** `--max-model-len 12288` on both. Enable FP8 KV cache (`--kv-cache-dtype fp8`) if VRAM pressure causes OOM. Do not use the model's native 32K context window — it will bloat the KV allocation past available VRAM.

**Ports:** planner `:8000`, critic `:8001` — as specified in ADR 10.

**Critic workaround for guided JSON:** Do not pass `--reasoning-parser` to the critic instance. Call with `response_format={"type":"json_object"}` or `guided_json` schema. Without the reasoning parser, `enable_thinking` has no effect on the guided decoding pipeline and issue #18819 is not triggered.

#### 2. Production stack (2× Arc Pro B70, XPU LLM-Scaler)

**Planner:** `Qwen/Qwen3.6-27B` (or the FP8 checkpoint `Qwen/Qwen3.6-27B-FP8`) pinned to GPU 0:
```
--quantization fp8
--max-model-len 32768
--gpu-memory-utilization 0.88
--enforce-eager
--reasoning-parser qwen3
```
Set `UR_L0_ENABLE_RELAXED_ALLOCATION_LIMITS=1` in Docker environment.

**Critic:** `Qwen/Qwen3-14B` (BF16, fits 32 GB) pinned to GPU 1:
```
--max-model-len 32768
--gpu-memory-utilization 0.88
--enforce-eager
# No reasoning parser (same critic isolation as rehearsal)
```

**Image:** `intel/llm-scaler-vllm:0.14.0-b8.2.1` (May 2026 release with B70 support).

**Do NOT use:** Pre-quantized AWQ checkpoints from HuggingFace on the XPU stack (torchao CUDA dependency, issue #269). Use online `--quantization fp8` or `--quantization sym_int4`.

**Fallback if FP8 on 27B is too large:** Use AutoRound W4A16 checkpoint (e.g., `Qwen3.6-27B-W4A16-G128-AutoRound` — may need to be produced with `intel/auto-round` if not pre-published).

#### 3. Single biggest risk

**The Qwen3 + guided JSON + thinking-parser interaction bug (vLLM #18819) on the critic.** If the critic vLLM instance is started with `--reasoning-parser qwen3` and the ember code passes `enable_thinking=False` at the request level with a `guided_json` schema, nearly every critic response will be malformed. This will look like "the JSON mode doesn't work on small models" but is actually a server configuration error. The fix is simple (remove `--reasoning-parser` from the critic process), but it must be applied deliberately. Make this the first configuration test before any loop runs.

#### What NOT to do

- **Do not use Ollama on Arc.** It has no Arc acceleration and silently falls back to CPU. IPEX-LLM (the historical workaround) was archived in January 2026. This is the correct conclusion in ADR 9.
- **Do not use tensor parallelism across both B70s for the two-model setup.** TP is for single large models that don't fit one card. Running each model pinned to one card is simpler, avoids oneCCL inter-GPU communication setup, and is the correct architecture for ember's sequential loop.
- **Do not set `--max-model-len` to the model's training maximum (32K–128K).** vLLM pre-allocates KV cache blocks for the full stated length. On 12 GB, `--max-model-len 131072` will OOM before the first request.
- **Do not run MoE models on XPU as the primary production configuration** until the MoE XPU kernel has more soak time. The 35B-A3B is technically supported but represents more surface area for Arc-specific issues. Dense 27B is the conservative, validated path.
- **Do not skip the `--enforce-eager` flag on XPU.** CUDA graphs do not exist on the XPU backend; omitting `--enforce-eager` will cause startup failures or silent correctness issues.
- **Do not benchmark on the B70 without verifying the NDEBUG flag.** Builds without `-DNDEBUG` understate prefill by 50–180%.

---

## References

1. **Qwen3 model card and blog** — https://qwenlm.github.io/blog/qwen3/
   *Primary source for Qwen3 architecture, benchmark scores, thinking/non-thinking mode design, AWQ/FP8 pre-quantized model availability.*

2. **Qwen3 technical report (arXiv 2505.09388)** — https://arxiv.org/pdf/2505.09388
   *Formal benchmark comparisons for all Qwen3 sizes including 4B and 8B vs prior generation; IFBench scores.*

3. **Intel LLM-Scaler GitHub** — https://github.com/intel/llm-scaler
   *Official Intel vLLM XPU image; supported models, quantization methods, multi-GPU setup, Docker launch parameters. Required reading before Arc deployment.*

4. **vLLM XPU hardware supported models doc** — https://docs.vllm.ai/en/stable/models/hardware_supported_models/xpu/
   *Canonical list of validated XPU models and supported quantization on Arc Pro B-Series. Check for Qwen3.6 addition.*

5. **vLLM Quantization docs** — https://docs.vllm.ai/en/latest/features/quantization/
   *AWQ, GPTQ, FP8, INT4 support matrix; which backends (Marlin, torchao) are used per format on CUDA.*

6. **Intel Quantization RFC H1 2026 (vLLM #37979)** — https://github.com/vllm-project/vllm/issues/37979
   *Intel's roadmap for XPU quantization: W4A16, FP8 W8A16 on Linear and MoE layers. Signals which gaps are on Intel's radar.*

7. **AWQ INT4 XPU failure (LLM-Scaler #269)** — https://github.com/intel/llm-scaler/issues/269
   *Exact failure mode for pre-quantized AWQ on XPU (torchao CUDA assertion). Workaround: sym_int4 or AutoRound.*

8. **Qwen3 guided JSON bug (vLLM #18819)** — https://github.com/vllm-project/vllm/issues/18819
   *Bug where enable_thinking=False + guided_json produces garbage output. Affects exactly the critic configuration ember wants. Workaround documented.*

9. **Intel arc-pro-b70-benchmarks (PMZFX)** — https://github.com/PMZFX/intel-arc-pro-b70-benchmarks
   *Community benchmark repository with FINDINGS.md: NDEBUG bug, MoE vs dense energy, Qwen3.6-35B performance, dual-card scaling.*

10. **arc-pro-b70-inference-setup-ubuntu-server (Hal9000AIML)** — https://github.com/Hal9000AIML/arc-pro-b70-inference-setup-ubuntu-server
    *Working vLLM TP=2 setup for 2× B70; 140 tok/s at 8-concurrent; documents critical env vars and kernel version requirements.*

11. **"How to Run Qwen3.6-27B on Intel Arc Pro B70" (Medium)** — https://bibek-poudel.medium.com/how-to-run-qwen3-6-27b-locally-on-intel-arc-pro-b70-what-actually-works-c96dec67c6f7
    *Practitioner walkthrough: llama.cpp SYCL on B70, Q4_K_M quant fits in 15.65 GiB, 14–22 tok/s. Confirms model-fits-one-B70 claim.*

12. **LLM-Scaler vllm-0.14.0-b8.2 release (Phoronix)** — https://www.phoronix.com/news/Intel-LLM-Scaler-vllm-0.14-b8.2
    *Third-party confirmation of official B70 support in the May 2026 release.*

13. **GPUStack quantization impact benchmark** — https://docs.gpustack.ai/2.0/performance-lab/references/the-impact-of-quantization-on-vllm-inference-performance/
    *AWQ vs GPTQ vs FP8 throughput and quality delta numbers across 70B-class models; Marlin-AWQ as fastest 4-bit option.*

14. **vLLM Structured Outputs docs** — https://docs.vllm.ai/en/latest/features/structured_outputs/
    *xgrammar, outlines, lm-format-enforcer backends; guided_json API; recommended for all critic JSON schema configuration.*

15. **AutoRound GitHub** — https://github.com/intel/auto-round
    *Intel's SOTA low-bit quantization for CPU/XPU/CUDA; W4A16 G128 is the recommended format for Arc when pre-quantized AWQ is unavailable.*

16. **vLLM multi-model single-GPU guide** — https://lyceum.technology/magazine/multi-model-serving-single-gpu-vllm/
    *gpu_memory_utilization split ratios; recommendation of 0.40–0.45 per model on shared 12 GB device.*

17. **Qwen3.5 & 3.6 vLLM Recipes** — https://docs.vllm.ai/projects/recipes/en/latest/Qwen/Qwen3.5.html
    *Canonical vLLM launch flags for Qwen3.x family: reasoning parser setup, thinking mode toggle, FP8 checkpoint usage.*
