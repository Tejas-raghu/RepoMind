# RepoMind progress

## Week 1, Day 6 — verified

- Added multipart ZIP upload at `POST /api/repositories/upload`.
- Added an idempotent migration for `repositories` and `repository_chunks`; applying it twice succeeded on the local PostgreSQL database.
- A two-file ZIP upload returned `repositoryId: 1`, `status: completed`, `filesIndexed: 2`, `chunksCreated: 2`, and `filesSkipped: 0`.
- PostgreSQL showed two chunks for repository 1, with file paths, line ranges (1–12 and 1–44), and 768-dimensional embeddings.
- The existing embed, store, and search endpoints responded successfully after the interface change.
- `dotnet test tests/RepoMind.Api.Tests/RepoMind.Api.Tests.csproj --no-restore` passed 15 tests covering chunk boundaries, line numbers, ZIP paths, exclusions, symlinks, file count and byte budgets, a valid archive, batch embedding order, and long HTML lines.
- The upload endpoint returned HTTP 415 for a non-ZIP filename, HTTP 400 for malformed ZIP content, and HTTP 413 for a file over 5 MB.
- After allowing `.html` and a 250 KB per-file limit, the real seven-file HTML ZIP returned HTTP 200 with `repositoryId: 2`, `status: completed`, `filesIndexed: 7`, `chunksCreated: 485`, and `filesSkipped: 0`.
- PostgreSQL showed exactly 485 chunks across seven source paths for repository 2; all stored embeddings had 768 dimensions.
- The HTML ZIP contained nine generated lines over 8,192 characters. Skipping those lines and embedding duplicate chunk text only once reduced local CPU embedding work while preserving original line references and one database row per chunk.
