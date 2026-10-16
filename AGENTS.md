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

## Important

Implement only the current day's tasks unless explicitly instructed otherwise. Explain AI-related logic so the developer understands the implementation.
