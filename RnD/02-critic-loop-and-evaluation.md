# Critic-driven loop & plan evaluation

## Goal

Determine how to make ember's planner↔critic refinement loop converge reliably and become
measurable. Ground recommendations in 2024–2026 empirical findings on self-refinement,
multi-agent critique, convergence detection, plan evaluation, regression testing, and known
failure modes.

---

## Executive summary

- **Ember's asymmetric design is scientifically justified.** The strongest finding in the
  self-refinement literature is that intrinsic self-correction (same model critiques itself)
  fails on reasoning tasks and degrades as often as it improves. A *separate* critic model
  consistently outperforms self-critique, and heterogeneous pairings (Claude authors, GPT
  critiques) amplify the benefit by eliminating shared blind spots. Ember's design is
  correct; the evidence says don't consolidate to a single model.

- **Convergence signals exist beyond a round cap.** Tracking open-issue count per round
  (already done via `previousOpen`) is sound. The literature additionally recommends:
  semantic similarity between consecutive plan revisions to detect oscillation, a
  minimum-improvement threshold, and a hallucinated-issue rate budget for the critic.
  Ember currently has only the count backstop; the other signals are missing.

- **The critic is the fragile component.** CriticGPT-class models catch errors far better
  than humans (63 % preference rate) but produce hallucinated bugs at a materially higher
  rate than humans. In ember's critic prompt the severity labels `blocking` / `major` /
  `minor` are the only guard against issue inflation, and they are controlled entirely by
  the critic model. This is a single point of failure.

- **LLM-as-judge for plans is viable but requires analytic rubrics, not holistic scoring.**
  Pairwise comparison outperforms single-score pointwise evaluation for plan quality.
  Binary per-criterion judgments (MET / UNMET) produce the highest inter-rater reliability.
  Asking a judge "is this plan better than the previous one on criterion X?" is measurable;
  asking "score this plan out of 10" is not.

- **Loop regression testing is tractable.** A golden set of 20–30 hand-crafted
  (brief, plan-seed, expected verdict characteristics) triples, scored by an LLM judge
  against a rubric on every prompt or model change, gives a practical CI gate. Exact-match
  comparison of critic JSON output fails for non-deterministic pipelines; rubric-score
  regression is the right comparator.

- **The plan-improvement problem is not solved in the literature.** Gains plateau hard
  after round 1–2 of refinement; later rounds yield small or negative returns. A six-round
  cap is likely two to three rounds too many in practice. The cost of rounds 4–6 is real;
  the gain is speculative.

---

## Findings

### 1. Self-refinement: what the evidence actually shows

**Self-Refine (Madaan et al., 2023)** demonstrated that iterative critique-and-revision
improves output on open-ended generation tasks (dialogue, story, code style). Gains are
real but front-loaded: the first iteration captures most of the improvement; subsequent
rounds produce diminishing returns and occasionally regress.

**Huang et al., ICLR 2024** ("Large Language Models Cannot Self-Correct Reasoning Yet")
is the definitive negative result. Under *intrinsic* self-correction — the same model
corrects itself without external feedback — performance degrades as often as it improves
on reasoning benchmarks. The paper argues that what looks like "self-correction" in
multi-agent debate is actually self-consistency (majority voting across samples), not
genuine error correction. This directly cautions against any ember configuration where
the same model is both planner and critic.

**2025 follow-up empirical work** (large-scale study across Gemini 2.5 Pro, GPT-5,
DeepSeek-R1) confirmed: with purely intrinsic self-refinement, models gain ≤1.8
percentage points across five iterations on checklist-based pass rates. With *guided
external feedback*, the same models gain ~80 points. The gap is enormous and validates
ember's external-critic architecture.

**Reflexion (Shinn et al., 2023)** showed verbal reinforcement (retaining a memory of
prior critiques across episodes) yields +22 pp on AlfWorld and +11 pp on HumanEval.
The key insight: the critique must accumulate and be visible to the planner across
rounds, not just the most-recent verdict. Ember currently passes only the current
`CriticVerdict.OpenIssues` to the planner on each revision call. Prior rounds' issues
that were "resolved" are invisible — the planner cannot see what it already fixed, and
could unconsciously re-introduce earlier problems.

