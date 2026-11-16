# Day 14 — TaskTrail RAG evaluation

- Run (UTC): 2026-11-09 18:04:38
- API: `http://127.0.0.1:5100`; repository: `843` (TaskTrail indexed sample)
- Retrieval: hybrid search `topK: 5`; RAG answer: `topK: 3` (same as the Vue UI).
- Reranking: evaluator uses the API process setting; record that setting when comparing runs.
- Fixture: 12 expected indexed paths, five implementation anchors, and five absence-term checks.

- Total questions: 10; attempted: 10; skipped: 0
- Hit@1: 5/5 supported questions
- Hit@5: 5/5 supported questions
- Grounded supported answers: 5/5
- Correct abstentions: 5/5 unsupported questions
- False acceptances: 0/5 unsupported questions
- Average RAG answer request time: 54205 ms across 10 attempts
- Average retrieval request time: 1071 ms across 5 attempts
- HTTP, timeout, or response-parse failures: 0 question(s)

A retrieval hit requires the exact labeled path, the anchor line within the returned chunk range, and the anchor text in that chunk. HTTP 200 alone is never a hit. A correct abstention requires `insufficient_evidence` with zero sources. `answered` on an unsupported question is a false acceptance; unavailable or uncertain outcomes are failures, not correct abstentions.

| Question | Retrieval rank | Answer status | Labeled source in answer | Search ms | Answer ms | Outcome |
|---|---:|---|---|---:|---:|---|
| Where is the Bearer token added to requests? | 1 | answered | yes | 157 | 29649 | grounded answer |
| ↳ `bearer` | Expected `TaskTrail/src/services/apiClient.ts` L15 | Retrieved: TaskTrail/src/services/apiClient.ts, TaskTrail/server/AuthController.cs, TaskTrail/src/services/authService.ts, TaskTrail/src/router/guards.ts, TaskTrail/README.md | Answer sources: TaskTrail/src/services/apiClient.ts | | | |
| How are protected routes secured? | 1 | answered | yes | 207 | 39696 | grounded answer |
| ↳ `routes` | Expected `TaskTrail/src/router/guards.ts` L6 | Retrieved: TaskTrail/src/router/guards.ts, TaskTrail/server/AuthController.cs, TaskTrail/server/AuthController.cs, TaskTrail/src/map/outletMarkers.ts, TaskTrail/README.md | Answer sources: TaskTrail/src/router/guards.ts | | | |
| Where is backend login implemented? | 1 | answered | yes | 320 | 55559 | grounded answer |
| ↳ `backend-login` | Expected `TaskTrail/server/AuthController.cs` L23 | Retrieved: TaskTrail/server/AuthController.cs, TaskTrail/src/components/LoginForm.vue, TaskTrail/src/services/authService.ts, TaskTrail/src/router/guards.ts, TaskTrail/README.md | Answer sources: TaskTrail/server/AuthController.cs | | | |
| Where are Leaflet markers created? | 1 | answered | yes | 2514 | 95786 | grounded answer |
| ↳ `leaflet` | Expected `TaskTrail/src/map/outletMarkers.ts` L20 | Retrieved: TaskTrail/src/map/outletMarkers.ts, TaskTrail/server/AuthController.cs, TaskTrail/package.json, TaskTrail/README.md, TaskTrail/src/data/sampleTasks.json | Answer sources: TaskTrail/src/map/outletMarkers.ts | | | |
| How are overdue tasks identified? | 1 | answered | yes | 2155 | 102124 | grounded answer |
| ↳ `overdue` | Expected `TaskTrail/src/services/taskService.ts` L26 | Retrieved: TaskTrail/src/services/taskService.ts, TaskTrail/src/components/TaskBoard.vue, TaskTrail/src/components/LoginForm.vue, TaskTrail/src/data/sampleTasks.json, TaskTrail/README.md | Answer sources: TaskTrail/src/services/taskService.ts | | | |
| Where is Stripe payment processing implemented? | n/a | insufficient_evidence | n/a | — | 61019 | correct abstention |
| Where is Redis caching configured? | n/a | insufficient_evidence | n/a | — | 26286 | correct abstention |
| Where is AWS S3 file upload implemented? | n/a | insufficient_evidence | n/a | — | 45610 | correct abstention |
| Where are GraphQL subscriptions implemented? | n/a | insufficient_evidence | n/a | — | 46860 | correct abstention |
| Where is Kubernetes deployment configured? | n/a | insufficient_evidence | n/a | — | 39456 | correct abstention |

Hit@1 example: if the first of five returned chunks is the labeled implementation chunk, that question scores one hit. If it first appears at rank 3, it scores Hit@5 but not Hit@1. The denominator stays five supported questions; request failures are shown separately and score no hit. Average time uses Stopwatch wall time for every attempted request, including failed requests.

A grounded supported answer here means the answer cited the labeled implementation chunk; this run does not judge every sentence of generated prose. This is a ten-question smoke set for one indexed sample, not a general reliability estimate. The labels confirm absence only from this indexed fixture; ZIP ingestion may have excluded other files.
