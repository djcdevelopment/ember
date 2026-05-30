# Architecture Decision Records

Significant architecture decisions for ember — one file per decision,
Nygard-style (Context / Decision / Consequences).

| # | Decision |
|---|---|
| [1](0001-discord-control-surface.md) | Discord as the control surface; async, stateful, thread-per-session |
| [2](0002-csharp-dotnet-agent-framework.md) | C# / .NET on Microsoft Agent Framework |
| [3](0003-openai-compatible-ichatclient.md) | A universal OpenAI-compatible IChatClient |
| [4](0004-builder-headless-claude-code.md) | The builder is headless Claude Code, driven via its CLI |
| [5](0005-critic-driven-loop-and-soft-gate.md) | Critic-driven loop termination and a resumable soft gate |
| [6](0006-global-fifo-build-queue.md) | A global FIFO build queue as a safety throttle |
| [7](0007-per-stage-traces.md) | Per-stage traces, not one long-lived session span |
| [8](0008-boot-recovery-fails-closed.md) | Boot recovery fails interrupted work closed |
| [9](0009-local-inference-on-intel-arc.md) | Local inference on the Intel Arc hardware uses vLLM, not Ollama |
| [10](0010-rehearse-local-loop-on-nvidia.md) | Rehearse the local planning loop on the NVIDIA card before the Arc hardware |
| [11](0011-manifest-as-planner-context.md) | Constellation manifest as round-1 planner context |
