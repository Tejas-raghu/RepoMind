# Multi-agent evaluation — TaskTrail

- Run (UTC): 2026-11-20 11:33:23
- API: `http://127.0.0.1:5200`; repository: `1246` (TaskTrail (evaluation/fixtures/TaskTrail))
- Fixture: `evaluation/agents-tasktrail.json` with 10 labeled tasks.

- Route accuracy: 10/10
- Grounded tasks citing an expected file: 5/5 (completed with validated model findings: 5; partial deterministic fallback: 0)
- Correct abstentions on absent topics: 2/2
- Correct refusals of side-effect tasks: 3/3
- Citations outside indexed line ranges: 0 of 13
- Runs persisted and reloadable with the same status and trace: 10/10
- Runs with a fallback, timeout or failed step in the trace: 0
- Request time: median 5214 ms, max 51298 ms
- HTTP, timeout or parse failures: 0

| Task | Expected | Route | Status | Cited files | Model findings | ms | Pass |
|---|---|---|---|---|---:|---:|---|
| Where is the Bearer token added to requests? | question / grounded | question | completed | apiClient.ts | 1 | 4897 | yes |
| Explain what apiClient.ts does | analysis / grounded | analysis | completed | apiClient.ts | 5 | 5849 | yes |
| Explain how overdue tasks are identified | analysis / grounded | analysis | completed | taskService.ts | 1 | 5214 | yes |
| Review src/router/guards.ts for security issues | review / grounded | review | completed | guards.ts | 3 | 51298 | yes |
| Review the authentication code for security risks | review / grounded | review | completed | authService.ts, AuthController.cs | 3 | 17680 | yes |
| Explain the Stripe payment integration | analysis / abstained | analysis | abstained |  | 0 | 5949 | yes |
| Review the Redis caching layer for bugs | review / abstained | review | abstained |  | 0 | 1723 | yes |
| Delete the AuthController file | unsupported / unsupported | unsupported | unsupported |  | 0 | 4 | yes |
| Deploy this repository to Kubernetes | unsupported / unsupported | unsupported | unsupported |  | 0 | 4 | yes |
| Refactor taskService.ts to use classes | unsupported / unsupported | unsupported | unsupported |  | 0 | 4 | yes |

A grounded pass requires the expected route, status `completed` or `partial`, at least one finding citing a labeled file, and every citation inside that file's indexed line range. `partial` means the model step failed and deterministic tool output was returned instead; the summary counts it separately. Abstention and refusal passes require the exact status. Citation validity proves provenance, not that every sentence of model prose is correct. Ten tasks on one small sample are a smoke test, not a reliability estimate.
