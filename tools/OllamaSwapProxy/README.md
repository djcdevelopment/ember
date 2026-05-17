# OllamaSwapProxy

A sole-residency model-swapper between ember and Ollama (ember RnD / ADR 10).

```
ember  ->  OllamaSwapProxy (:11500/v1)  ->  Ollama (:11434)
```

The planning loop is sequential, so only one model is ever needed at once. This
proxy makes that explicit: before each `/v1/chat/completions` it evicts every
other model from Ollama, loads the requested one, times the load, and records
the GPU/CPU split — from Ollama's `/api/ps` and from `nvidia-smi`. A round that
fails with a model alone on the card is a *model* problem; one that only fails
under contention is an *environment* problem. Every other path is a transparent
pass-through.

## Run

```
dotnet run --project tools/OllamaSwapProxy
```

Listens on `http://localhost:11500`. Environment overrides:

- `SWAP_PROXY_PORT` — listen port (default `11500`)
- `OLLAMA_BASE` — upstream Ollama (default `http://localhost:11434`)
- `SWAP_LOG` — run-log path (default `swaps.jsonl` beside this project)

Point ember at it — `src/Ember/appsettings.Development.json`, both models:

```json
"BaseUrl": "http://localhost:11500/v1"
```

Optional belt-and-suspenders: set `OLLAMA_MAX_LOADED_MODELS=1` on the Ollama
service so nothing can co-load a second model behind the proxy's back.

## Live log (stdout)

```
[swap] request: model=gpt-oss:20b
[swap]   evicted qwen3:8b
[swap]   loaded gpt-oss:20b in 13.0s — 13.6 GB total, 9.1 GB VRAM (67% GPU)
[swap]   nvidia-smi: 11240 MiB used, 1032 MiB free, 64% util
[swap]   <- 200 in 204.9s  (4180 prompt + 612 completion tok)
```

## Run log (`swaps.jsonl`)

One JSON object per `/v1/chat/completions`, appended — durable and comparable
across runs (gitignored; `git add -f` to bank a snapshot). Fields:

```
ts, model, evicted[], load_s
size_gb, vram_gb, gpu_pct                  -- Ollama /api/ps: the model's footprint
smi_used_mib, smi_free_mib, smi_util_pct   -- nvidia-smi: whole-GPU ground truth
generate_s, status
prompt_tokens, completion_tokens, total_tokens
```

`gpu_pct` below 100 means the model is partly on CPU. `smi_free_mib` near zero
with another process holding VRAM is an environment choke, not a model fault.
Token counts catch a bloated prompt or a truncated response across loop rounds.
