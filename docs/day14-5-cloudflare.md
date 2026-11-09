# Day 14.5: optional Cloudflare Workers AI

RepoMind defaults to Ollama. Set `Ai__Provider=cloudflare` only for a separate
Cloudflare API process. The backend reads Cloudflare credentials from environment
variables or local .NET User Secrets; never put them in `appsettings.json` or any `VITE_` variable. Rotate any
token pasted into chat or a shell history before continued use.

## Existing data and migration

The current `embedding` columns are `vector(768)` and keep all Ollama values.
The additive migration creates nullable `embedding_cf vector(1024)` columns and
records each repository's embedding provider/model. Cloudflare uploads write only
the 1024-dimensional column; old rows are not converted. Querying a repository
with a different configured provider/model returns HTTP 409. Re-upload the source
ZIP in Cloudflare mode to create a separate repository. Exact cosine search still
uses pgvector's `<=>` operator and the existing repository B-tree filter; there
is no HNSW index in this small project.

For an existing Docker volume, from the repository root:

```powershell
docker compose up -d db
Get-Content database/0145_cloudflare_embeddings.sql -Raw |
  docker compose exec -T db psql -U repomind -d repomind -v ON_ERROR_STOP=1
```

Fresh database volumes receive the same script through `compose.yaml`. Reapplying
the script is safe.

## Start Cloudflare mode

Find the 32-character **Account ID** in Cloudflare Dashboard → Workers & Pages.
Use a Workers AI token with inference permission. In the same PowerShell window:

```powershell
$env:Ai__Provider = 'cloudflare'
$env:Cloudflare__AccountId = '<32-character-account-id>'
$secret = Read-Host 'Workers AI API token' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try { $env:Cloudflare__ApiToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
$env:Reranking__Enabled = 'false'
dotnet run --project backend/RepoMind.Api/RepoMind.Api.csproj --urls http://127.0.0.1:5015
```

To use the existing Vue UI with that test process, start Vite in another shell:

```powershell
cd frontend
$env:VITE_API_PROXY_TARGET = 'http://127.0.0.1:5015'
npm run dev
```

Only the local API URL goes into this Vite variable; the Cloudflare token stays in
the backend process.

On the development machine used for this check, the token was stored in .NET
User Secrets for `backend/RepoMind.Api/RepoMind.Api.csproj`; the `Read-Host` token
step can be skipped there. Keep `ASPNETCORE_ENVIRONMENT=Development` for User
Secrets to load. The account ID is separate from the token.

After rotating the token, replace that local User Secret without putting the
new value in a command argument:

```powershell
$secret = Read-Host 'New Workers AI token' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try {
  $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
  @{ 'Cloudflare:ApiToken' = $value } | ConvertTo-Json -Compress |
    dotnet user-secrets set --project backend/RepoMind.Api/RepoMind.Api.csproj
} finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
```

The chosen Cloudflare models are Qwen3 Embedding 0.6B (1024 dimensions), Qwen3
30B A3B for chat, verification and grounded answers, and optional BGE Reranker
Base. Cloudflare reranking requires both `Reranking__Enabled=true` and
`Cloudflare__RerankingEnabled=true`. It is off by default: in the two-file smoke
check BGE put the descriptive README before the implementation, changing Hit@1
from 1/1 to 0/1. This tiny check does not establish general BGE quality.
Cloudflare currently includes a daily free Workers AI allocation. Keep uploads
small and stop when the API reports HTTP 429 quota exhaustion; the backend does
not automatically move to a paid plan.
Cloudflare Qwen does not appear on the current JSON Mode support list, so the
backend asks for JSON in the prompt and strictly validates the returned object.
Malformed verification or answer JSON gets one bounded retry, then fails closed.
Cloudflare answers allow up to 1,200 output tokens by default because Qwen may
spend part of that allowance before producing final JSON. BGE sees at most 700 characters of each candidate
including its path; retrieval and citations keep the full original stored chunk.
The cap is an approximation of BGE's 512-token pair limit, so an over-limit API
response can still fail safely. Capacity error 3040 gets at most two retries;
daily quota error 3036 is returned as HTTP 429 without retry.

## Smoke test

Open a second PowerShell window in the repository root. This fixture is deliberately
small and synthetic. It cannot replace the twelve-file TaskTrail evaluation fixture.

```powershell
$zip = Join-Path $env:TEMP 'repomind-cloudflare-smoke.zip'
Compress-Archive -Path evaluation/cloudflare-smoke/TaskTrail -DestinationPath $zip -Force
$upload = curl.exe -sS -F "file=@$zip" http://127.0.0.1:5015/api/repositories/upload | ConvertFrom-Json
$upload
$id = $upload.repositoryId
$body = @{ query = 'Where is the Bearer token added to requests?'; topK = 1 } | ConvertTo-Json
Invoke-RestMethod "http://127.0.0.1:5015/api/repositories/$id/hybrid/search" -Method Post -ContentType 'application/json' -Body $body
Invoke-RestMethod "http://127.0.0.1:5015/api/repositories/$id/rag/answer" -Method Post -ContentType 'application/json' -Body $body
```

For a comparable Day 14 run, upload the **original** twelve-file TaskTrail ZIP
in Cloudflare mode and use the new repository ID. The evaluator validates all
twelve indexed paths and five exact anchors before measuring anything:

```powershell
dotnet run --project evaluation/RepoMind.Evaluation/RepoMind.Evaluation.csproj -- --base-url http://127.0.0.1:5015 --repository-id <new-id> --report evaluation/day145-cloudflare-report.md
```

For Ollama, open a clean shell without `Ai__Provider=cloudflare`, or set
`$env:Ai__Provider='ollama'`. Existing 768-dimensional repositories remain usable.
The API reports prompt/output tokens only if Cloudflare supplies them; it does
not calculate or invent neuron usage.

Cloudflare references: [embedding model](https://developers.cloudflare.com/workers-ai/models/qwen3-embedding-0.6b/),
[chat model](https://developers.cloudflare.com/workers-ai/models/qwen3-30b-a3b-fp8/),
[reranker](https://developers.cloudflare.com/workers-ai/models/bge-reranker-base/),
[JSON Mode support](https://developers.cloudflare.com/workers-ai/features/json-mode/),
[errors](https://developers.cloudflare.com/workers-ai/platform/errors/),
[free allocation](https://developers.cloudflare.com/workers-ai/platform/pricing/).
