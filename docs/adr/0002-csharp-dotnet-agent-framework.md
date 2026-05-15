# 2. C# / .NET on Microsoft Agent Framework

- Status: Accepted
- Date: 2026-05-15

## Context

The initial plan targeted Node / TypeScript, reusing the goats-after-dark
bot's skeleton. The operator works across Node and .NET and asked about
Microsoft Agent Framework (MAF) and OpenTelemetry.

MAF reached 1.0 GA in April 2026. It is built on `Microsoft.Extensions.AI`
(the `IChatClient` abstraction) and emits OpenTelemetry traces natively. The
Discord client library for .NET, Discord.Net, is mature. The session design
— state machine, schema, phases — is language-neutral.

## Decision

Build ember in C# / .NET 9: Generic Host, Discord.Net for the bot,
`Microsoft.Extensions.AI` for the planner and critic, and OpenTelemetry for
tracing. The conceptual design carries over from the Node plan unchanged.

## Consequences

- Native OpenTelemetry; a provider-agnostic model abstraction (`IChatClient`);
  the operator's primary stack.
- The goats-after-dark Node skeleton is not reused — ember is a fresh project.
- The builder (Claude Code) has no C# SDK and is therefore driven via its CLI
  (see ADR 4).
