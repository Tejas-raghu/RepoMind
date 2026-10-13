# RepoMind

Day 2: a local ASP.NET Core API that generates text embeddings using Ollama.

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
No AI SDK or third-party NuGet packages are required.

Reference: [Ollama embeddings documentation](https://docs.ollama.com/capabilities/embeddings).

Database storage, similarity search, RAG, frontend work, and agents belong to later days.
