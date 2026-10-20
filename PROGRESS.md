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

## Week 1, Day 7 — verified

- Added `POST /api/repositories/{repositoryId}/search` with positive numeric ID, nonempty query of at most 500 characters, and `topK` from 1 to 20.
- Search reuses the configured Ollama embedding service and pgvector data source. SQL filters by repository ID and model, ranks by cosine distance, and returns IDs, file paths, line ranges, content, and cosine similarity without vectors.
- Added the idempotent B-tree index migration on `repository_chunks(repository_id)`; applying it twice succeeded.
- `dotnet test tests/RepoMind.Api.Tests/RepoMind.Api.Tests.csproj --no-restore` passed 26 tests. The PostgreSQL integration test verified repository isolation, model filtering, ordering, Top-K, missing and empty repositories, similarity, and source metadata.
- Live HTTP checks returned 200 for searches of indexed repositories 1 and 2, 404 for an unknown repository, and 400 for a nonnumeric ID, blank query, and `topK` of 21. Repository 2 returned five ranked HTML chunks for `topK: 5`, with no embedding vectors in the response.
- A temporary empty repository returned `results: []` through HTTP and was then deleted.
- Added a configurable minimum cosine similarity of 0.35 after a map query returned unrelated chunks from repository 1. The integration test verified that scores below the cutoff are excluded before Top-K and that a stricter configured value changes the results.
- Live checks with the updated API returned no results for the map query in repository 1 and the authentication query in repository 2, while returning five map chunks in repository 2 and relevant embedding code in repository 1. This cutoff is provisional and needs more labeled queries before treating it as a general relevance rule.
