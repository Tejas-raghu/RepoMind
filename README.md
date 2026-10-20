# RepoMind

Day 2: a local ASP.NET Core API that generates text embeddings using Ollama.
Day 3: a local PostgreSQL database with pgvector enabled.
Day 4: save text and its Ollama embedding in PostgreSQL.
Day 5: search saved text by cosine similarity.
Day 6: upload a trusted source ZIP and store indexed code chunks.

## Run

Prerequisites: .NET 10 SDK, running Ollama, and the `embeddinggemma` model.
If the Ollama desktop app is not running, start `ollama serve` in a separate terminal.
Day 1 already installed the model; if needed, run `ollama pull embeddinggemma`.

From the repository root:

```powershell
dotnet build backend/RepoMind.Api
dotnet run --project backend/RepoMind.Api --no-launch-profile --urls http://localhost:5000
```

In another PowerShell terminal:

```powershell
$body = @{ text = 'Authentication validates a user before granting access.' } | ConvertTo-Json
$result = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/embeddings -ContentType 'application/json' -Body $body
$result.model
$result.dimensions
$result.embedding | Select-Object -First 5
```

Expect `embeddinggemma`, a positive dimension count, and numeric vector values.
Repeat with different text to see how the vector changes. Individual coordinates
are learned features, not readable labels; comparing whole vectors is what makes
them useful for semantic search later.

An empty or whitespace-only `text` returns HTTP 400. An unavailable Ollama or failed
upstream request returns 502; a two-minute upstream timeout returns 504.

## How it works

- `Program.cs` defines the endpoint and registers the service using dependency injection.
- `Contracts/EmbeddingDtos.cs` defines the public JSON request and response.
- `Services/EmbeddingService.cs` sends `{ model, input, truncate: false }` to Ollama's
  `/api/embed` and extracts the one vector from its `embeddings` array.
- `appsettings.json` configures the local Ollama URL and embedding model.

The injected `HttpClient` is managed by ASP.NET Core. Calls use async/await so the
server does not block a thread while Ollama works. The request cancellation token
flows through the HTTP call so a disconnected client can cancel the work.
`truncate: false` avoids silently discarding text beyond the model's context limit.
The Day 2 endpoint needs no AI SDK or third-party NuGet package.

