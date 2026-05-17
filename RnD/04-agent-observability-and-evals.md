# Agent observability & evals

## Goal

Evolve ember's telemetry from "per-stage traces exist in Jaeger" to a state where traces
actively drive evaluation, cost accounting, and regression detection — without adding
operational overhead that would be wrong-sized for a single-operator, self-hosted project.

---

## Executive summary

- **OTel GenAI semantic conventions (gen_ai.\*) are the right foundation to adopt now.**
  They are still in "Development" (experimental) stability, but `Microsoft.Extensions.AI`
  already ships `OpenTelemetryChatClient` implementing spec v1.41, so ember can get standard
  token-count attributes (`gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`) and
  model metadata into every planner and critic span with a one-line pipeline change — no
  third-party library needed.

- **Cost accounting is a solved, low-effort add.** Token counts land on spans via
  `OpenTelemetryChatClient`; a thin `DelegatingChatClient` middleware can compute a
  `llm.cost.usd` attribute at call time and increment an OTel counter. Jaeger already stores
  it. No new infrastructure required.

- **Arize Phoenix is the best-fit dedicated eval layer for a single-operator setup.**
  One `docker run` command, accepts OTLP gRPC on port 4317 (same port ember already uses for
  Jaeger), SQLite-backed by default, no cloud account required. Langfuse is the stronger
  eval platform but requires Postgres + ClickHouse + MinIO + Redis — significant overhead for
  one operator.

- **ADR 7's per-stage trace design is directly compatible with Phoenix's data model**,
  but ADR 7 itself already identified the clean path forward: persist the W3C trace context
  in the session row and add OTel span links so each stage trace links back to the preceding
  one. This turns tag-based correlation into first-class linked traces in any backend.

- **Regression detection for a non-deterministic pipeline requires a golden-set replay
  loop, not continuous monitoring alone.** Curate 10–20 (`brief`, `expected_plan_shape`)
  pairs; run them through the planner+critic loop in a separate execution mode; score with
  an LLM-as-judge; track the score time series. A sustained drop of two or more consecutive
  runs signals drift.

- **Do not adopt LangSmith or Braintrust.** Both are cloud-first products with no credible
  self-hosted path; they would route every prompt and completion off-machine. They are also
  overkill for a single pipeline.

---

## Findings

### 1. OTel GenAI semantic conventions — current state

**Stability:** All `gen_ai.*` attributes, metrics, and events are in "Development" status as
of May 2026. The spec explicitly states that the transition plan to "Stable" will be published
before promotion. The version identifiers matter: instrumentations targeting v1.36 or earlier
must not silently switch to the new form; they should gate behind
`OTEL_SEMCONV_STABILITY_OPT_IN=gen_ai_latest_experimental`.

**Span attributes (gen_ai-spans):**

| Attribute | Level | Purpose |
|---|---|---|
| `gen_ai.operation.name` | Required | `chat`, `embeddings`, `execute_tool` |
| `gen_ai.provider.name` | Required | `openai`, `anthropic`, `ollama` |
| `gen_ai.request.model` | Conditionally Required | Model the request targets |
| `gen_ai.response.model` | Recommended | Model that actually responded |
| `gen_ai.usage.input_tokens` | Recommended | Prompt token count |
| `gen_ai.usage.output_tokens` | Recommended | Completion token count |
| `gen_ai.usage.cache_read.input_tokens` | Recommended | Tokens served from provider cache |
| `gen_ai.usage.cache_creation.input_tokens` | Recommended | Tokens written to provider cache |

There is **no official `cost` attribute** in the spec. Cost must be computed as a custom
attribute (`llm.cost.usd`) using the formula:
`(input_tokens × input_rate + output_tokens × output_rate) / 1_000_000`.

**Agent-specific spans (gen_ai-agent-spans):**

| Attribute | Level | Purpose |
|---|---|---|
| `gen_ai.agent.name` | Conditional | Human-readable agent name ("planner", "critic") |
| `gen_ai.agent.id` | Conditional | Unique agent instance id |
| `gen_ai.agent.description` | Conditional | Free-form role description |
| `gen_ai.conversation.id` | Conditional | Correlates messages across iterations |
| `gen_ai.workflow.name` | Conditional | Multi-agent workflow label |
| `gen_ai.operation.name` | Required | `invoke_agent` for orchestration spans |

