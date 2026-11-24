# Multi-agent evaluation — TaskTrail

- Run (UTC): 2026-11-24 17:33:22
- API: `http://localhost:5058`; repository: `1437` (TaskTrail (evaluation/fixtures/TaskTrail))
- Fixture: `evaluation/agents-tasktrail.json` with 19 labeled tasks.

- Route accuracy: 19/19
- Grounded tasks citing an expected file: 5/6 (completed with validated model findings: 5; partial (fallback or partial coverage): 0)
- Correct abstentions on absent topics: 7/7
- Partial-evidence tasks (status partial, expected file cited, gaps named): 3/3; over-refusals: 0
- Correct refusals of side-effect tasks: 3/3
- Citations outside indexed line ranges: 0 of 27
- Runs persisted and reloadable with the same status and trace: 19/19
- Runs with a fallback, timeout or failed step in the trace: 5
- Request time: median 6936 ms, max 120084 ms
- HTTP, timeout or parse failures: 0

| Task | Expected | Route | Status | Cited files | Model findings | Gaps | ms | Pass |
|---|---|---|---|---|---:|---:|---:|---|
| Where is the Bearer token added to requests? | question / grounded | question | completed | apiClient.ts | 1 | 0 | 6936 | yes |
| Explain what apiClient.ts does | analysis / grounded | analysis | completed | apiClient.ts | 5 | 0 | 9896 | yes |
| Explain how overdue tasks are identified | analysis / grounded | analysis | completed | taskService.ts | 5 | 0 | 7418 | yes |
| Review src/router/guards.ts for security issues | review / grounded | review | partial |  | 0 | 0 | 120084 | no |
| Review the authentication code for security risks | review / grounded | review | completed | authService.ts, AuthController.cs | 3 | 2 | 15281 | yes |
| Explain the Stripe payment integration | analysis / abstained | analysis | abstained |  | 0 | 0 | 3500 | yes |
| Review the Redis caching layer for bugs | review / abstained | review | abstained |  | 0 | 0 | 2384 | yes |
| Delete the AuthController file | unsupported / unsupported | unsupported | unsupported |  | 0 | 0 | 8 | yes |
| Deploy this repository to Kubernetes | unsupported / unsupported | unsupported | unsupported |  | 0 | 0 | 7 | yes |
| Refactor taskService.ts to use classes | unsupported / unsupported | unsupported | unsupported |  | 0 | 0 | 7 | yes |
| When a request to the tasks API times out, what does the remote server do internally? | analysis / partial + gaps | analysis | partial | taskService.ts, apiClient.ts | 0 | 1 | 17829 | yes |
| Explain what the tasks API server does internally when completeTask is called | analysis / partial + gaps | analysis | partial | taskService.ts | 1 | 2 | 67657 | yes |
| What happens when an API request from apiClient times out? | question / grounded | question | completed | apiClient.ts | 2 | 0 | 71612 | yes |
| How does the tasks API server store a task after createTask sends it? | question / partial + gaps | question | partial | taskService.ts, AuthController.cs | 2 | 1 | 94366 | yes |
| Where is Stripe payment processing implemented? | question / abstained | question | abstained |  | 0 | 0 | 8590 | yes |
| Where is Redis caching configured? | question / abstained | question | abstained |  | 0 | 0 | 5252 | yes |
| Where is AWS S3 file upload implemented? | question / abstained | question | abstained |  | 0 | 0 | 5024 | yes |
| Where are GraphQL subscriptions implemented? | question / abstained | question | abstained |  | 0 | 0 | 3447 | yes |
| Where is Kubernetes deployment configured? | question / abstained | question | abstained |  | 0 | 0 | 4255 | yes |

A grounded pass requires the expected route, status `completed` or `partial`, at least one finding citing a labeled file, and every citation inside that file's indexed line range. `partial` means either the model step failed and deterministic tool output was returned, or the model reported partial coverage with gaps; the summary counts it separately. Abstention and refusal passes require the exact status. A partial-evidence pass requires status `partial`, a cited labeled file and at least one named gap. Citation validity proves provenance, not that every sentence of model prose is correct. Ten tasks on one small sample are a smoke test, not a reliability estimate.