### 2. Separate critic vs. self-critique: the evidence for ember's design

**CriticGPT (McAleese et al., OpenAI 2024)** is the clearest empirical endorsement of
a separate critic model. A GPT-4-based critic trained specifically on RLHF critique tasks:
- Was preferred over human-written critiques in 63 % of cases on naturally-occurring errors.
- Was preferred over human critiques > 80 % of the time on seeded bugs.
- Substantially *reduced* nitpick and hallucination rates compared to prompted ChatGPT.
- Still produced higher hallucinated-bug rates than humans.

The paper recommends "human-machine teams" as the practical operating point. For ember's
unattended pipeline, the implication is: build a hallucinated-issue rate budget into the
critic prompt and termination logic.

**Heterogeneous multi-agent debate (A-HMAD, 2025)** showed +7–15 pp improvement over
single-agent baselines when agents use different model families and roles. The mechanism
is error-set disjointness: GPT and Claude have different blind spots, so cross-model
critique surfaces issues neither would self-identify. Ember's Claude-author / GPT-critic
split is optimal on this dimension.

**Caution on multi-agent debate convergence.** When agents are similar or weak, MAD
underperforms advanced single-agent reasoning. Heterogeneity is the differentiating factor;
same-family models converge to shared errors rather than correcting them.

### 3. Termination and convergence: detecting diminishing returns and oscillation

**What ember has today:**
- Round cap (`MaxPlanRounds = 6`)
- Stall detection: `OpenIssues.Count >= previousOpen` (count did not decrease)

**What is missing:**

*Semantic similarity check.* Comparing the current plan revision to the previous one
using embedding cosine similarity (or even normalized Levenshtein on the Markdown text)
detects when the planner is making cosmetic edits rather than substantive revisions.
If similarity > 0.95 between rounds N and N+1, the loop has stalled semantically even
if the issue count decreased by one (which could be critic variability, not real progress).

*Minimum-improvement threshold.* Rather than "did count decrease at all?", track whether
the count decreased by at least K (e.g., K=1 blocking or K=2 total major+blocking) per
round. A loop that goes from 3 blocking to 2 blocking to 1 blocking is converging;
a loop that bounces between 2 and 3 is oscillating. The current `>=` check catches
non-decreasing; it misses the bounce-between-two-values pattern if count alternates.

*Round-over-round issue identity tracking.* The stall check compares counts, not
identities. If the critic removes issue A and adds issue B in the same round, the count
is unchanged but the *content* changed — this is oscillation/churn, not convergence.
Tracking issue summaries (or hashed fingerprints) across rounds makes oscillation
visible.

*Over-refinement guard.* Research on Reflexion and Self-Refine consistently warns of
"over-refinement" — the plan degrades after round 4–5 because the planner starts
hallucinating structure to satisfy the critic. A practical proxy: if the plan length
grows more than 30 % between two consecutive rounds without a commensurate drop in
open issues, treat that round as regressive.

**Recommended round cap.** The literature consensus is that the first 1–2 iterations
capture most improvement. A cap of 6 is conservative in cost and possibly harmful
(rounds 4–6 may make the plan worse). A cap of 3–4, combined with richer stall
detection, is likely better.

### 4. Plan evaluation: measuring whether a plan improved

Plan quality is not well-defined in the literature for *prose/Markdown implementation
plans* (as opposed to formal PDDL plans with executable validators). Practical approaches:

**Analytic rubric scoring (LLM-as-judge).** The 2025–2026 consensus on LLM-as-judge
for qualitative artifacts is: decompose into independent binary (MET/UNMET) or narrow
ordinal (1–3) criteria. For a software implementation plan the criteria set should cover:

