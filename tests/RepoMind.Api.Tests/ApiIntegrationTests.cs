using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepoMind.Api.Agents;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

// The real ASP.NET Core pipeline (routing, binding, validation, status codes,
// DI) against the real local PostgreSQL + pgvector database. Only the AI
// providers are replaced with deterministic fakes; live-model behavior is
// covered separately by the end-to-end run and the evaluation harness.
public sealed class RepoMindApiFactory : WebApplicationFactory<Program>
{
    public StubEmbeddings Embeddings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:RepoMind", TestDatabase.ConnectionString);
        builder.UseSetting("Ai:Provider", "ollama");
        builder.UseSetting("Reranking:Enabled", "false");
        builder.UseSetting("Agents:ModelPlanning", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService>(Embeddings);
            services.RemoveAll<ILlmService>();
            services.AddSingleton<ILlmService, FakeLlm>();
            services.RemoveAll<IEvidenceVerificationService>();
            services.AddSingleton<IEvidenceVerificationService, FakeVerifier>();
            services.RemoveAll<IAgentModel>();
            services.AddSingleton<IAgentModel, FakeAgentModel>();
        });
    }

    private sealed class FakeLlm : ILlmService
    {
        public Task<LlmChatResponse> ChatAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(new LlmChatResponse("fake answer", 1, 1, 1));

        public Task<GroundedGenerationResult> GenerateGroundedAsync(RagGenerationPrompt prompt,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GroundedGenerationResult("The client sets the Bearer header.",
                "It uses the stored token.", [prompt.IncludedChunkIds[0]], "fake-llm", 10, 5, 1));
    }

    // Supports the first candidate unless the question names an absent topic.
    private sealed class FakeVerifier : IEvidenceVerificationService
    {
        public Task<EvidenceVerificationResult> VerifyAsync(string query,
            IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken) =>
            Task.FromResult(query.Contains("Stripe", StringComparison.OrdinalIgnoreCase)
                ? new EvidenceVerificationResult(EvidenceVerificationStatus.InsufficientEvidence, false, [], "none")
                : new EvidenceVerificationResult(EvidenceVerificationStatus.Supported, true, [candidates[0].ChunkId], "ok"));
    }

    private sealed class FakeAgentModel : IAgentModel
    {
        public string ModelName => "fake-agent";

        public Task<AgentModelResult> CompleteJsonAsync(AgentModelRequest request, CancellationToken cancellationToken)
        {
            var evidence = request.UserContent.Split('\n').First(line => line.StartsWith("[E1]"));
            var start = int.Parse(evidence.Split(" lines ")[1].Split('-')[0]);
            var severity = request.SystemInstruction.Contains("Review Agent") ? "\"severity\":\"low\"," : "";
            var json = $$"""
                {"sufficient":true,"summary":"Fake synthesis.","findings":[
                  {"title":"Finding","detail":"Cited line.",{{severity}}"evidenceId":"E1","startLine":{{start}},"endLine":{{start}}}]}
                """;
            return Task.FromResult(new AgentModelResult(JsonDocument.Parse(json).RootElement.Clone(), ModelName, 1, 1));
        }
    }
}

public sealed class ApiIntegrationTests(RepoMindApiFactory factory) : IClassFixture<RepoMindApiFactory>, IAsyncLifetime
{
    private const string ApiClient =
        "export const apiClient = {\n  get(url: string, token: string) {\n" +
        "    const headers = new Headers()\n    headers.set('Authorization', `Bearer ${token}`)\n" +
        "    return fetch(url, { headers })\n  }\n}\n";
    private readonly HttpClient client = factory.CreateClient();
    private readonly List<long> repositories = [];

