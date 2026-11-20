# Manual sanity-testing checklist

Use a fresh terminal for each service. Tick each item. Local model steps take
15–90 seconds on CPU, so wait for the elapsed counter.

## 0. Setup

- [ ] `docker compose up -d db` → `docker compose ps` shows `db` healthy.
- [ ] On an existing volume, apply the new migration once (safe to repeat):
      `Get-Content database/015_agent_runs.sql -Raw | docker compose exec -T db psql -U repomind -d repomind -v ON_ERROR_STOP=1`
- [ ] `ollama list` shows `embeddinggemma` and `qwen3:4b`.
- [ ] Start the API: `dotnet run --project backend/RepoMind.Api --no-launch-profile --urls http://localhost:5000`
- [ ] `Invoke-RestMethod http://localhost:5000/api/system/info` shows `ollama`, `embeddinggemma`, `qwen3:4b`, `rerankingEnabled: False`.
- [ ] Start the UI: `cd frontend; npm install; npm run dev`, then open http://127.0.0.1:5173/.

## 1. Repositories and upload

- [ ] The repository panel shows "AI provider: ollama · embeddinggemma · qwen3:4b".
- [ ] Dropdown entries show `name · #id · provider`.
- [ ] Select a Cloudflare repository (#581/#582) → a red mismatch warning appears.
- [ ] Create the fixture ZIP: `Compress-Archive -Path evaluation/fixtures/TaskTrail -DestinationPath $env:TEMP\TaskTrail.zip -Force`, upload it → "Repository #N indexed · 12 files · 13 chunks", and it becomes selected.
- [ ] Upload a `.txt` renamed or any non-ZIP → clear error, no new repository.
- [ ] Upload a ZIP containing `src/config.ts` with `const awsKey = 'AKIAABCDEFGHIJKLMNOP'` → success line says "1 secret value(s) redacted". The explorer shows `[REDACTED:secret]` on that line, with the same line number.

## 2. Explorer

- [ ] The file tree shows nested `TaskTrail/src/...` folders; folders collapse and expand.
- [ ] Open `guards.ts` → line numbers, syntax highlighting, "Copy code" and "Copy path" work.
- [ ] Keyboard: Tab reaches tree items and buttons; focus outlines are visible.

## 3. Questions (one Ask box)

Everything, questions and agent tasks alike, is typed into the single **"02 / Ask RepoMind"** box.

- [ ] "Where is the Bearer token added to requests?" → "Answer ready", with a source `apiClient.ts`.
- [ ] Expand the source card → code is shown as text. "Open in explorer" scrolls down and highlights the cited lines.
- [ ] "Where is Stripe payment processing implemented?" → "Insufficient evidence", with no sources.
- [ ] Start a question, then press Cancel → the spinner stops and no answer appears.
- [ ] Start a question, then switch repository → the old answer never appears.
- [ ] "How are protected routes secured?" → note the outcome. The verifier has abstained on this one before; record whether it answers.

## 4. Explain and review tasks (same box)

- [ ] "Delete the AuthController file" (Ctrl+Enter) → "Not supported" within a second, with a read-only explanation.
- [ ] "Review src/router/guards.ts for security issues" → Review Agent, Completed or Partial. Open "Plan and execution trace": `read_file`, `static_checks`, `synthesize`.
- [ ] "Explain what apiClient.ts does" → Code Analysis Agent; findings carry citation buttons. Clicking one opens the explorer at that line.
- [ ] "Explain how overdue tasks are identified" (no file named) → the trace shows `search_code` and `plan_targets`; evidence comes from `taskService.ts`.
- [ ] "Explain the Stripe payment integration" → Abstained, no findings.
- [ ] A plain question shows the badge "Retrieval Agent · RAG answer" with source cards; a review shows "Review Agent" with findings.
- [ ] "Recent questions and tasks" lists each run, questions included; clicking one reopens it with the same findings and `run #id`.
- [ ] Rule findings are labeled `rule · <rule-id>`, model findings `model`. Severity colors are visible.

## 5. Failure behavior

- [ ] Stop Ollama (`ollama` tray → Quit), then run "Review src/router/guards.ts" → status Partial, trace `synthesize failed`, rule findings only (if any). Restart Ollama.
- [ ] Stop the API and refresh the UI → "The RepoMind API is unavailable…" message.
- [ ] Restart the API → Refresh works again.

## 6. Layout

- [ ] Browser width about 390 px: no horizontal page scroll, panels stack, buttons stay usable.
- [ ] Desktop width: the agent trace table scrolls horizontally inside its box when needed.

## 7. Provider switching (optional, needs a Cloudflare account)

- [ ] Follow [day14-5-cloudflare.md](day14-5-cloudflare.md) to start a second API with `Ai__Provider=cloudflare`.
- [ ] `/api/system/info` shows `cloudflare`. Querying an Ollama repository returns HTTP 409, and the UI shows the mismatch warning.
- [ ] Upload a small ZIP in Cloudflare mode, then run a question and an agent task on it.

## 8. Automated suites (optional repeat)

- [ ] `dotnet test tests/RepoMind.Api.Tests/RepoMind.Api.Tests.csproj`
- [ ] `cd frontend; npm test; npm run build`
- [ ] Browser E2E and evaluations: see [testing.md](testing.md).

## 9. Portfolio tasks (manual, not done by the agent)

- [ ] Record the demo: upload, ask, cite to explorer, agent review with trace, abstention.
- [ ] Prepare the GitHub README screenshots and the LinkedIn post.
