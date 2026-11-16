# RepoMind completion plan (Days 15–30)

Written after inspecting the code, schema, tests and running services on
2026-11-09. Where the documentation and the code differed, the code was taken as
correct.

## Inventory at the start of this phase

### Completed and verified
- Ollama embeddings (`embeddinggemma`, 768 dimensions), stored text and similarity search (Days 2–5).
- ZIP ingestion, which reads entries without extracting them. It rejects traversal and absolute paths and enforces limits on size, file count, binaries and file names (Day 6).
- Repository-scoped vector search with a cutoff (Day 7) and the local Qwen chat endpoint (Day 8).
- Hybrid vector and lexical retrieval with RRF, optional LLM reranking, evidence verification with abstention, and grounded answers with validated citations (Days 9–11).
- Code-implementation-first ordering after RRF. Hit@1 rose from 2/5 to 5/5 on the TaskTrail labels (Day 14 follow-up).
- Optional Cloudflare Workers AI provider. It writes to a separate `vector(1024)` column and returns 409 for a repository/model mismatch (Day 14.5).
- 141 backend tests, including PostgreSQL integration tests. 4 frontend Node tests and the production build pass.

### Partially complete
- Day 12 dashboard and Day 13 explorer: the API contract is verified, but the in-browser checks never finished (keyboard, citation scrolling, narrow layout).
- Day 14 evaluation: it depends on repository 38. The original TaskTrail ZIP is not in the workspace, so the run can't be reproduced from a clean database.

### Missing (roadmap Weeks 3–4)
- Retrieval Agent, Code Analysis Agent, Review Agent, Supervisor/Router, and agent execution traces.
- Tool calling and controlled tool execution; planning; agent state; recovery.
- Evaluation of multi-agent behavior.
- Frontend for agent results, traces and run history.
- HTTP-level API integration tests, frontend component tests and browser E2E tests.
- Consolidated documentation and a manual sanity checklist.

### Bugs and technical debt found
1. `Reranking:Enabled` is `true` by default, but every recorded local rerank timed out, and the following verifier call then timed out too. The default RAG flow therefore fails locally.
2. Secret filtering looks only at file names. A credential inside an ordinary `.ts` or `.cs` file is indexed and can be sent to a model, including the optional cloud provider.
3. `GET /api/repositories` doesn't expose the embedding provider/model, so the UI can't warn before a 409 mismatch.
4. No test exercises the real HTTP pipeline (routing, model binding, status codes) or rollback of the upload transaction.
5. `Program.cs` repeats the same exception mapping for every endpoint. This is left alone because rewriting it is unrelated to the remaining work.

### Architectural limitations (accepted for this project)
- Uploads are indexed synchronously inside the request, so there is no background queue.
- There is no authentication. The app is for local, single-user use only.
- Search scans each repository exactly, with no approximate (HNSW) index and no lexical index. This is fine at the sample sizes used.
- A local 4B model is slow (tens of seconds per call) and its outputs vary between runs.

## Backlog by phase

| Phase | Work | Depends on |
|---|---|---|
| P1 Reliability and security | Reranking off by default; secret redaction during ingestion; testable upload persistence with a rollback test; provider/model in the repository list; `GET /api/system/info` | — |
| P2 Agent foundation | Structured-JSON model interface (Ollama + Cloudflare); allowlisted, repository-scoped tool executor with budgets; deterministic outline and review-rule tools; run state and trace model; Supervisor router | P1 |
| P3 Agents and orchestration | Retrieval Agent (planner with fallback), Code Analysis Agent, Review Agent, Supervisor orchestration, trace persistence (`agent_runs` migration), run/history endpoints | P2 |
| P4 Backend tests | Unit tests for router, tools, rules, validators and orchestration failures; PostgreSQL integration tests; HTTP integration tests with WebApplicationFactory | P1–P3 |
| P5 Frontend | Agent workspace, findings with citation navigation, plan/trace view, run history, provider badges; component tests | P3 |
| P6 Evaluation | Reconstructed TaskTrail fixture; agent evaluation fixture and evaluator mode | P3 |
| P7 End-to-end | Live stack run, browser E2E, Day 14 evaluator and agent evaluator compared with the baseline | P4–P6 |
| P8 Documentation | README quick start, architecture, agent concepts, PROGRESS, manual checklist | all |

Demo recording and the GitHub/LinkedIn presentation are manual tasks for the
developer. They are listed in the checklist, not marked complete here.

## Status at completion (2026-11-10)

| Phase | Status | Evidence |
|---|---|---|
| P1 Reliability and security | Done | Redaction, rollback and cancellation tests; reranking off; verifier 120 s; generation budget fix |
| P2 Agent foundation | Done | `IAgentModel`, `AgentToolbox`, `AgentRouter`, unit and DB tests |
| P3 Agents and orchestration | Done | Supervisor, three agents, `agent_runs`, endpoints; agent evaluation 10/10 |
| P4 Backend tests | Done | 217/217 passing (unit, PostgreSQL integration, HTTP integration) |
| P5 Frontend | Done | Agent workspace, provider badge; 5 + 14 tests; build passes |
| P6 Evaluation | Done | Fixture byte-identical to repository 38; RAG Hit@1 5/5, grounded 5/5, abstentions 5/5 |
| P7 End-to-end | Done | Playwright 8/8 against the live stack |
| P8 Documentation | Done | README, architecture, agents, testing, checklist, PROGRESS |
| Demo, GitHub/LinkedIn | Open (manual) | — |
