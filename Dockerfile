# One image for the hosted demo: the ASP.NET Core API also serves the built Vue UI.
# Build: docker build -t repomind .   Run: see docs/deployment.md

# 1. Build the Vue app. VITE_API_BASE_URL stays unset, so the UI calls /api on its own origin.
FROM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

# 2. Publish the API.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY backend/RepoMind.Api/RepoMind.Api.csproj backend/RepoMind.Api/
RUN dotnet restore backend/RepoMind.Api/RepoMind.Api.csproj
COPY backend/ backend/
RUN dotnet publish backend/RepoMind.Api/RepoMind.Api.csproj -c Release -o /app --no-restore

# 3. Small runtime image, running as the non-root user the base image provides.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app ./
COPY --from=frontend /src/frontend/dist ./wwwroot
# "Hosted" loads appsettings.Hosted.json: Cloudflare provider and public-demo limits.
# Secrets (database, Cloudflare token) come only from environment variables at run time.
ENV ASPNETCORE_ENVIRONMENT=Hosted \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "RepoMind.Api.dll"]