| Criterion | Prompt framing |
|---|---|
| Goal clarity | Does the plan state a clear, unambiguous end-state? |
| Step completeness | Are the implementation steps concrete enough to act on? |
| File/module specificity | Are affected files named? |
| Acceptance criteria | Are testable success conditions present? |
| Failure-mode coverage | Are at least the obvious failure paths addressed? |
| Scope containment | Does the plan avoid scope creep beyond the brief? |

Scoring each criterion 0/1 and summing gives a rubric score from 0–6. A score trend
across rounds is the convergence signal. This sidesteps the unreliable "score out of 10"
pattern.

**Pairwise comparison.** "Is plan N better than plan N-1 on criterion X?" outperforms
pointwise scoring for alignment with human judgment (literature shows +17 pp on smaller
judge models). For ember's loop, a pairwise judge call after each revision round
(comparing current to previous) gives a binary "improved / regressed" signal per
criterion. This is cheap (one additional LLM call) and actionable.

**Known biases to control:**
- *Verbosity bias*: LLM judges prefer longer responses. A longer plan revision is not
  necessarily better. Explicitly instruct the judge: "Do not prefer the plan that is
  longer."
- *Position bias*: When doing pairwise, randomize which plan (current vs. prior) appears
  first and average across orderings.
- *Self-enhancement bias*: If Claude is both planner and judge, it will favor its own
  output. Use a different model family as judge (GPT-4o is the natural choice given it
  is already the critic).
- *Agreeableness*: LLM judges exhibit high true-positive rates (>96 %) but very low
  true-negative rates (<25 %). They rarely say "this plan got worse". Use explicit
  negative exemplars in the judge's few-shot prompt.

**Correlation with critic verdicts.** In ember's existing loop, the critic verdict
*is* the implicit quality signal: fewer open issues = better plan. This is reasonable
but conflates plan quality with critic satisfaction. A plan can satisfy the critic
with superficial additions that technically address each issue without improving
the underlying engineering thinking. A separate rubric judge catches this.

### 5. Loop regression testing

The ember loop is a non-deterministic pipeline. Exact-match comparison of critic JSON
output or plan text is not a useful regression gate.

**Practical golden-set approach:**

1. Capture 20–30 `(brief, repoContext)` input pairs from real ember sessions.
2. Record: expected gate outcome category (Approved vs. Stalled/RoundCap), expected
   open-issue count range at termination, expected rubric score range for the final plan.
3. On any prompt change or model swap, re-run the full loop against all golden inputs.
4. Score each final plan with the rubric judge. Flag any item where the rubric score
   drops by more than 1 criterion from the recorded baseline, or where the gate outcome
   category changes.

**The tiered gate pattern** from industry practice:
- Every commit: parse-and-validity check on critic JSON (catch prompt regressions that
  break structured output).
- Every PR merge: rubric-score regression on 5–10 golden inputs.
- Periodic (weekly): full 20–30 input suite with cost and latency tracking.

**Semantic similarity as a regression signal.** Embed the final plan text from the
baseline run and the new run. A cosine similarity < 0.85 warrants human review; it
means the plan changed substantially. Not all change is regression, but unintended
large drift is a strong signal of prompt or model impact.

**Non-determinism handling.** Run each golden input 3 times and median the rubric score
and issue count. This collapses stochastic variance and makes genuine regressions
distinguishable from sampling noise.

### 6. Failure modes and mitigations

**Critic sycophancy.** After several rounds, the critic may shift from red-teaming to
approving because the plan now matches whatever pattern the critic associates with
"good". This is more likely with GPT models after RLHF, which amplifies agreeableness.
Mitigation: include an explicit instruction in the critic system prompt: *"Your job is
to find real problems. If you rated a prior version of this plan more harshly, explain
why you changed your assessment."* Also: if the critic approves after round 1 on a
complex brief, treat this as a yellow flag — require a second independent critique call
before accepting (not currently implemented in ember).

**Severity inflation / issue manufacturing.** The critic may re-classify prior `minor`
issues as `major` (or invent new `blocking` issues) to avoid appearing sycophantic.
This is the opposite pathology. Mitigation: track severity distribution per round. If
blocking-count *increases* after a revision, require the critic to explicitly justify
each newly-escalated issue with: *"This was previously [severity]; I am elevating it
because…"*. This can be enforced in the critic system prompt.

