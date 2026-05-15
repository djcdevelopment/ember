# 3. A universal OpenAI-compatible IChatClient

- Status: Accepted
- Date: 2026-05-15

## Context

ember needs model clients for the planner (Claude) and the critic (GPT), and
intends to later run either on a local model via Ollama. Microsoft Agent
Framework ships first-party connectors per provider. Separately,
`Microsoft.Extensions.AI.OpenAI` exposes an `IChatClient` over the OpenAI SDK,
and the OpenAI SDK can target any OpenAI-compatible endpoint — OpenAI itself,
Ollama (`/v1`), and Anthropic, which publishes an OpenAI-compatible endpoint.

## Decision

A single `ChatClientFactory` builds every model client through
`Microsoft.Extensions.AI.OpenAI`, choosing the endpoint from a `provider`
config value (`openai`, `ollama`, `anthropic`) or an explicit `baseUrl`. One
adapter, every provider.

## Consequences

- One code path; less surface area; the loop never sees a provider.
- Switching the planner or critic to a local model is a config change.
- This deviates from PLAN.md's original "first-party MAF connectors" wording.
  It depends on Anthropic's OpenAI-compatibility endpoint; provider-specific
  features beyond chat completion are not reachable through this path. If that
  becomes limiting, a provider can be given its own `IChatClient`
  implementation behind the same factory without touching the loop.
