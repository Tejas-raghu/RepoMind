# RepoMind architecture

RepoMind indexes an uploaded source ZIP, answers questions about it with
citations to exact lines, and runs read-only agents that explain or review
code. Everything runs locally by default: Vue in the browser, an ASP.NET Core
API, PostgreSQL with pgvector, and Ollama models. Cloudflare Workers AI is an
optional second AI provider.

```
Browser (Vue 3 + Vite)
  │  /api  (Vite dev proxy)
  ▼
ASP.NET Core minimal API  ── Program.cs: validation, status codes, DI
  ├─ Ingestion     RepositoryZipReader → SecretRedactor → CodeChunker → IEmbeddingService → PostgreSQL (one transaction)
  ├─ Retrieval     HybridSearchService: vector + lexical candidates → RRF → implementation-first ordering → optional reranker
  ├─ RAG           RagEvidenceVerifier (verify chunk IDs) → RagGenerationPromptBuilder → ILlmService → citation validation
  ├─ Explorer      RepositorySourceService: indexed file lines with gap/conflict markers
  └─ Agents        AgentSupervisor → AgentRouter → RetrievalAgent / CodeAnalysisAgent / ReviewAgent
                   AgentToolbox (allowlisted tools) · IAgentModel (structured JSON) · AgentRunStore (traces)
  ▼                                   ▼
PostgreSQL 17 + pgvector 0.8      Ollama (embeddinggemma, qwen3:4b)  or  Cloudflare Workers AI (optional)
```

## Backend components

| Area | Types | Responsibility |
|---|---|---|
| Ingestion | `RepositoryZipReader`, `SecretRedactor`, `CodeChunker`, `RepositoryUploadService` | Read the ZIP in memory without extracting it. Reject traversal paths; skip binary, hidden, generated, oversized and secret-named files. Redact credential values on the same line, split into 60-line windows with 10 lines of overlap, embed each distinct text once, and insert the repository and all chunks in one transaction. |
| Providers | `IEmbeddingService`, `ILlmService`, `IRerankingService`, `IEvidenceVerificationService`, `IAgentModel` | Ollama implementations by default; Cloudflare implementations when `Ai:Provider=cloudflare`. Choosing happens once, in `Program.cs`. |
| Retrieval | `RepositorySearchStore`, `HybridSearchService` | Every query is scoped to repository ID and embedding model. Vector and lexical candidate lists are combined with Reciprocal Rank Fusion, then code is ranked before documentation for implementation questions. |
| RAG | `RagEvidenceVerifier`, `RagAnswerService`, `RagGenerationPromptBuilder`, `GroundedOutputParser` | Generate only after the verifier accepts specific chunk IDs. The answer may cite only included chunk IDs; paths and lines come from the database. |
| Explorer | `RepositorySourceService` | Rebuild a file view from stored chunks. It never reads the server file system. |
| Agents | `AgentSupervisor`, `AgentRouter`, `RetrievalAgent`, `CodeAnalysisAgent`, `ReviewAgent`, `AgentToolbox`, `AgentSynthesis`, `AgentRunStore` | See [agents.md](agents.md). |

## Data model

| Table | Purpose | Migration |
|---|---|---|
| `stored_embeddings` | Day 4–5 text embedding demo | `init.sql`, `0145_cloudflare_embeddings.sql` |
| `repositories` | One row per upload, with `embedding_provider` and `embedding_model` | `006_repository_upload.sql`, `0145_…` |
| `repository_chunks` | Path, original line range, text, model, and exactly one vector: `embedding vector(768)` for Ollama or `embedding_cf vector(1024)` for Cloudflare | `006_…`, `007_repository_search.sql`, `0145_…` |
| `agent_runs` | Each agent run's complete response (route, plan, trace, findings, evidence) as JSONB, scoped to a repository | `015_agent_runs.sql` |

Every migration is additive and safe to run more than once. Existing 768-dimensional
data is never converted or deleted. A repository can only be queried by the
provider/model that indexed it; a mismatch returns HTTP 409.

## HTTP API

| Method and route | Purpose |
|---|---|
| `GET /api/system/info` | Active provider, embedding and chat models, reranking and planner flags (no secrets) |
| `POST /api/embeddings`, `/api/embeddings/store`, `/api/embeddings/search` | Day 2–5 learning endpoints |
| `POST /api/llm/chat` | Day 8 direct chat |
| `GET /api/repositories` | Completed repositories with `embeddingProvider` and `embeddingModel` |
| `POST /api/repositories/upload` | Multipart field `file`; returns counts and `secretsRedacted` |
| `GET /api/repositories/{id}/files`, `/files/content?path=` | Indexed file list and line view |
| `POST /api/repositories/{id}/search` | Day 7 vector search with cutoff |
| `POST /api/repositories/{id}/hybrid/search` | Retrieval diagnostics (ranks and RRF scores) |
| `POST /api/repositories/{id}/rag/context`, `/rag/verify`, `/rag/answer` | RAG pipeline stages |
| `POST /api/repositories/{id}/agents/run` | `{ task }` → routed multi-agent run with findings, evidence, plan and trace |
| `GET /api/repositories/{id}/agents/runs[?limit=]` | Run history for that repository (up to 50) |
| `GET /api/repositories/{id}/agents/runs/{runId}` | One stored run; 404 when the run belongs to another repository |

Status codes are consistent across endpoints:

| Code | Meaning |
|---|---|
| 400 | Invalid input, or a ZIP that is unsafe or contains no supported files |
| 404 | Unknown repository, file or run |
| 409 | Embedding model mismatch, or invalid stored data |
| 413 | Upload or file view too large |
| 415 | Not a multipart ZIP upload |
| 429 | Cloudflare quota exhausted |
| 502 | AI provider error |
| 503 | PostgreSQL unavailable or a migration missing |
| 504 | Timeout |

Changes since Day 14 are additive: new endpoints and new response fields only.

## Frontend

`App.vue` owns all request state and cancels stale requests with `AbortController`.
Every HTTP call goes through `services/repomindApi.ts`, which validates each
response shape before the UI shows it. Components:

- `RepositoryPanel` (selector with provider badge and mismatch warning, upload)
- `AnswerPanel` (question, status, sources)
- `AgentPanel` / `AgentResult` (task, findings with citation buttons, evidence, plan and trace table, run history)
- `RepositoryFileTree` / `CodeViewer` (explorer with cited-line highlighting)

Repository text is always rendered as escaped text.

## Configuration (`appsettings.json`, override with environment variables such as `Agents__ModelPlanning=false`)

| Section | Defaults and notes |
|---|---|
| `Ai:Provider` | `ollama` (default) or `cloudflare` |
| `Cloudflare:ChatModel` | Default `@cf/openai/gpt-oss-20b`. Workers AI retires models (the original Qwen3 30B chat model began returning HTTP 410 on 2026-11-20), so the model name is configuration, not code. |
| `Reranking:Enabled` | `false`. Local LLM reranking timed out in every recorded run and caused verification to fail too, so it is opt-in. |
| `EvidenceVerification:TimeoutSeconds` | `120`. The measured average verifier time was about 50 s, so the previous 60 s limit failed often. |
| `RagGeneration:MaxOutputTokens` | `512` (was 300), together with `maxLength` caps on the summary and explanation in the generation schema. Uncapped output was cut off into invalid JSON and reported as `generation_unavailable`. |
| `Agents` | Run deadline 420 s, model step 240 s, planner 60 s, 10 tool calls, 6,000 evidence characters, 150 lines per file read, model planning on |