**Planner ignoring critique.** The current `ReviseAsync` passes `OpenIssues` as a flat
list and says "resolve every one". In practice, LLMs selectively ignore issues that
require significant structural revision. Research at ICLR 2025 on human reviewers given
structured feedback found only 26.6 % incorporated it. LLMs are not better. Mitigation:
after each revision, the critic should be asked to verify specific prior issues: *"Issue
[X] was present in the previous version. Is it resolved in this version? Yes or No."*
A structured resolution-tracking call (separate from the main review) catches slippage.

**Verdict gaming.** If the planner is a powerful model, it may learn to satisfy the
critic's literal issue list without improving the underlying plan (e.g., adding acceptance
criteria as boilerplate text to satisfy the "acceptance criteria" issue). This is hard to
detect from the verdict alone. Mitigation: the rubric judge (criterion 6: scope
containment) catches whether the plan gained text without gaining substance.

**Stalled loop misclassified as Approved.** A parse failure in the critic already has a
safety net (treated as Major issue). The second risk is a valid JSON verdict with
`issues: []` produced by a sycophantic critic after exhausting the planner's ability to
revise. Gate-display logic already distinguishes Approved from RoundCap/Stalled —
this is good. The additional safeguard is: require the critic to state a positive
assessment of each plan dimension explicitly (not just the absence of issues) before
emitting an empty issues list. A one-sentence "this plan is sound on [dimensions]"
structured field makes vacuous approvals less likely.

**Hallucinated critic issues.** CriticGPT research quantified this: LLM critics
hallucinate bugs at a higher rate than humans even after RLHF training. In ember,
hallucinated `blocking` issues will cause unnecessary revision rounds. The cost is
wasted LLM calls; the risk is plan degradation if the planner tries to address a
non-existent problem. Mitigation: add a `rationale` field to `CriticIssue` (already
has `Fix` but no evidence field). Require the critic to quote or paraphrase the specific
plan text that supports each issue. Ungrounded issues (no quoted evidence) are more
likely hallucinated.

---

## Surprising / novel

- The magnitude of the guided-vs-intrinsic gap is larger than most practitioners assume.
  State-of-the-art models in 2025 gain <2 pp from self-correction but ~80 pp from
  external guidance. This is not a marginal difference; it is a qualitative boundary.
  Ember's architecture sits on the right side of it.

- Pairwise LLM comparison outperforms pointwise scoring not just slightly but by ~17 pp
  on human alignment for smaller judge models. Most LLM eval frameworks default to
  pointwise scoring because it is simpler. The literature now clearly favors pairwise for
  plan-quality judgments.

- The ICLR 2025 human-review study finding (only 26.6 % of reviewers incorporated
  structured feedback they were given) is a sobering benchmark for what to expect from
  the planner model. "The planner will incorporate every issue" is not a safe assumption.

- Adversarial prompt injection can steer LLM judge scores dramatically (JudgeDeceiver,
  2024). For ember this is low-risk (the operator is not an adversary), but it underscores
  that the critic's structured JSON output is a manipulable surface if prompt injection
  ever becomes a threat vector (e.g., if the brief includes untrusted text from a repo
  issue tracker).

- The "Stalled" backstop ember already has (`OpenIssues.Count >= previousOpen`) is more
  sophisticated than most published loop implementations, which use only a round cap.
  The existing implementation is ahead of the published field on this specific point.

---

## Where this uniquely aligns with ember

**ADR 5's asymmetric design is empirically optimal.** The Huang et al. finding (same
model cannot self-correct reasoning) and the CriticGPT finding (separate critic model
outperforms humans) together fully validate ADR 5's core decision. There is no
academic pressure to revisit this.

**The `previousOpen >= currentOpen` stall check is sound but incomplete.** It catches
count non-decrease; it does not catch oscillation (count bounces between 2 and 3) or
semantic stall (plan text barely changes despite issue list moving). Both gaps are
fixable with low-engineering-cost additions.

