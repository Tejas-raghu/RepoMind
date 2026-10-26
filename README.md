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

## Day 9: Preview repository RAG context

With PostgreSQL, Ollama, and the API running, use an ID returned by repository
upload:

```powershell
$body = @{ query = 'Where are embeddings stored?'; topK = 5 } | ConvertTo-Json
$preview = Invoke-RestMethod -Uri 'http://localhost:5000/api/repositories/1/rag/context' -Method Post -ContentType 'application/json' -Body $body
$preview | Select-Object repositoryId,query,chunksUsed,contextCharacters,chunksOmittedDueToBudget,insufficientBudget
$preview.verification | Select-Object status,answerable,reason,verifierDurationMs
$preview.context
$preview.sources | Format-Table sourceNumber,filePath,startLine,endLine,similarity
```

The endpoint uses the repository-scoped hybrid retrieval described below, then
calls the local verifier before formatting source blocks. It does not generate an answer. `topK`
must be 1–10; the query must be nonblank and at most 500 characters. An unknown
repository returns 404. An empty repository returns zero sources.
With no pre-fusion similarity cutoff, a populated repository can still retrieve
low-relevance chunks. Only IDs accepted by verification enter the context.
Check `verification.answerable` and `chunksUsed` before using it for an answer.
Non-supported or unavailable verification returns no source blocks. The local
Qwen call can make this endpoint much slower than hybrid search alone.
The Day 7 search endpoint retains its own cosine cutoff. Hybrid retrieval has
no cosine cutoff before fusion; `topK` limits the fused list before context
formatting.

`RagContext:MaxCharacters` defaults to 10,000 and must be at least 1,000.
This limits the entire preview,
including the instruction, question, and source blocks. It counts .NET string
characters as a simple approximation, **not model tokens or an exact token
budget**. Source blocks are added in final rank order and only when each whole
block fits. If any verified evidence block does not fit, the endpoint returns
no source blocks, sets `insufficientBudget`, and marks verification `uncertain`.
Source paths and line ranges come only from stored chunk metadata. Repository
code is untrusted data, even when a code comment contains instructions.

## Day 9.5A: Hybrid search preview

The existing `/search` endpoint keeps its semantic-search behavior. The
`/rag/context` endpoint uses hybrid ranking and verification. The separate
hybrid endpoint exposes ranks and scores for retrieval inspection:

```powershell
$body = @{ query = 'Where is the Bearer token added to requests?'; topK = 5 } | ConvertTo-Json
$result = Invoke-RestMethod -Uri 'http://localhost:5000/api/repositories/38/hybrid/search' -Method Post -ContentType 'application/json' -Body $body
$result.results | Select-Object filePath,startLine,endLine,vectorRank,lexicalRank,cosineSimilarity,rrfScore | Format-Table
```

Replace `38` with your uploaded repository ID. `topK` defaults to 5 and accepts
1–20. `HybridSearch:VectorCandidates` and `HybridSearch:LexicalCandidates`
default to 15 each. A nonexistent repository returns 404; an empty repository
returns no results without embedding the query.

The vector list uses the same pgvector cosine ordering as Day 7 but has **no
similarity cutoff before fusion**. The lexical list searches chunk content and
relative paths with PostgreSQL's `simple` full-text configuration, plus
case-insensitive literal term matching for code terms such as `Authorization`,
`Bearer`, `JWT`, and `CS0234`. It puts matching source-code files before docs
within that list. Both SQL queries restrict `repository_id` and embedding model.
No new database, model, or migration is needed for this small repository scan.

Reciprocal Rank Fusion (RRF) adds `1 / (60 + rank)` from each list containing a
chunk, using ranks starting at 1. This combines ranks rather than cosine and
text-search scores, which have different scales. Matching chunk IDs are merged
once; ties use vector rank, lexical rank, then chunk ID. The response retains
stored source content and line metadata and includes `vectorRank`, `lexicalRank`,
`cosineSimilarity`, and `rrfScore` for inspection. A missing rank means that
chunk was absent from that candidate list. These diagnostics do not log source
content. This first version scans a small repository for lexical matches and
uses simple English-oriented query term extraction; larger or multilingual
repositories will need evaluation and indexing work.

## Day 9.5C: Verify source evidence

`POST /api/repositories/{repositoryId}/rag/verify` accepts a nonblank query of
at most 500 characters and `topK` from 1 to 5 (default 5). It uses the same
repository-scoped hybrid search and optional reranker as `/rag/context`. It
does not generate an answer. The response has `status` (`supported`,
`insufficient_evidence`, `uncertain`, or `verification_unavailable`),
`answerable`, validated `evidenceChunkIds`, trusted `sources` (database chunk
ID, path, and lines), a short safe `reason`, `rerankingStatus`, and
`verifierDurationMs`. Source contents are never copied from the model output.

The verifier sends up to five complete final-ranked chunks to the configured
local Qwen model with a JSON Schema. `EvidenceVerification:MaxCharacters`
(default 8,000) bounds the question and complete chunk data; it never silently
cuts code while retaining an inaccurate line range. If the budget omits chunks,
an `insufficient_evidence` model decision becomes `uncertain`. If no chunk fits,
the endpoint returns `uncertain` without calling Qwen. Empty repositories return
`insufficient_evidence` without calling Qwen. Model timeout, unavailable model,
or invalid output returns `verification_unavailable` and `answerable: false`.
`EvidenceVerification:TimeoutSeconds` defaults to 60.

```powershell
$body = @{ query = 'Where is the Bearer token added to requests?'; topK = 3 } | ConvertTo-Json
$result = Invoke-RestMethod -Uri 'http://localhost:5000/api/repositories/38/rag/verify' -Method Post -ContentType 'application/json' -Body $body
$result | Select-Object status,answerable,reason,verifierDurationMs,rerankingStatus
$result.sources | Format-Table chunkId,filePath,startLine,endLine
```

Replace `38` with your uploaded repository ID. The fixed TaskTrail smoke set
and measurement script are in `evaluation/day95c-tasktrail.json` and
`evaluation/day95c-evaluate.ps1`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File evaluation/day95c-evaluate.ps1 -RepositoryId 38 -TopK 3
```

The script prints per-query outcomes, supported acceptance, unsupported false
acceptance, uncertain/unavailable counts, and average verifier duration. This
small set is a learning aid, not a reliability claim.

### One RAG question flow

`/rag/context` runs one repository-scoped hybrid search (vector plus lexical
candidates, RRF, and local reranking), verifies up to five final-ranked
chunks, and formats only validated evidence. `/rag/verify` returns the same
verification decision without context formatting. `/hybrid/search` remains a
retrieval diagnostic. No endpoint generates a final answer yet. For an empty
repository, the verifier is skipped. When verification is uncertain,
insufficient, or unavailable, the context contains no source blocks.
`Reranking:Enabled` is true in `appsettings.json`; set it to false to skip that
local model call while keeping RRF and verification. Both model calls add
latency. Check `rerankingStatus` and `verification.status` in the response.
