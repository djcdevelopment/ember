# 1. Discord as the control surface; async, stateful, thread-per-session

- Status: Accepted
- Date: 2026-05-15

## Context

ember is a personal tool for driving development work. Its control surface
could be a CLI, a web app, or a chat platform. A previous bot
(goats-after-dark) established Discord as a comfortable, zero-install
substrate — but that bot is request/response: an interaction triggers a
computation that replies within Discord's limits (a 3-second acknowledgement,
a 15-minute ceiling on deferred replies).

ember's unit of work is a multi-round LLM planning loop followed by a build.
It runs for minutes to hours. It cannot fit inside a single interaction.

## Decision

Discord is the control surface. Each `/plan` invocation becomes a long-lived,
asynchronous **session**, isolated in its own Discord **thread**; the thread
id is the session key. ember is a long-running host process, not a
request/response handler — interactions only acknowledge, and all real work
runs afterward and is posted into the thread. Session state is persisted in
SQLite so sessions survive a restart.

## Consequences

- A familiar, zero-install control surface; threads give per-session isolation
  and a durable conversation log for free.
- Sessions survive process restarts.
- ember must run as an always-on host.
- Work must respect Discord rate limits — post digests, never a message per
  event.
- The 15-minute deferred-reply ceiling stops mattering: ember acknowledges the
  interaction immediately and posts everything else into the thread directly.