**The `CriticVerdict.OpenIssues` list is passed to `ReviseAsync` flat.** This means
the planner receives no context about which issues persist from prior rounds vs. which
are newly surfaced. Prior-round context is the mechanism that makes Reflexion work.
Adding a "prior issues that were not resolved" field to the revision prompt would bring
ember meaningfully closer to the Reflexion improvement profile.

**The soft gate already differentiates Approved / RoundCap / Stalled.** This is the
correct pattern. The operator sees which exit path was taken. No change needed here.

**The six-round cap is likely one to two rounds too many.** Literature shows gains plateau
at rounds 1–2. Rounds 4–6 risk degradation. For a single-operator personal tool where
inference cost matters less than code quality, the risk is plan overfit to the critic's
preferences rather than financial cost. But a cap of 4 is safer and consistent with
the evidence.

**The `GateCountdownSeconds` pattern (gate survives restarts) is not present in any
published loop design** and is a genuine reliability contribution from ember's systems
engineering context.

**The critic system prompt instructs "Do NOT raise style preferences"** — this is a
direct mitigation of nitpick inflation and is well-aligned with the research recommendation
to constrain the critic's scope to failure-critical issues. Good instinct, already
implemented. The gap is on the escalation-detection and grounding-evidence side.

---

## Recommendations

Prioritized in order of impact-to-effort ratio. The first three are loop-hardening changes
to existing code; the rest are new capabilities.

### P0 — Loop hardening (do now, low effort)

**R1. Add `Rationale` / evidence field to `CriticIssue`.**
Extend `CriticIssue` with a `string Evidence` property and update the critic system prompt
to require the critic to quote or paraphrase the specific plan text supporting each issue.
Issues with no quoted evidence are candidates for hallucination filtering. This is a
two-line model change and a system-prompt addition. It also makes the Discord thread output
more useful to the operator.

**R2. Pass prior-round issue history to the planner.**
In `ReviseAsync`, add a `previousRoundIssues` parameter and include issues from *all*
prior rounds (not just current) in the revision prompt, grouped by status: "These issues
were raised in earlier rounds and the plan now appears to address them — do not re-introduce
them." This is the Reflexion insight applied to ember's plan refinement context.
Implementation: accumulate a `List<(int round, CriticIssue issue)>` in `PlanningLoopRunner`
and pass it down the call chain.

**R3. Lower the round cap to 4.**
Change `MaxPlanRounds = 6` to `MaxPlanRounds = 4`. The literature is consistent: rounds 5+
produce negligible or negative returns. Adjust `GateReason.RoundCap` messaging to reflect
this. If future evidence from ember's own runs argues for more, raise it back.

### P1 — Convergence detection (medium effort, high value)

**R4. Add semantic similarity stall detection.**
After each revision, compute the normalized edit distance (or cosine similarity on
tokenized plan text) between the new plan and the prior plan. If similarity > 0.95,
treat as a stall regardless of issue count change. This requires no external dependency —
a simple `StringComparer` on Markdown text normalized to lowercase, punctuation-stripped
tokens is sufficient for a first pass. Emit a structured telemetry span (`plan.similarity`)
so the operator can see the convergence curve in Jaeger.

**R5. Track issue identity across rounds, not just count.**
Add fingerprinting of issue summaries (lowercase + trim + first 60 chars is sufficient
as a fuzzy key). Track which issues appeared in round N and recur in round N+1 with the
same or higher severity. Log this as `plan.recurring_issues` in telemetry. A recurring
blocking issue is a strong signal that the planner cannot resolve it and the loop should
stall rather than continue.

### P2 — Plan quality measurement (new capability, medium effort)

**R6. Build a minimal rubric judge.**
Create a `PlanQualityJudge` service that scores a plan revision against the six-criterion
rubric (goal clarity, step completeness, file specificity, acceptance criteria, failure
coverage, scope containment). Each criterion returns 0 or 1. The total (0–6) is logged
as a telemetry tag. Call this once per round. The rubric score trend across rounds (not
the final score) is the meaningful signal. If the score stops increasing before the
round cap, treat as a convergence signal.

