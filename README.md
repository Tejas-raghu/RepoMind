# RepoMind

Day 2: a local ASP.NET Core API that generates text embeddings using Ollama.
Day 3: a local PostgreSQL database with pgvector enabled.
Day 4: save text and its Ollama embedding in PostgreSQL.

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
