# Deploying the public demo (free tiers)

RepoMind deploys as **one Docker container**. The ASP.NET Core API also serves the
built Vue UI, so the site has a single URL and needs no CORS configuration.

| Part | Service | Free-tier notes |
|---|---|---|
| App container (API + UI) | [Render](https://render.com) web service | Sleeps after about 15 minutes without visitors. The first request after that takes about a minute while it starts. |
| PostgreSQL + pgvector | [Neon](https://neon.tech) | About 0.5 GB of storage. Its compute also pauses when idle and resumes quickly. |
| Embeddings and chat model | Cloudflare Workers AI | Daily free allowance of neurons. On the Workers **Free** plan, requests beyond it fail until the next day instead of being billed. |

Free hosting has no GPU, so the hosted demo uses the Cloudflare provider. Ollama stays
the default for local development.

## What the hosted mode changes

The image sets `ASPNETCORE_ENVIRONMENT=Hosted`, which loads
[appsettings.Hosted.json](../backend/RepoMind.Api/appsettings.Hosted.json):

- **AI provider:** `Ai:Provider = cloudflare`.
- **Public demo:** `Demo:Enabled = true`. Concretely:
  - **Rate limits.** Every ask (`/agents/run`) and upload passes two limits: one per visitor IP and one shared daily cap that protects your Cloudflare quota. A visitor who reaches their own limit does not use up the shared cap. Over the limit, the API returns HTTP 429 and the UI shows "Try again in about N minutes".
  - **Learning endpoints off.** The Days 2–10 endpoints (`/api/llm/chat`, `/api/embeddings*`, `/search`, `/rag/*`, `/hybrid/search`) return 404. The UI does not use them.
  - **Upload warning.** The upload panel tells visitors that every visitor can see uploaded code.
- **Health check:** `/health` returns `{"status":"ok"}` without calling the database or the AI provider.
- **Secrets:** they come only from environment variables. The database connection string is empty in the file on purpose.

Default limits (change them with environment variables such as `Demo__AsksPerDay=100`):

| Setting | Default |
|---|---|
| `Demo__AsksPerIpPerHour` | 20 |
| `Demo__AsksPerDay` | 150 (shared by all visitors) |
| `Demo__UploadsPerIpPerDay` | 5 |
| `Demo__UploadsPerDay` | 30 (shared) |

## 1. Create the Neon database

1. Sign up at neon.tech and create a project. Pick the region closest to your Render region (for example Singapore for both).
2. In **Connect**, turn **off** connection pooling, so you get the direct host without `-pooler`.
3. Copy two forms of the connection string:
   - the **psql** URL, `postgresql://USER:PASSWORD@HOST/neondb?sslmode=require`, used to create the tables;
   - the **.NET** form, `Host=HOST;Database=neondb;Username=USER;Password=PASSWORD;SSL Mode=Require`, used by Render.
4. Create the tables from the repository root. This uses the psql that is already inside the pgvector image, so you don't need to install anything:

   ```powershell
   $url = 'postgresql://USER:PASSWORD@HOST/neondb?sslmode=require'
   Get-Content database/init.sql, database/006_repository_upload.sql, database/007_repository_search.sql,
     database/0145_cloudflare_embeddings.sql, database/015_agent_runs.sql -Raw |
     docker run --rm -i pgvector/pgvector:0.8.7-pg17 psql $url -v ON_ERROR_STOP=1
   ```

   The scripts use `IF NOT EXISTS`, so running them twice is harmless. Neon supports the `vector` extension.

## 2. Cloudflare credentials

Use the same account ID and Workers AI API token as in [day14-5-cloudflare.md](day14-5-cloudflare.md).
In the Cloudflare dashboard, check which Workers plan the account is on. On the Free plan,
going over the daily allowance only causes errors, never charges.

## 3. Deploy on Render

1. Push `main` to GitHub. Already done if you are reading this on GitHub.
2. In Render: **New → Blueprint**, connect the GitHub repository, and select it. Render reads [render.yaml](../render.yaml).
3. Render asks for the three secret values. Paste them; they are stored only in Render.
   - `ConnectionStrings__RepoMind`: the Neon **.NET** connection string
   - `Cloudflare__AccountId`
   - `Cloudflare__ApiToken`
4. Create the Blueprint. The first build takes about 5–10 minutes. Then open `https://<service>.onrender.com/health`, which should return `{"status":"ok"}`.
5. `autoDeploy` is off, so later pushes don't redeploy automatically. To publish a new version, use **Manual Deploy → Deploy latest commit**.

## 4. After the first deploy

1. Open the site. The repository panel should show `cloudflare · @cf/qwen/qwen3-embedding-0.6b · @cf/openai/gpt-oss-20b` and the yellow public-demo notice.
2. Upload the sample so visitors have something to try:
   `Compress-Archive -Path evaluation/fixtures/TaskTrail -DestinationPath $env:TEMP\TaskTrail.zip -Force`
3. Ask "Where is the Bearer token added to requests?", then "Review src/router/guards.ts for security issues".
4. Put the URL in the README and on LinkedIn. Mention that the first visit may take a minute while the free instance wakes up.

## Test the image locally first (optional)

The image runs the hosted configuration against your local database:

```powershell
docker compose up -d db
docker build -t repomind:local .
docker run --rm -p 8080:8080 --network repomind_default `
  -e ConnectionStrings__RepoMind='Host=db;Port=5432;Database=repomind;Username=repomind;Password=localdev' `
  -e Cloudflare__AccountId=$env:Cloudflare__AccountId -e Cloudflare__ApiToken=$env:Cloudflare__ApiToken `
  -e Demo__BehindProxy=false repomind:local
```

Then open http://localhost:8080/. `Demo__BehindProxy=false` is needed only locally,
because no proxy adds `X-Forwarded-For`.

## Housekeeping

- **Remove a visitor's upload**, for example if someone uploads private code. Run in the Neon SQL editor:

  ```sql
  DELETE FROM agent_runs WHERE repository_id = 42;
  DELETE FROM repository_chunks WHERE repository_id = 42;
  DELETE FROM repositories WHERE id = 42;
  ```

- **Storage:** check usage in the Neon dashboard now and then. 30 uploads a day is far below the free limit for a while, but uploads are not deleted automatically.
- **Logs:** use the Render dashboard → Logs. Secrets are never logged.
- **Limitation:** client IPs come from the proxy's `X-Forwarded-For` header. If the platform's proxy chain changes, visitors may share one per-IP bucket. The shared daily cap still protects the quota.