**Events (gen_ai-events):** Two standard event names:
- `gen_ai.client.inference.operation.details` — captures prompt/completion content
  (opt-in; likely contains PII; gated by `EnableSensitiveData = true`)
- `gen_ai.evaluation.result` — carries `gen_ai.evaluation.name`, `gen_ai.evaluation.score.value`,
  and `gen_ai.evaluation.score.label`. This is the spec's own hook for attaching eval scores
  to spans. Both events are Development status.

**Metrics (gen_ai-metrics):**

| Metric | Unit | Type | Purpose |
|---|---|---|---|
| `gen_ai.client.token.usage` | `{token}` | Histogram | Input/output tokens per call |
| `gen_ai.client.operation.duration` | `s` | Histogram | End-to-end latency per call |
| `gen_ai.client.operation.time_to_first_chunk` | `s` | Histogram | Streaming TTFT |

All Development status.

**`Microsoft.Extensions.AI` integration:** ember already uses `IChatClient`. Adding
`.UseOpenTelemetry()` to the client pipeline in `ChatClientFactory.Create()` inserts
`OpenTelemetryChatClient` (implements spec v1.41) as a middleware, automatically emitting
token counts and model attributes on every `GetResponseAsync` call. The `UsageDetails`
object on `ChatResponse` exposes `InputTokenCount`, `OutputTokenCount`, and `TotalTokenCount`
for any manual handling if needed.

---

### 2. Agent observability tooling — self-hostable options

#### Arize Phoenix (recommended for ember)

- **What it is:** Open-source LLM observability + evals platform from Arize AI, MIT-licensed.
- **Deployment:** Single Docker container:
  `docker run -p 6006:6006 -p 4317:4317 -i -t arizephoenix/phoenix:latest`
  SQLite by default; PostgreSQL >= 14 optionally for production persistence.
  OTLP gRPC on port 4317 and OTLP HTTP on port 6006. The gRPC port overlaps with Jaeger's
  current port (4317) — one or the other must be remapped if run on the same machine.
  Reported resource usage: as low as 0.5 CPU / 1 GB RAM for light workloads.
- **Key capabilities:** Trace ingestion via OTLP, span-level LLM-as-judge evals, dataset
  management (collect traces → dataset → replay), human annotation, experiment comparison
  (A/B prompt variants), cost and token tracking dashboards.
- **Limitations:** The Python-ecosystem SDKs (OpenInference) are first-class; .NET receives
  raw OTLP only — no auto-instrumentation package. Phoenix's own UI-triggered evals require
  an OpenAI or Anthropic API key to call the judge model (not self-contained).
- **Evidence quality:** Strong — docs and GitHub are current, Docker Hub image updated 2025.

#### Langfuse (strong eval platform, heavier infra)

- **What it is:** Open-source LLM observability with a strong eval, prompt management, and
  annotation story. MIT licensed. OTLP support added in v3.22.0.
- **OTLP endpoint:** `http://localhost:3000/api/public/otel` (self-hosted). Accepts OTLP
  over HTTP/JSON and HTTP/protobuf; **gRPC not yet supported**.
- **Span-to-trace mapping:** Any span with a `model` attribute becomes a "Generation" record
  in Langfuse's data model. Root spans become trace records. `gen_ai.*` attributes map
  automatically to Langfuse fields.
- **Eval capabilities:** Online evals (asynchronous LLM-as-judge runs against ingested
  observations) and offline experiments. Eval scores attach back to the specific observation.
  Self-hosted evals work but require the worker container and a judge model API key.
- **Infrastructure cost:** Full v3 stack requires PostgreSQL + ClickHouse + MinIO + Redis +
  two app containers. Production minimum: 2 CPU, 4 GB RAM for all containers.
- **Verdict for ember:** Infra burden is real. Phoenix or raw OTel+Jaeger is right for now;
  Langfuse becomes worth the overhead if prompt management and annotation UIs are wanted.

#### OpenLLMetry / Traceloop

