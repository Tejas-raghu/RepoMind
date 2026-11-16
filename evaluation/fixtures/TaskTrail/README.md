# TaskTrail — RepoMind Test Repository

TaskTrail is a fictional Vue 3 + TypeScript task-management application. This is a **safe synthetic codebase** created for practicing repository ingestion, semantic search and RAG. Nothing connects to a real company or service.

## Architecture
- `src/services/authService.ts` handles sign-in, session storage and sign-out.
- `src/router/guards.ts` protects routes that require a signed-in user.
- `src/services/apiClient.ts` attaches a bearer token and handles unauthorized responses.
- `src/services/taskService.ts` loads, creates and completes tasks.
- `src/components/LoginForm.vue` collects login credentials.
- `src/components/TaskBoard.vue` lists pending tasks.
- `src/utils/dateUtils.ts` formats deadlines.
- `src/map/outletMarkers.ts` builds Leaflet markers for outlet locations.
- `server/AuthController.cs` is illustrative C# backend login and token validation code.

## Test questions
1. Where are user credentials sent when someone signs in?
2. Which code blocks unauthenticated users from protected pages?
3. Where is the Authorization bearer header added?
4. What happens after the API responds with HTTP 401?
5. How are overdue tasks identified?
6. Where are map markers created from latitude and longitude?
7. What function completes a task?
8. Where does the API validate JWTs?
9. Is there payment processing in this repository? (Expected: no supporting code.)

## Notes
- Sample API URLs and tokens are fictional.
- This repository is for reading and indexing, not deployment.
- Do not execute unknown ZIP contents. RepoMind should read source as untrusted data.