    [Fact]
    public async Task System_info_reports_the_active_provider_without_secrets()
    {
        var info = await client.GetFromJsonAsync<JsonElement>("/api/system/info");
        Assert.Equal("ollama", info.GetProperty("provider").GetString());
        Assert.Equal("embeddinggemma", info.GetProperty("embeddingModel").GetString());
        Assert.False(info.GetProperty("rerankingEnabled").GetBoolean());
        Assert.DoesNotContain("token", info.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_lists_browses_and_searches_only_the_uploaded_repository()
    {
        var first = await UploadAsync(("app/src/apiClient.ts", ApiClient), ("app/README.md", "# App\n"),
            ("app/node_modules/x/index.js", "ignored"), ("app/logo.png", "ignored"));
        var second = await UploadAsync(("other/src/internal.ts", "export const privateBearer = 'value'\n"));
        Assert.Equal((2, 2), (first.GetProperty("filesIndexed").GetInt32(), first.GetProperty("filesSkipped").GetInt32()));
        var firstId = first.GetProperty("repositoryId").GetInt64();
        var secondId = second.GetProperty("repositoryId").GetInt64();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/repositories");
        var entry = listed.EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == firstId);
        Assert.Equal(("ollama", "embeddinggemma"),
            (entry.GetProperty("embeddingProvider").GetString(), entry.GetProperty("embeddingModel").GetString()));

        var files = await client.GetFromJsonAsync<JsonElement>($"/api/repositories/{firstId}/files");
        Assert.Equal(["app/README.md", "app/src/apiClient.ts"], files.EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        var content = await client.GetFromJsonAsync<JsonElement>(
            $"/api/repositories/{firstId}/files/content?path={Uri.EscapeDataString("app/src/apiClient.ts")}");
        Assert.Equal("    headers.set('Authorization', `Bearer ${token}`)", content.GetProperty("lines")[3].GetProperty("content").GetString());

        // The other repository's path is invisible through this repository's ID.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            $"/api/repositories/{firstId}/files/content?path={Uri.EscapeDataString("other/src/internal.ts")}")).StatusCode);
        var search = await PostAsync($"/api/repositories/{firstId}/hybrid/search", new { query = "privateBearer Bearer", topK = 10 });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var results = (await search.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.NotEmpty(results.EnumerateArray());
        Assert.All(results.EnumerateArray(), r => Assert.StartsWith("app/", r.GetProperty("filePath").GetString()));
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task Rag_answer_returns_stored_citations_and_abstains_for_unsupported_topics()
    {
        var id = (await UploadAsync(("app/src/apiClient.ts", ApiClient))).GetProperty("repositoryId").GetInt64();

        var answered = await (await PostAsync($"/api/repositories/{id}/rag/answer",
            new { query = "Where is the Bearer token added?", topK = 3 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("answered", answered.GetProperty("status").GetString());
        var source = answered.GetProperty("sources")[0];
        Assert.Equal(("app/src/apiClient.ts", 1, 7), (source.GetProperty("filePath").GetString(),
            source.GetProperty("startLine").GetInt32(), source.GetProperty("endLine").GetInt32()));
        Assert.EndsWith($"[source:{source.GetProperty("chunkId").GetInt64()}]", answered.GetProperty("answer").GetString());

        var abstained = await (await PostAsync($"/api/repositories/{id}/rag/answer",
            new { query = "Where is Stripe payment processing implemented?", topK = 3 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_evidence", abstained.GetProperty("status").GetString());
        Assert.Empty(abstained.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task Agent_endpoints_run_persist_and_scope_traces_by_repository()
    {
        var id = (await UploadAsync(("app/src/apiClient.ts", ApiClient))).GetProperty("repositoryId").GetInt64();
        var otherId = (await UploadAsync(("b/a.ts", "export const a = 1\n"))).GetProperty("repositoryId").GetInt64();

        var review = await (await PostAsync($"/api/repositories/{id}/agents/run",
            new { task = "Review apiClient.ts" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("review", "completed"), (review.GetProperty("route").GetString(), review.GetProperty("status").GetString()));
        Assert.Equal("app/src/apiClient.ts", review.GetProperty("findings")[0].GetProperty("citations")[0].GetProperty("filePath").GetString());
        var runId = review.GetProperty("runId").GetInt64();

        var unsupported = await (await PostAsync($"/api/repositories/{id}/agents/run",
            new { task = "Delete apiClient.ts" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unsupported", unsupported.GetProperty("status").GetString());

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/repositories/{id}/agents/runs");
        Assert.Equal(2, history.GetArrayLength());
        var stored = await client.GetFromJsonAsync<JsonElement>($"/api/repositories/{id}/agents/runs/{runId}");
        Assert.Equal(review.GetProperty("trace").GetArrayLength(), stored.GetProperty("trace").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/repositories/{otherId}/agents/runs/{runId}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/api/repositories/{otherId}/agents/runs")).GetArrayLength());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task Invalid_repository_ids_return_400(string id)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/repositories/{id}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/repositories/{id}/agents/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync($"/api/repositories/{id}/rag/answer", new { query = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync($"/api/repositories/{id}/agents/run", new { task = "x" })).StatusCode);
    }

    [Fact]
    public async Task Unknown_repositories_return_404_and_invalid_bodies_return_400()
    {
        const long missing = 9_000_000_000_000;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/repositories/{missing}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/repositories/{missing}/agents/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/repositories/{missing}/agents/runs/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"/api/repositories/{missing}/rag/answer", new { query = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"/api/repositories/{missing}/agents/run", new { task = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/repositories/1/agents/run", new { task = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/repositories/1/agents/run", new { task = new string('a', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/repositories/1/rag/answer", new { query = "x", topK = 6 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/repositories/1/files/content?path=../etc/passwd")).StatusCode);
    }

    [Fact]
    public async Task Unsafe_unsupported_and_oversized_uploads_are_rejected_without_storing_rows()
    {
        var tag = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync(Zip(("../evil.ts", "x")), $"evil-{tag}.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync(Zip(("src/C:/evil.ts", "x")), $"evil2-{tag}.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync(Zip(("image.png", "x"), (".env", "SECRET=1")), $"none-{tag}.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadRawAsync(Zip(("a.ts", "x")), $"repo-{tag}.tar")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync([1, 2, 3, 4], $"broken-{tag}.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync([], $"empty-{tag}.zip")).StatusCode);
        var oversized = new byte[RepositoryZipReader.MaxUploadBytes + 1];
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadRawAsync(oversized, $"big-{tag}.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await client.PostAsJsonAsync("/api/repositories/upload", new { file = "a" })).StatusCode);
        Assert.Equal(0, await RepositoryCountAsync(tag));
    }

    [Fact]
    public async Task Provider_failure_maps_to_502_and_leaves_no_partial_upload()
    {
        var id = (await UploadAsync(("app/src/apiClient.ts", ApiClient))).GetProperty("repositoryId").GetInt64();
        var tag = Guid.NewGuid().ToString("N");
        factory.Embeddings.Failure = new HttpRequestException("embedding provider down");
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await PostAsync($"/api/repositories/{id}/rag/answer",
                new { query = "Where is the Bearer token added?" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadGateway, (await UploadRawAsync(Zip(("a.ts", "export const a = 1")), $"fails-{tag}.zip")).StatusCode);
            var run = await (await PostAsync($"/api/repositories/{id}/agents/run",
                new { task = "Explain how the Bearer header is attached" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("failed", run.GetProperty("status").GetString());
        }
        finally { factory.Embeddings.Failure = null; }
        Assert.Equal(0, await RepositoryCountAsync(tag));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var dataSource = TestDatabase.Create();
        await TestDatabase.CleanupAsync(dataSource, repositories);
    }

    private async Task<JsonElement> UploadAsync(params (string Path, string Content)[] files)
    {
        var response = await UploadRawAsync(Zip(files), $"api-test-{Guid.NewGuid():N}.zip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private sealed record UploadResult(HttpStatusCode StatusCode, string Body);

    private async Task<UploadResult> UploadRawAsync(byte[] bytes, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", fileName);
        using var response = await client.PostAsync("/api/repositories/upload", form);
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.OK)
            repositories.Add(JsonDocument.Parse(body).RootElement.GetProperty("repositoryId").GetInt64());
        return new UploadResult(response.StatusCode, body);
    }

    private Task<HttpResponseMessage> PostAsync(string url, object body) => client.PostAsJsonAsync(url, body);

    private static byte[] Zip(params (string Path, string Content)[] files) =>
        RepositoryUploadIntegrationTests.Zip(files).ToArray();

    // Counts only this test's uniquely named uploads; other test classes run in parallel.
    private static async Task<long> RepositoryCountAsync(string tag)
    {
        await using var dataSource = TestDatabase.Create();
        return await TestDatabase.CountAsync(dataSource,
            "SELECT count(*) FROM repositories WHERE name LIKE $1", "%" + tag);
    }
}
