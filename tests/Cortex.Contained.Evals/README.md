# Cortex.Contained.Evals

Live-LLM evaluation suites for the memory system. They call a real LLM provider and a real
Ollama embedding service, so they are excluded from the default test gate
(`Self-Update.ps1 -IncludeEvals` adds them).

## Configuration

| Setting | Purpose |
|---|---|
| `EVAL_LLM_API_KEY` (+ `EVAL_LLM_MODEL`, `EVAL_LLM_ENDPOINT`) | LLM provider credentials, separate from production |
| `EVAL_OLLAMA_ENDPOINT` | Ollama endpoint used for embeddings |

`EVAL_OLLAMA_ENDPOINT` is required unless an Ollama is already listening on
`http://localhost:11434`. The fixture fails fast with an explanatory message otherwise.
The embeddings container of a normal install publishes no port, so one of these is needed:

1. **Compose override (local eval runs only).** Publish the embedder on loopback, run the evals, stop it:

   ```powershell
   docker compose -f docker-compose.yml -f docker-compose.eval.yml --profile memory up -d embeddings
   $env:EVAL_OLLAMA_ENDPOINT = "http://127.0.0.1:11434"
   dotnet test tests/Cortex.Contained.Evals
   docker compose --profile memory stop embeddings
   ```

2. **Host Ollama.** Install Ollama on the host, `ollama pull qwen3-embedding:0.6b`, and either leave
   it on its default port or point `EVAL_OLLAMA_ENDPOINT` at it.

The embedding model and dimensions can be overridden with `Eval:Ollama:EmbeddingModel` /
`Eval:Ollama:EmbeddingDimensions` in `appsettings.eval.local.json`.
