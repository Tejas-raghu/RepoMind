# RepoMind — Project Instructions

## Project Vision

RepoMind is an AI-powered Codebase Intelligence platform being developed as a 30-day learning and portfolio project.

The purpose is to gain hands-on experience with LLMs, embeddings, vector databases, semantic search, RAG, multi-agent systems, and tool calling.

This is not a commercial or production-scale project.

## Technology Stack

- Frontend: Vue 3 + TypeScript + Vite
- Backend: ASP.NET Core Web API / C#
- Database: PostgreSQL + pgvector
- LLM runtime: Ollama (local)
- Embedding model: embeddinggemma
- AI generation model: Small local Ollama model, to be selected later
- Agent orchestration: Initially plain C# services
- Containers: Docker Compose

## Constraints

- Free and open-source tools only.
- No paid API keys or services.
- Run AI models locally.
- Keep the architecture simple and modular.
- The developer works full-time and has approximately 45–60 minutes on weekdays.
- Follow the incremental 30-day roadmap.
- Do not implement future features prematurely.

## Development Approach

The primary goal is learning, not generating large amounts of code.

When implementing a feature:
1. Explain the concept briefly.
2. Describe the files being added or modified.
3. Implement the smallest working solution.
4. Explain important code decisions.
5. Show how to test it.
6. Avoid unnecessary dependencies and abstractions.

Do not rewrite unrelated files.

Use dependency injection, async/await, cancellation tokens, and typed DTOs where appropriate.

## Roadmap

### Week 1 — Embeddings and Semantic Search
- Generate text embeddings
- Integrate Ollama with ASP.NET Core
- Configure PostgreSQL and pgvector
- Store embeddings
- Build similarity search
- Ingest a small code repository

### Week 2 — RAG
- Retrieve relevant code chunks
- Generate answers using local LLMs
- Include file and line references
- Build Vue upload and chat UI

### Week 3 — Multi-Agent System
- Retrieval Agent
- Code Analysis Agent
- Review Agent
- Supervisor / Router
- Agent execution traces

### Week 4 — Evaluation and Portfolio
- Hybrid search
- Retrieval evaluation
- Documentation
- Demo recording
- GitHub / LinkedIn presentation

## Current Progress

Day 1: Completed.

The developer:
- Installed Ollama
- Downloaded embeddinggemma
- Generated text embeddings using the Ollama API
- Calculated cosine similarity in PowerShell

Results:
- Authentication vs Login: 0.66566
- Authentication vs Leaflet: 0.34564

Day 2: Completed.

Goal:
Create an ASP.NET Core Web API with an EmbeddingService that calls the local Ollama `/api/embed` endpoint.

Day 3: Completed.

Goal:
Configure local PostgreSQL with pgvector using Docker Compose, and verify that the `vector` extension is enabled.

Day 4: Completed.

Goal:
Store text and its `embeddinggemma` vector in PostgreSQL through the API.

Day 5: Completed.

Goal:
Search stored text by cosine similarity using a query embedding and pgvector.

Day 6: Completed.

Goal:
Upload a small trusted source ZIP and store line-based code chunks with source references.

Day 7: Completed.

Goal:
Search indexed code chunks within one repository using a query embedding and pgvector.

Day 8: Completed.

Goal:
Call a local Ollama chat model (`qwen3:4b`) through the API.

Day 9 / 9.5: Completed.

Goal:
Build RAG context from repository-scoped hybrid retrieval (vector + lexical, RRF), optional LLM reranking, and evidence verification with abstention.

Day 10: Completed.

Goal:
Generate grounded RAG answers with validated source citations via `POST /api/repositories/{repositoryId}/rag/answer`, without calling the generation model when evidence is not supported.

Known limitation: with reranking enabled, local latency makes verification time out; successful live answers were measured with reranking disabled.

Days 11–14.5: Completed (answer quality, Vue dashboard, code explorer, evaluation and Hit@1 ranking, optional Cloudflare provider). See PROGRESS.md.

Week 3 (Days 15–21), multi-agent system: Implemented and verified. It has a rule-based Supervisor/Router, a Retrieval Agent with a model planner and rule fallback, a Code Analysis Agent, a Review Agent, allowlisted read-only tools with budgets, validated line citations, recovery to deterministic output, persisted execution traces (`agent_runs`), a UI workspace, and an agent evaluation mode. See docs/agents.md.

Week 4: Hybrid search and retrieval evaluation are done. Also done: a reproducible TaskTrail fixture, secret redaction during ingestion, HTTP/component/browser test layers, and documentation (docs/). Still open, manual: demo recording and the GitHub/LinkedIn presentation.

Reranking is off by default; enable it with `Reranking__Enabled=true`.

## Important

Implement only the current day's tasks unless explicitly instructed otherwise. Explain AI-related logic so the developer understands the implementation.