**R7. Pairwise improvement check.**
After each revision, ask the judge: "Is Plan B better than Plan A on criterion X? Yes/No."
Run this for the three most important criteria (goal clarity, acceptance criteria, failure
coverage). A regression on any criterion in round N+1 relative to round N is a loop-quality
alert. This adds one LLM call per round but provides the most actionable measurement
signal.

### P3 — Regression harness (new capability, medium effort)

**R8. Build a golden-set regression runner.**
Create a console test runner (`scripts/` or a separate project) that:
- Takes a JSON file of `(brief, repoContextSnippet)` pairs (start with 10 cases).
- Runs the full ember loop (or a dry-run loop that calls planner and critic but skips
  Discord and the gate).
- Records: gate outcome, round count, final rubric score, final open-issue count.
- Compares against a checked-in baseline JSON.
- Fails with a non-zero exit code if rubric score drops >1 on any case, or gate outcome
  changes category.

This is not a test suite in the unit-test sense — it is a prompt-regression harness.
Run it before any critic prompt change or model swap.

**R9. Run each golden case 3 times and median the scores.**
Non-determinism makes single-shot comparison unreliable. Three runs per case is the
minimum to separate genuine regression from stochastic variance. Cache the raw verdicts
to avoid re-running on re-evaluation.

### What NOT to do

- **Do not consolidate planner and critic to a single model.** The evidence is unambiguous:
  same-model self-correction fails or degrades for reasoning tasks. The cost savings are
  not worth it.

- **Do not use holistic 1–10 scoring for plan quality.** LLM judges show central tendency
  bias, verbosity bias, and self-enhancement bias on open-ended numeric scales. The score
  is noisy. Binary per-criterion scoring is more reliable.

- **Do not increase MaxPlanRounds beyond 4 without evidence from ember's own runs.**
  Round count inflation has a real cost: plan overfit to critic preferences, increased
  token spend, and the risk of the planner hallucinating structure to satisfy the critic.

- **Do not replace the operator's soft-gate veto with automatic approval.** The literature
  on RLHF and critic models is clear: critic models hallucinate issues, approve
  vacuously, and can be manipulated. The human-in-the-loop gate is a genuine safety
  property, not bureaucratic overhead.

- **Do not rely on critic JSON parse success as a quality signal.** A valid JSON with
  `issues: []` is not the same as a genuinely sound plan. The empty-issues case needs
  its own positive-evidence check (see failure-mode section above).

---

## References

1. **Madaan et al. — Self-Refine: Iterative Refinement with Self-Feedback (2023)**
   `https://arxiv.org/abs/2303.17651`
   Original Self-Refine paper. Demonstrates iterative critique+revision improves open-ended
   tasks; gains are front-loaded in round 1. Foundation for the field.

2. **Huang et al. — Large Language Models Cannot Self-Correct Reasoning Yet (ICLR 2024)**
   `https://arxiv.org/abs/2310.01798`
   Definitive finding: intrinsic self-correction fails or degrades on reasoning tasks
   without external feedback. The key empirical justification for ember's separate-critic
   design.

3. **Shinn et al. — Reflexion: Language Agents with Verbal Reinforcement Learning (2023)**
   `https://arxiv.org/abs/2303.11366`
   Verbal reflection accumulated across episodes yields +22 pp on AlfWorld, +11 pp on
   HumanEval. Key insight for ember: pass prior-round issue history to the planner.

4. **McAleese et al. (OpenAI) — LLM Critics Help Catch LLM Bugs / CriticGPT (2024)**
   `https://arxiv.org/html/2407.00215v1`
   Separate GPT-4-based critic preferred over human critiques 63 % of the time. Also
   shows hallucinated-bug failure mode. Direct evidence for ember's heterogeneous design
   and for adding an evidence/rationale field to CriticIssue.

