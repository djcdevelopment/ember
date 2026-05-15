# 4. The builder is headless Claude Code, driven via its CLI

- Status: Accepted
- Date: 2026-05-15

## Context

Once a plan converges, a builder must implement it. Three options were weighed:

1. A hand-rolled coding agent (MAF plus file and shell tools).
2. The Claude Agent SDK.
3. Claude Code, the existing agentic coding tool.

Claude Code is a far stronger coding agent than anything hand-rolled. The
Claude Agent SDK has no C# binding. The Claude Code CLI is language-neutral
and has a headless mode: `claude -p --output-format stream-json`.

## Decision

The builder is headless Claude Code. ember launches it as a child process
inside a dedicated git worktree and parses its stream-json output. ember owns
all git remote operations — commit, push, PR — and the builder is given no
push credentials; it works only inside the worktree.

## Consequences

- A best-in-class builder, language-neutral, with a clean trust boundary:
  ember owns the remote, the builder cannot publish.
- A subprocess plus stream parsing, rather than typed SDK events.
- The host must have Claude Code installed and authenticated.
- Implemented in Phase 2; Phase 1 ends at a gate stub.