- **What it is:** OTel extension library providing zero-code auto-instrumentation for OpenAI,
  Anthropic, Ollama, and vector DBs. Python and Go packages only. **No .NET package exists.**
- **Verdict:** Not usable directly. ember already emits custom spans via `ActivitySource`;
  the `OpenTelemetryChatClient` middleware covers the same ground in C#.

#### Helicone

- **What it is:** Open-source LLM observability + AI gateway. Apache 2.0. Self-hosted via
  Docker Compose (reduced to 4 containers as of 2025).
- **Integration model:** Primarily an API proxy (requests pass through Helicone), not an OTLP
  receiver. A separate `ai-gateway` repo handles OTLP ingestion.
- **Verdict:** Proxy-model doesn't fit ember's architecture (calls go direct to OpenAI/Anthropic
  endpoints; no gateway layer). OTLP ingestion is less mature than Phoenix or Langfuse.

#### Braintrust

- **What it is:** Cloud-first LLM eval and observability platform. Online scoring, CI/CD
  GitHub Actions integration, trace-to-dataset conversion.
- **Self-hosted:** No credible self-hosted path. All data transits Braintrust cloud.
- **Verdict:** Eliminate. Violates the single-operator, self-hostable constraint.

#### LangSmith

- **What it is:** LangChain's hosted tracing and eval platform.
- **Self-hosted:** Enterprise only (paid). Cloud otherwise.
- **Verdict:** Eliminate. Same reasons as Braintrust.

#### Jaeger (current)

- **What it is:** ember's existing trace backend. Excellent for distributed trace visualization,
  span search, and latency analysis.
- **Eval capabilities:** None. Jaeger is a trace store, not an eval platform.
- **Verdict:** Keep for trace visualization. It is not replaceable by Phoenix for its primary
  purpose, but Phoenix + Jaeger in parallel is operationally simple (both accept OTLP; port
  assignment requires minor attention).

---

### 3. Trace-driven evals