5. **A Survey on LLM-as-a-Judge (2024–2025)**
   `https://arxiv.org/html/2411.15594v6`
   Comprehensive survey of pairwise vs. pointwise evaluation, known biases (position,
   verbosity, self-enhancement), and reliability practices. Pairwise outperforms pointwise
   by ~17 pp on human alignment.

6. **Adaptive Heterogeneous Multi-Agent Debate (A-HMAD, 2025)**
   `https://link.springer.com/article/10.1007/s44443-025-00353-3`
   Heterogeneous agent pairings (different model families, different roles) yield +7–15 pp
   over homogeneous single-model baselines. Validates Claude-author / GPT-critic pairing.

7. **Multi-Agent Debate for LLM Judges with Adaptive Stability Detection (2025)**
   `https://arxiv.org/html/2510.12697v1`
   Debate among judge agents with stability detection — formalizes convergence criteria
   for judge panels. Relevant to ember's rubric-judge design.

8. **Enhancing LLM Planning Capabilities through Intrinsic Self-Critique (2025)**
   `https://arxiv.org/html/2512.24103v1`
   Applies self-critique to formal planning tasks. Key finding: most gains in round 1;
   self-consistency voting (multiple critique calls, majority vote) reduces false-positive
   critic verdicts by ~5 %.

9. **Rubric-Based Evaluations & LLM-as-a-Judge (Masood, Apr 2026)**
   `https://medium.com/@adnanmasood/rubric-based-evals-llm-as-a-judge-methodologies-and-empirical-validation-in-domain-context-71936b989e80`
   Analytic rubrics (criterion-by-criterion) vs. holistic scoring. Binary criteria yield
   highest inter-rater reliability. Practical rubric design patterns.

10. **Evaluating the Effectiveness of LLM-Evaluators (Eugene Yan, 2024)**
    `https://eugeneyan.com/writing/llm-evaluators/`
    Empirical reliability analysis: position bias (~50–70 % depending on model), verbosity
    bias (>90 % preference for longer), self-enhancement (~10 % self-preference for GPT-4).
    Practical recommendations for building reliable eval harnesses.

11. **Automated Prompt Regression Testing with LLM-as-a-Judge and CI/CD (Traceloop)**
    `https://www.traceloop.com/blog/automated-prompt-regression-testing-with-llm-as-a-judge-and-ci-cd`
    Practical pattern for golden-set regression testing of LLM pipelines. Tiered gate
    (JSON validity on commit, rubric score on PR, full suite on merge). Directly applicable
    to ember's regression harness design.

12. **LLM Regression Testing Pipeline (TestQuality, 2026)**
    `https://testquality.com/llm-regression-testing-pipeline/`
    Golden dataset design (200–500 cases for production; 20–30 for personal tooling),
    semantic similarity as a regression comparator, non-determinism handling strategies.

13. **LLMs Cannot Reliably Judge Yet: Assessment on Robustness of LLM-as-a-Judge (2025)**
    `https://arxiv.org/html/2506.09443v1`
    Robustness failure modes in LLM judges: adversarial prompt injection, sensitivity to
    phrasing, near-chance performance under adversarial attack. Relevant to ember's
    planned rubric judge.

14. **SycEval: Evaluating LLM Sycophancy (AAAI 2025)**
    `https://ojs.aaai.org/index.php/AIES/article/download/36598/38734/40673`
    Systematic measurement of sycophancy including rebuttal-sycophancy (judge reverses
    assessment when challenged). Relevant to critic behavior in later rounds.

15. **AWS Prescriptive Guidance — Evaluator Reflect-Refine Loop Patterns**
    `https://docs.aws.amazon.com/prescriptive-guidance/latest/agentic-ai-patterns/evaluator-reflect-refine-loop-patterns.html`
    Systems-engineering framing of LLM feedback loops as feedback control systems.
    Convergence criteria, retry logic, escalation patterns. Useful reference for
    ember's gate design.

16. **Self-Reflection in LLM Agents: Effects on Problem-Solving Performance (2024)**
    `https://arxiv.org/pdf/2405.06682`
    Over-refinement characterization: excessive iteration leads to diminishing returns or
    degradation. Recommends careful balance in refinement process design.
