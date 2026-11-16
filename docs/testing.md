# Testing RepoMind

| Layer | What is real | What is faked | Command |
|---|---|---|---|
| Backend unit | Business logic: chunking, ZIP rules, redaction, RRF, routing, outline/rule tools, planner and synthesis validation, provider parsing | HTTP handlers for provider responses | `dotnet test tests/RepoMind.Api.Tests` |
| Backend DB integration | PostgreSQL + pgvector, SQL, transactions, repository isolation, agent tools, orchestration and trace storage | Embedding and model providers (deterministic) | same command (needs `docker compose up -d db`) |
| Backend HTTP integration (`ApiIntegrationTests`) | The full ASP.NET Core pipeline through `WebApplicationFactory`, plus PostgreSQL | AI providers only | same command |
| Frontend API contract | Response validation in `repomindApi.ts` | `fetch` | `cd frontend; npm test` |
| Frontend components and app (`tests/unit`) | Vue components, `App.vue` state, cancellation, citation navigation | HTTP backend, syntax highlighter | `cd frontend; npm test` |
| Browser E2E (`tests/e2e`) | Chrome → Vite → API → PostgreSQL → Ollama. Nothing is mocked. | — | see below |
| Evaluation | Live retrieval, RAG and agents on the labeled TaskTrail fixture | — | see below |

The database tests use the local development database and remove every row
they create. Set `REPOMIND_TEST_CONNECTION` to use another database. The HTTP
tests replace only the AI provider registrations; every other service, route
and SQL statement is the production code.

## Backend

```powershell
docker compose up -d db
dotnet test tests/RepoMind.Api.Tests/RepoMind.Api.Tests.csproj
```

If a running API process locks the build output, build into a separate folder:
`dotnet test tests/RepoMind.Api.Tests --artifacts-path $env:TEMP\repomind-test`.

## Frontend

```powershell
cd frontend
npm install
npm test        # Node API-contract tests + Vitest component/app tests
npm run build   # vue-tsc type check + production bundle
```

## Browser end-to-end (live models)

Start PostgreSQL, Ollama and the API. Then create the fixture ZIP and run
Playwright with the installed Chrome:

```powershell
docker compose up -d db
dotnet run --project backend/RepoMind.Api --no-launch-profile --urls http://127.0.0.1:5000
# second terminal, repository root:
Compress-Archive -Path evaluation/fixtures/TaskTrail -DestinationPath $env:TEMP\TaskTrail-e2e.zip -Force
cd frontend
$env:E2E_ZIP = "$env:TEMP\TaskTrail-e2e.zip"
$env:E2E_API_URL = 'http://127.0.0.1:5000'
npm run test:e2e
```

The test starts its own Vite server on port 5174 (`E2E_PORT`) and uploads one
new repository per run. Use `E2E_BROWSER_CHANNEL=msedge` for Edge. Model steps
can take minutes on CPU.

## Evaluation

```powershell
Compress-Archive -Path evaluation/fixtures/TaskTrail -DestinationPath $env:TEMP\TaskTrail.zip -Force
curl.exe -F "file=@$env:TEMP\TaskTrail.zip" http://127.0.0.1:5000/api/repositories/upload
# use the returned repositoryId:
dotnet run --project evaluation/RepoMind.Evaluation -- --base-url http://127.0.0.1:5000 --repository-id <id> --report evaluation/final-rag-report.md
dotnet run --project evaluation/RepoMind.Evaluation -- --mode agents --base-url http://127.0.0.1:5000 --repository-id <id> --report evaluation/final-agents-report.md
```

`evaluation/fixtures/TaskTrail` was rebuilt from repository 38's gap-free
indexed coverage. Uploading it produces chunks byte-identical to repository 38,
so Day 14 results can be reproduced on a clean database.
