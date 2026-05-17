# OllamaSwapProxy

A sole-residency model-swapper between ember and Ollama (ember RnD / ADR 10).

```
ember  ->  OllamaSwapProxy (:11500/v1)  ->  Ollama (:11434)
```

The planning loop is sequential, so only one model is ever needed at once. This
proxy makes that explicit: before each `/v1/chat/completions` it evicts every
other model from Ollama, loads the requested one, times the load, and logs the
GPU/CPU split from `/api/ps`. A round that fails with a model alone on the card
is a *model* problem; one that only fails under contention is an *environment*
problem. Every other path is a transparent pass-through.

## Run

```
dotnet run --project tools/OllamaSwapProxy
```

Listens on `http://localhost:11500`. Environment overrides:

- `SWAP_PROXY_PORT` — listen port (default `11500`)
- `OLLAMA_BASE` — upstream Ollama (default `http://localhost:11434`)

Point ember at it — `src/Ember/appsettings.Development.json`, both models:

```json
"BaseUrl": "http://localhost:11500/v1"
```

Optional belt-and-suspenders: set `OLLAMA_MAX_LOADED_MODELS=1` on the Ollama
service so nothing can co-load a second model behind the proxy's back.

## Reading the log

Each swap prints, to stdout:

```
[swap] request: model=gpt-oss:20b
[swap]   evicted qwen3:8b
[swap]   loaded gpt-oss:20b in 47.3s — 12.8 GB total, 9.1 GB VRAM (71% GPU)
[swap]   <- 200 in 240.6s
```

The `% GPU` figure is the attribution signal: a model that runs well below
100% GPU is being throttled by the environment, not by its own capability.
