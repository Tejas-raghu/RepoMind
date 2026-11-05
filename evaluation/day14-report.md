# Day 14 — TaskTrail RAG evaluation

- Run (UTC): 2026-11-04 08:47:12
- API: `http://127.0.0.1:5000`; repository: `38` (TaskTrail indexed sample)
- Retrieval: hybrid search `topK: 5`; RAG answer: `topK: 3` (same as the Vue UI).
- Reranking: evaluator uses the API process setting; record that setting when comparing runs.
- Fixture: 12 expected indexed paths, five implementation anchors, and five absence-term checks.

- Total questions: 10; attempted: 10; skipped: 0
- Hit@1: 2/5 supported questions
- Hit@5: 5/5 supported questions
- Grounded supported answers: 3/5
- Correct abstentions: 5/5 unsupported questions
- False acceptances: 0/5 unsupported questions
- Average RAG answer request time: 139310 ms across 10 attempts
- Average retrieval request time: 1299 ms across 5 attempts
- HTTP, timeout, or response-parse failures: 0 question(s)

A retrieval hit requires the exact labeled path, the anchor line within the returned chunk range, and the anchor text in that chunk. HTTP 200 alone is never a hit. A correct abstention requires `insufficient_evidence` with zero sources. `answered` on an unsupported question is a false acceptance; unavailable or uncertain outcomes are failures, not correct abstentions.

| Question | Retrieval rank | Answer status | Labeled source in answer | Search ms | Answer ms | Outcome |
|---|---:|---|---|---:|---:|---|
| Where is the Bearer token added to requests? | 1 | answered | yes | 60 | 25161 | grounded answer |
| ↳ `bearer` | Expected `TaskTrail/src/services/apiClient.ts` L15 | Retrieved: TaskTrail/src/services/apiClient.ts, TaskTrail/README.md, TaskTrail/server/AuthController.cs, TaskTrail/src/services/authService.ts, TaskTrail/src/router/guards.ts | Answer sources: TaskTrail/src/services/apiClient.ts | | | |
| How are protected routes secured? | 2 | insufficient_evidence | no | 58 | 32370 | answer/source miss |
| ↳ `routes` | Expected `TaskTrail/src/router/guards.ts` L6 | Retrieved: TaskTrail/README.md, TaskTrail/src/router/guards.ts, TaskTrail/server/AuthController.cs, TaskTrail/server/AuthController.cs, TaskTrail/src/map/outletMarkers.ts | Answer sources:  | | | |
| Where is backend login implemented? | 2 | answered | yes | 101 | 73900 | grounded answer |
| ↳ `backend-login` | Expected `TaskTrail/server/AuthController.cs` L23 | Retrieved: TaskTrail/src/services/authService.ts, TaskTrail/server/AuthController.cs, TaskTrail/src/components/LoginForm.vue, TaskTrail/src/router/guards.ts, TaskTrail/README.md | Answer sources: TaskTrail/server/AuthController.cs | | | |
| Where are Leaflet markers created? | 1 | generation_unavailable | no | 2103 | 86805 | answer/source miss |
| ↳ `leaflet` | Expected `TaskTrail/src/map/outletMarkers.ts` L20 | Retrieved: TaskTrail/src/map/outletMarkers.ts, TaskTrail/package.json, TaskTrail/README.md, TaskTrail/src/data/sampleTasks.json, TaskTrail/server/AuthController.cs | Answer sources:  | | | |
| How are overdue tasks identified? | 2 | answered | yes | 4174 | 82313 | grounded answer |
| ↳ `overdue` | Expected `TaskTrail/src/services/taskService.ts` L26 | Retrieved: TaskTrail/src/components/TaskBoard.vue, TaskTrail/src/services/taskService.ts, TaskTrail/src/data/sampleTasks.json, TaskTrail/README.md, TaskTrail/src/components/LoginForm.vue | Answer sources: TaskTrail/src/services/taskService.ts | | | |
| Where is Stripe payment processing implemented? | n/a | insufficient_evidence | n/a | — | 48236 | correct abstention |
| Where is Redis caching configured? | n/a | insufficient_evidence | n/a | — | 39443 | correct abstention |
| Where is AWS S3 file upload implemented? | n/a | insufficient_evidence | n/a | — | 911139 | correct abstention |
| Where are GraphQL subscriptions implemented? | n/a | insufficient_evidence | n/a | — | 48316 | correct abstention |
| Where is Kubernetes deployment configured? | n/a | insufficient_evidence | n/a | — | 45416 | correct abstention |

Hit@1 example: if the first of five returned chunks is the labeled implementation chunk, that question scores one hit. If it first appears at rank 3, it scores Hit@5 but not Hit@1. The denominator stays five supported questions; request failures are shown separately and score no hit. Average time uses Stopwatch wall time for every attempted request, including failed requests.

A grounded supported answer here means the answer cited the labeled implementation chunk; this run does not judge every sentence of generated prose. This is a ten-question smoke set for one indexed sample, not a general reliability estimate. The labels confirm absence only from this indexed fixture; ZIP ingestion may have excluded other files.