**Pattern:** Production traces become eval inputs by extracting span attributes
(`gen_ai.input.messages`, `gen_ai.output.messages`, or ember's existing span tags) and feeding
them to an eval runner. Two modes:

- **Online evals** run asynchronously on every new trace at ingest time. An LLM-as-judge
  scores each observation against a rubric (e.g., "Does the critic verdict contain a concrete
  fix for every blocking issue?"). Scores are attached back to the span.
  Phoenix and Langfuse both support this; it requires a judge-model API call per scored span.

- **Offline evals (golden-set replay)** maintain a curated dataset of `(brief, expected_shape)`
  pairs. A replay runner feeds each brief through the live planner+critic loop and scores
  the output. This is a deterministic regression test for a non-deterministic pipeline.

**LLM-as-judge wired to traces:** The OTel spec itself provides `gen_ai.evaluation.result`
events with `gen_ai.evaluation.score.value` and `gen_ai.evaluation.score.label` attributes
for attaching scores back to spans in a standard way. Phoenix's eval framework can write
results to this event format, or a custom `DelegatingChatClient` can score inline.

**Cross-stage correlation for replay:** Because ember's pipeline is split across 4 separate
traces (ADR 7), replaying a "session" for eval means running all four stages and correlating
the outputs by `ember.thread_id`. The evaluation score applies to the plan text extracted
from the `plan.session` trace's output, not the full session trace.

---

### 4. Cost & token accounting

**Immediate path (no new infrastructure):**

1. Add `.UseOpenTelemetry()` to both planner and critic `IChatClient` pipelines in
   `ChatClientFactory.Create()`. Token counts appear on every chat span automatically.

2. Add a thin `CostAccountingChatClient : DelegatingChatClient` that reads `UsageDetails`
   after each `GetResponseAsync`, computes cost from a static pricing table, and sets
   `ember.llm.cost.usd` on the parent `Activity` plus increments an OTel counter
   `ember.llm.cost.total`.

3. Add `ember.agent` tag ("planner" or "critic") to the keyed service registrations so
   every span is tagged with which agent incurred the cost.

4. Jaeger stores all this; cost-per-session is queryable via tag filter on `ember.thread_id`
   summed across the 4 stage traces.

**Metrics path (for dashboards/alerts):**

Emit `gen_ai.client.token.usage` (the standard OTel histogram with `gen_ai.token.type`
= input/output) and a custom `ember.llm.cost.usd` counter. Route to Prometheus via the OTLP
metrics exporter (already wired in `Program.cs`). Alertmanager can fire when daily cost
exceeds a threshold.

**No cost attribute in the OTel spec:** The spec defines token counts but not cost. Custom
`llm.cost.usd` is the community convention; name collision risk is low but flag that this is
non-standard.

---

### 5. Regression detection

**The core challenge:** The planner+critic loop is non-deterministic. A single run cannot
tell you whether quality regressed; only a score distribution over multiple runs does.

**Recommended approach — golden-set replay:**

1. Curate 10–20 representative briefs with rubric-style expected outcomes
   (not exact text, but structural expectations: "plan must specify files touched",
   "critic must identify at least one issue on a deliberately underspecified brief").
2. Implement a `PlanCli`-compatible replay runner that runs each brief through the live loop,
   captures the plan text and critic verdict, scores with an LLM judge, and writes results
   to a JSON file.
3. Track the aggregate score per run. Alert if the rolling average over 2 consecutive runs
   drops by > 10% from the baseline.
4. Store baselines as committed JSON snapshots. Diff on every model swap or prompt edit.

**Triggers for replay:**
- Planner or critic model change (`appsettings.*.json` diff)
- System prompt edit (tracked in source control)
- Local model update (vLLM reload for Intel Arc path, per ADR 9/10)

**What NOT to do for regression detection:**
- Continuous per-request scoring: adds latency and cost to every production run; unnecessary
  for a single-operator tool.
- Statistical process control on live traces: insufficient volume (one plan per developer
  intent) for meaningful SPC charts.

---

## Surprising / novel

- **`gen_ai.evaluation.result` is a first-class OTel event**, not just a custom attribute.
  The spec already anticipates eval scores being attached to traces. Almost no tooling
  documentation for .NET mentions this; it's hidden in the events spec page.

- **`OpenTelemetryChatClient` already implements spec v1.41**, not v1.0 or some outdated
  version. Microsoft's MEF.AI team tracks the upstream spec with unusual fidelity for an
  experimental standard. ember gets this for free just by adding `.UseOpenTelemetry()`.

- **Phoenix's OTLP gRPC port (4317) collides with Jaeger's gRPC port.** Running both on the
  same machine requires remapping one — e.g., Phoenix on 4318/6006, or Jaeger on 4327.
  The demo command `dotnet run -- demo --otlp` hardcodes `localhost:4317`; it would need
  a flag for the Phoenix endpoint.

- **Langfuse v3 OTLP does not support gRPC.** HTTP/protobuf only. This means
  `AddOtlpExporter()` must explicitly use HTTP transport, not the default gRPC transport.
  Silently fails otherwise.

- **The agent-span conventions are actively evolving.** The "Agent Framework Convention" was
  still under active discussion in the OpenTelemetry GenAI SIG as of early 2026. The `invoke_agent`
  operation name and `gen_ai.conversation.id` attribute only appeared in recent spec drafts.
  Adopting them now means accepting potential attribute name churn.

- **Per-stage trace architecture (ADR 7) is actually more eval-friendly than a single long
  trace.** Eval tools that score individual observations prefer short, bounded traces.
  The `plan.session` trace and its `plan.round` children map cleanly to a "session" +
  "generation" hierarchy that Phoenix and Langfuse both understand natively.

---

## Where this uniquely aligns with ember

**ADR 7 is already doing the right thing.** The per-stage trace architecture keeps each
trace short-lived and bounded — exactly what eval platforms expect. Phoenix's data model
maps `plan.session` → trace, `plan.round` → observations (one per critic/planner call pair).
No architectural change is needed; only attribute enrichment.

**ADR 7's recorded future option is worth building now.** The ADR notes: "persist the W3C
trace context on the session row and join stages with span links." The session row already
exists in SQLite (`SessionStore`). Adding two columns — `trace_id` and `span_id` — and
using them to create `ActivityLink` objects when starting downstream stage spans completes
the correlation story. Any OTLP-speaking backend (Jaeger, Phoenix, Langfuse) then shows
linked traces rather than requiring tag-based manual joins.

**`IChatClient` middleware is the clean injection point.** ember's `ChatClientFactory`
builds the `IChatClient` pipeline for both planner and critic. Adding
`.UseOpenTelemetry(enableSensitiveData: false)` and a custom `CostAccountingChatClient`
middleware affects both agents without touching `Planner.cs` or `Critic.cs`. The keyed DI
registrations (`"planner"` / `"critic"`) mean the `gen_ai.agent.name` tag can be set per
registration.

**The TraceDemo already validates the span shape.** `dotnet run -- demo --otlp` can be
retargeted at Phoenix (`--otlp http://localhost:6007/v1/traces` or similar) to visually
validate the enriched span set before any production traffic is sent.

**The critic's structured JSON output is eval-friendly.** `CriticVerdict` already produces
machine-readable `issues` with `severity` and `summary`. This is a natural score source:
`approved = true` → 1.0, count of blocking/major issues → deduction from 1.0. An inline
`CriticScoringClient` wrapper can emit a `gen_ai.evaluation.result` event with the score
immediately after every critic call, with zero additional LLM API calls.

---

## Recommendations

Listed in priority order, with a clear "what NOT to do" section at the end.

### Priority 1 — Add `gen_ai.*` token attributes (days of effort)

In `ChatClientFactory.Create()`, chain `.UseOpenTelemetry()` to both planner and critic
pipelines:

```csharp
return client.GetChatClient(options.Model)
    .AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(configure: c => c.EnableSensitiveData = false)
    .Build();
```

Set `gen_ai.agent.name` via a tag on the wrapping `plan.round` or `plan.session` activity,
or by passing a source name override to `OpenTelemetryChatClient`. This gives Jaeger (and
any future backend) `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`,
`gen_ai.request.model`, and `gen_ai.provider.name` on every LLM call span — the foundation
for all cost and latency analysis.

### Priority 2 — Add inline cost accounting (days of effort)

Write a `CostAccountingChatClient : DelegatingChatClient` that:
- Reads `response.Usage.InputTokenCount` and `response.Usage.OutputTokenCount`
- Looks up current model pricing from a static table in config
- Sets `ember.llm.cost.usd` on the current `Activity`
- Increments an OTel counter `ember.llm.cost.total` with `ember.agent` and `gen_ai.request.model` tags

No new infrastructure. Cost data lands in Jaeger immediately. Add a daily total query to
a simple Markdown runbook so the operator can spot-check spend.

### Priority 3 — Emit inline critic scores as eval events (days of effort)

After each `Critic.ReviewAsync()` call, emit a `gen_ai.evaluation.result` OTel event on
the `plan.round` span:

```csharp
round?.AddEvent(new ActivityEvent("gen_ai.evaluation.result", tags: new ActivityTagsCollection
{
    ["gen_ai.evaluation.name"] = "critic.plan_quality",
    ["gen_ai.evaluation.score.value"] = ComputeScore(verdict),
    ["gen_ai.evaluation.score.label"] = verdict.Approved ? "approved" : "needs_revision",
}));
```

`ComputeScore` maps `Approved=true` → 1.0, `blocking issue count` → deduction. This is
zero-cost (no extra LLM call), deterministic, and gives a per-round score time series in any
OTLP-capable backend.

### Priority 4 — Implement span links for cross-stage correlation (1–2 days)

Add `trace_id` and `span_id` VARCHAR columns to the session SQLite schema. When
`PlanningLoopRunner` starts `plan.session`, record `activity.TraceId` and `activity.SpanId`
in the session row. When `GateService` starts `gate.fire` and `BuilderRunner` starts
`build.run`, load the stored context and add an `ActivityLink`:

```csharp
var link = new ActivityLink(new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded));
using var activity = Telemetry.Activity.StartActivity("gate.fire", ActivityKind.Internal,
    parentContext: default, links: new[] { link });
```

This upgrades the correlation from "search by tag" to first-class linked traces in Jaeger,
Phoenix, and every other backend. Directly implements the "future option" ADR 7 recorded.

### Priority 5 — Add Phoenix alongside Jaeger for eval UI (1–2 days)

Run Phoenix on different ports from Jaeger:

```bash
docker run -p 6007:6006 -p 4318:4317 -i -t arizephoenix/phoenix:latest
```

Add a second OTLP exporter in `Program.cs` (or use an OTel Collector as a fan-out):

```csharp
tracing.AddOtlpExporter("phoenix", o => {
    o.Endpoint = new Uri("http://localhost:4318");
    o.Protocol = OtlpExportProtocol.Grpc;
});
```

Phoenix then provides: span search with LLM-specific filters, dataset curation from
production spans, eval experiment runs, and token/cost dashboards. Jaeger is retained for
timeline and waterfall views that Phoenix's UI is less strong on.

### Priority 6 — Build a golden-set replay runner for regression detection (1 week)

Extend `PlanCli` (already exists as `dotnet run -- plan "<brief>" <repo>`) to accept a
`--golden <file>` flag that loads a JSON file of `{ "brief", "rubric" }` pairs and scores
each with an LLM judge. Output a JSON result file with per-brief scores. Store the baseline
result in `RnD/eval-baselines/`. Run it manually on model changes. Add a GitHub Actions
workflow (or a local PowerShell script) that diffs results against the baseline and fails if
aggregate score drops > 10%.

Scoring rubric for the planner: does the output mention specific files, specific steps, and
acceptance criteria? Scoring for critic: on deliberately thin briefs, does the critic return
at least one blocking issue?

---

### What NOT to do

- **Do not adopt LangSmith or Braintrust.** Both are cloud-only (or effectively so). Every
  prompt and completion would leave the machine. Incompatible with the operator's control
  requirements and unnecessary for a single-pipeline tool.

- **Do not replace Jaeger with Phoenix.** They serve different purposes. Jaeger excels at
  distributed trace waterfall visualization; Phoenix excels at LLM-specific eval and dataset
  management. They are complementary.

- **Do not add continuous online scoring to every production run.** At current volume (one
  `/plan` per engineering intent), it adds LLM cost per run and latency without statistical
  value. Save LLM-as-judge for golden-set replay.

- **Do not emit prompt/completion content in spans by default** (`EnableSensitiveData =
  false`). System prompts are in source control; keeping them out of trace data avoids
  cluttering span storage and potential accidental logging of user briefs. Use opt-in
  `gen_ai.input.messages` events only in a dev/debug mode.

- **Do not adopt OpenLLMetry/Traceloop.** Python-only; no .NET package exists or is
  planned. The `OpenTelemetryChatClient` in `Microsoft.Extensions.AI` provides the same
  value natively.

- **Do not adopt the agent-span conventions (`gen_ai.agent.name`, `invoke_agent`, etc.)
  immediately in production spans.** They are in active flux in the OpenTelemetry GenAI SIG.
  Emit them in the TraceDemo first; wait for the agent convention to reach a stable draft
  before wiring into production code.

---

## References

1. **OpenTelemetry Semantic Conventions — GenAI overview**
   `https://opentelemetry.io/docs/specs/semconv/gen-ai/`
   The canonical hub for all gen_ai.* conventions. Stability status, versioning opt-in, and
   links to sub-pages. All GenAI conventions are "Development" as of May 2026.

2. **OTel GenAI span attributes spec**
   `https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/`
   Full table of span attributes: `gen_ai.operation.name`, `gen_ai.provider.name`,
   `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, etc. Required/Recommended/Opt-In
   levels and data types.

3. **OTel GenAI events spec (including gen_ai.evaluation.result)**
   `https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-events/`
   Defines `gen_ai.client.inference.operation.details` (prompt/response capture) and
   `gen_ai.evaluation.result` (eval score attachment). Both are Development status.

4. **OTel GenAI agent and framework spans**
   `https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-agent-spans/`
   `gen_ai.agent.name`, `gen_ai.agent.id`, `gen_ai.conversation.id`, `invoke_agent`
   operation. Still under active discussion; use cautiously.

5. **OTel GenAI metrics spec**
   `https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/`
   `gen_ai.client.token.usage` (histogram), `gen_ai.client.operation.duration` (histogram).
   Development status. These are the counterpart metrics to span attributes.

6. **OTel blog: AI Agent Observability (2025)**
   `https://opentelemetry.io/blog/2025/ai-agent-observability/`
   Overview of 2025 state: which frameworks are instrumenting natively, the agent framework
   convention discussion, and the GenAI SIG's direction.

7. **Microsoft.Extensions.AI — OpenTelemetryChatClient**
   `https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.opentelemetrychatclient`
   .NET API reference. Confirms spec v1.41 implementation, `EnableSensitiveData` property,
   and `DelegatingChatClient` inheritance model.

8. **Tracking Token Usage with Microsoft.Extensions.AI (Mark Heath, 2025)**
   `https://markheath.net/post/2025/1/11/tracking-token-usage-microsoft-extensions-ai`
   Practical C# guide to `UsageDetails`, `UsageContent` in streaming responses, and the
   `InputTokenCount`/`OutputTokenCount`/`TotalTokenCount` properties.

9. **Arize Phoenix documentation — self-hosting**
   `https://arize.com/docs/phoenix/self-hosting/docker`
   Docker deployment: ports 6006 (UI + OTLP HTTP), 4317 (OTLP gRPC), SQLite default,
   PostgreSQL optional. Single container, no external dependencies required.

10. **Arize Phoenix GitHub repository**
    `https://github.com/Arize-ai/phoenix`
    Source, issue tracker, and releases. Active development as of 2025–2026. MIT license
    confirmed. Evidence of .NET OTLP compatibility via standard OTLP receiver.

11. **Langfuse OpenTelemetry integration**
    `https://langfuse.com/integrations/native/opentelemetry`
    Documents OTLP endpoint (`/api/public/otel`), span→generation mapping rules,
    `gen_ai.*` attribute recognition, and self-hosted configuration (v3.22.0+).

12. **Langfuse Docker Compose self-hosting**
    `https://langfuse.com/self-hosting/deployment/docker-compose`
    Six-container stack (web, worker, postgres, clickhouse, minio, redis). Min 2 CPU, 4 GB
    RAM. Establishes the operational overhead benchmark for the Langfuse path.

13. **Langfuse LLM-as-a-Judge docs**
    `https://langfuse.com/docs/evaluation/evaluation-methods/llm-as-a-judge`
    Online (observation-level) vs offline (experiment) eval modes, score attachment to
    spans, asynchronous evaluation queue. Self-hosted operation documented.

14. **LLM Cost Monitoring with OpenTelemetry (Uptrace blog)**
    `https://uptrace.dev/blog/llm-cost-monitoring`
    Cost formula, custom `llm.cost.usd` attribute convention, OTel counter + histogram
    approach, and multi-stage cost rollup via parent span aggregation.

15. **Braintrust — LLM evaluation metrics guide**
    `https://www.braintrust.dev/articles/llm-evaluation-metrics-guide`
    Used to evaluate Braintrust's feature set (trace-to-dataset, online scoring, CI gates).
    Confirmed cloud-only; excluded from recommendations.

16. **Agent Observability 2026: Evals, Traces, Cost Guide (Digital Applied)**
    `https://www.digitalapplied.com/blog/agent-observability-2026-evals-traces-cost-guide`
    Three-layer eval model (unit/LLM-judge/production sampling), golden-set replay pattern,
    cost attribution via tag propagation, Langfuse recommendation for self-hosted setups.

17. **Semantic Conventions for GenAI Agentic Systems — GitHub issue #2664**
    `https://github.com/open-telemetry/semantic-conventions/issues/2664`
    Active SIG discussion on agent conventions as of 2025. Evidence that the agent-span spec
    is in flux and not yet production-stable.

18. **OpenTelemetry Span Links — OneUptime blog (Jan 2026)**
    `https://oneuptime.com/blog/post/2026-01-07-opentelemetry-span-links/view`
    Practical guide to `ActivityLink` pattern for async/cross-process correlation; directly
    applies to ADR 7's recorded "future option" for linking per-stage traces.