Reference: [Ollama embeddings documentation](https://docs.ollama.com/capabilities/embeddings).

Similarity search, RAG, frontend work, and agents belong to later days.

## Day 3: PostgreSQL and pgvector

Prerequisite: Docker Desktop with Docker Compose available from your terminal.
From the repository root, start the database:

```powershell
docker compose up -d db
docker compose ps
```

Once `db` is healthy, check the extension:

```powershell
docker compose exec db psql -U repomind -d repomind -c "SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';"
```

Expect one row named `vector`. The database is available only on your machine at
`localhost:5432`, with database/user `repomind` and password `localdev`. These are
local development credentials; change them before using this setup elsewhere.
The named Docker volume keeps data between container restarts. `database/init.sql`
runs only when PostgreSQL initializes a new, empty volume.

To stop the database while retaining its data, run `docker compose down`.

## Day 4: Store embeddings

Start PostgreSQL and Ollama as above. Apply the schema from the repository root:

```powershell
docker compose exec db psql -U repomind -d repomind -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/01-init.sql
```

This step is needed for the existing Day 3 Docker volume because PostgreSQL only
runs `init.sql` automatically when the volume is first created. The SQL is safe to
run again. Then run the API with the command above and save one example:

```powershell
$body = @{ text = 'Authentication validates a user before granting access.' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/embeddings/store -ContentType 'application/json' -Body $body
docker compose exec db psql -U repomind -d repomind -c "SELECT id, content, model, vector_dims(embedding) AS dimensions FROM stored_embeddings ORDER BY id DESC LIMIT 5;"
```

The API response contains the new row ID, `embeddinggemma`, and `768` dimensions.
The query should show the same ID and text. Each request inserts a new row.

`EmbeddingService` turns text into a list of numbers whose position captures some
of its meaning. `EmbeddingStore` writes those numbers as a pgvector `vector`, along
with the source text and model name. The insert uses parameters so text is treated
as data. `vector(768)` matches the configured `embeddinggemma` model; switching
models with a different dimension will require a schema change. The route accepts
the same `text` request as `/api/embeddings`, but returns row metadata instead of
the full vector. The PostgreSQL connection string in `appsettings.json` is for
local development and can be overridden with `ConnectionStrings__RepoMind`.

## Day 5: Similarity search

With PostgreSQL, Ollama, and the API running, save at least two different texts
using `/api/embeddings/store`. Then search with a related question:

```powershell
$body = @{ text = 'How does login verify a user?' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/embeddings/search -ContentType 'application/json' -Body $body
```

The response is a list containing the closest stored text. The result includes
its row ID, text, model, and similarity score.
If the table has no rows for the configured model, the response is an empty list.

The API embeds the question using the same model used to store the texts. In SQL,
`<=>` computes cosine distance between vectors; `1 - distance` gives cosine
similarity, so higher scores mean closer meanings. The query filters by model to
avoid comparing embeddings from different models. It scans the small table for
now; an approximate index can wait until there is enough data to need one.

## Day 6: Upload a trusted repository ZIP

Start PostgreSQL and Ollama. On an existing Day 3–5 database, apply the new
idempotent migration before starting the API:

```powershell
docker compose up -d db
docker compose exec db psql -U repomind -d repomind -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/02-repository-upload.sql
dotnet run --project backend/RepoMind.Api --no-launch-profile --urls http://localhost:5000
```

In another PowerShell terminal, create a small ZIP for practice and upload it:

```powershell
Compress-Archive -Path backend/RepoMind.Api/Contracts/EmbeddingDtos.cs,backend/RepoMind.Api/Services/EmbeddingStore.cs -DestinationPath sample-repo.zip -Force
curl.exe -F "file=@sample-repo.zip" http://localhost:5000/api/repositories/upload
```

Expect `repositoryId`, `status: completed`, `filesIndexed`, `chunksCreated`, and
`filesSkipped`. Check the indexed lines and embedding dimensions:

```powershell
docker compose exec db psql -U repomind -d repomind -c "SELECT repository_id, source_path, start_line, end_line, vector_dims(embedding) AS dimensions FROM repository_chunks ORDER BY id DESC LIMIT 5;"
```

The endpoint reads `.cs`, `.ts`, `.tsx`, `.js`, `.jsx`, `.vue`, `.json`, `.md`, and `.html`
directly from the ZIP. It never extracts source files. It rejects absolute and
traversal paths, and skips hidden, generated, secret/config-sensitive, binary,
oversized, and unsupported files. The ZIP limit is 5 MB; it accepts at most 100
files, 250 KB per file, and 10 MB total uncompressed content. Code is split into
60-line chunks with 10 overlapping lines; unusually long windows are split into
pieces of at most 2,000 characters. Each chunk gets an Ollama embedding
and a row linked to its repository. HTML lines longer than 8,192 characters
are omitted from chunks because generated inline data can overwhelm local
embedding; original line numbers are preserved. Identical chunk text is
embedded once per upload, while every chunk gets its own database row.
The upload commits all rows together after embedding succeeds. Upload only a
repository you trust; this local learning endpoint has no authentication.
Day 2–5 text embeddings remain in
`stored_embeddings`; uploaded code uses separate `repositories` and
`repository_chunks` tables.

Run the focused tests with:

```powershell
dotnet test tests/RepoMind.Api.Tests/RepoMind.Api.Tests.csproj
```

## Day 7: Search one indexed repository

On an existing database, apply the idempotent index migration once. The database
container must be recreated after updating its Compose mounts:

```powershell
docker compose up -d db
docker compose exec -T db psql -U repomind -d repomind -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/03-repository-search.sql
dotnet run --project backend/RepoMind.Api --no-launch-profile --urls http://localhost:5000
```

In another PowerShell terminal, search a repository ID returned by an upload:

```powershell
$body = @{ query = 'Where are embeddings stored?'; topK = 5 } | ConvertTo-Json
Invoke-RestMethod -Uri 'http://localhost:5000/api/repositories/1/search' -Method Post -ContentType 'application/json' -Body $body
```

The response contains `repositoryId` and ranked `results`. Each result has
`chunkId`, `filePath`, `startLine`, `endLine`, `content`, and `similarity`;
vectors are not returned. The query is embedded with the same configured model
used for upload. PostgreSQL filters by repository ID and model, orders by
`embedding <=> query_embedding` (cosine distance), and converts distance to
similarity with `1 - distance`. `topK` must be 1–20 and the query at most 500
characters. Results below `RepositorySearch:MinimumSimilarity` (0.35 by default)
are excluded before applying `topK`, so a populated repository can also return
`results: []` when no chunk clears the cutoff. This value is a provisional
setting based on a few local queries, not a universal definition of relevance;
adjust it using labeled examples from your own repositories. An existing empty
repository returns `results: []` without calling
Ollama; an unknown ID returns 404. The regular index on `repository_id` helps
scope searches; this small MVP still uses exact ranking within that repository.

The tests include a PostgreSQL integration test, so start the local database
before running `dotnet test tests/RepoMind.Api.Tests/RepoMind.Api.Tests.csproj`.
Set `REPOMIND_TEST_CONNECTION` if the test database uses a different connection
string.
