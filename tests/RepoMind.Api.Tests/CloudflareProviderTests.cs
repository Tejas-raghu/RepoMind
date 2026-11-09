using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class CloudflareProviderTests
{
    [Fact]
    public async Task Embeddings_use_documented_text_body_and_require_finite_1024_vectors()
    {
        var vector = new float[1024];
        vector[0] = 1;
        var handler = new StubHandler(_ => Reply(new { success = true,
            result = new { data = new[] { vector }, shape = new[] { 1, 1024 } } }));
        var service = new CloudflareEmbeddingService(Client(handler));
        var result = await service.GenerateAsync("auth code", CancellationToken.None);
        Assert.Equal("cloudflare", result.Provider);
        Assert.Equal(1024, result.Dimensions);
        Assert.Contains("/ai/run/@cf/qwen/qwen3-embedding-0.6b", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("auth code", body.RootElement.GetProperty("text")[0].GetString());
        Assert.Equal("embedding_cf", EmbeddingCompatibility.Column(result.Model, result.Dimensions));

        var bad = new StubHandler(_ => Reply(new { success = true,
            result = new { data = new[] { new[] { 1f, 2f } } } }));
        await Assert.ThrowsAsync<JsonException>(() =>
            new CloudflareEmbeddingService(Client(bad)).GenerateAsync("auth", CancellationToken.None));
    }

    [Fact]
    public async Task Chat_and_verifier_parse_cloudflare_content_and_validate_evidence_ids()
    {
        var chat = new StubHandler(_ => Reply(new { success = true,
            result = new { response = "hello", usage = new { prompt_tokens = 4, completion_tokens = 2 } } }));
        var answer = await new CloudflareLlmService(Client(chat)).ChatAsync("hi", CancellationToken.None);
        Assert.Equal("hello", answer.Answer);
        Assert.Equal(4, answer.PromptTokens);
        Assert.Equal(2, answer.GeneratedTokens);
        using (var body = JsonDocument.Parse(chat.Body!))
            Assert.False(body.RootElement.TryGetProperty("think", out _));

        var candidate = new HybridSearchResult(7, "src/api.ts", 1, 3,
            "headers.set('Authorization', token)", 0.5, 1, 1, 0.03);
        var verifier = new StubHandler(_ => Reply(new { success = true,
            result = new { response = "{\"reason\":\"header set\",\"status\":\"supported\",\"evidenceChunkIds\":[7]}" } }));
        var decision = await new CloudflareEvidenceVerificationService(Client(verifier), Config())
            .VerifyAsync("Where is the header set?", [candidate], CancellationToken.None);
        Assert.Equal(EvidenceVerificationStatus.Supported, decision.Status);
        Assert.Equal([7L], decision.EvidenceChunkIds);

        var invalid = new StubHandler(_ => Reply(new { success = true,
            result = new { response = "{\"reason\":\"wrong\",\"status\":\"supported\",\"evidenceChunkIds\":[99]}" } }));
        await Assert.ThrowsAsync<JsonException>(() =>
            new CloudflareEvidenceVerificationService(Client(invalid), Config())
                .VerifyAsync("Where is the header set?", [candidate], CancellationToken.None));
    }

    [Fact]
    public async Task Reranker_maps_context_indexes_without_changing_original_source()
    {
        var candidates = new[]
        {
            new HybridSearchResult(11, "src/one.ts", 1, 5, new string('a', 1000), 0.5, 1, 1, 0.03),
            new HybridSearchResult(12, "src/two.ts", 1, 5, "target", 0.4, 2, 2, 0.02)
        };
        var handler = new StubHandler(_ => Reply(new { success = true,
            result = new { response = new[] { new { id = 1, score = 3.0 }, new { id = 0, score = -2.0 } } } }));
        var scores = await new CloudflareRerankingService(Client(handler))
            .ScoreAsync("target", candidates, CancellationToken.None);
        Assert.True(scores[12] > scores[11]);
        Assert.Equal(1000, candidates[0].Content.Length);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.GetProperty("contexts")[0].GetProperty("text").GetString()!.Length <= 700);
        Assert.Equal(2, body.RootElement.GetProperty("top_k").GetInt32());
    }

    [Fact]
    public async Task Grounded_generation_preserves_verified_citation_validation()
    {
        var prompt = new RagGenerationPrompt("Use source only.", "Where is Bearer set?",
            "Chunk ID: 7\nheaders.set('Authorization', token)", [7], 0, 100,
            new RagGenerationOptions(4096, 300, 512, 5));
        var handler = new StubHandler(_ => Reply(new { success = true,
            result = new { response = "{\"summary\":\"Bearer is set.\",\"explanation\":\"The header is assigned.\",\"citationChunkIds\":[7]}",
                usage = new { prompt_tokens = 120, completion_tokens = 35 } } }));
        var generated = await new CloudflareLlmService(Client(handler))
            .GenerateGroundedAsync(prompt, CancellationToken.None);
        Assert.Equal([7L], generated.SourceIds);
        Assert.Equal(120, generated.PromptTokens);
        Assert.Equal(35, generated.GeneratedTokens);
        using (var body = JsonDocument.Parse(handler.Body!))
        {
            Assert.False(body.RootElement.TryGetProperty("format", out _));
            Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        }

        var bad = new StubHandler(_ => Reply(new { success = true,
            result = new { response = "{\"summary\":\"wrong\",\"explanation\":\"wrong\",\"citationChunkIds\":[99]}" } }));
        await Assert.ThrowsAsync<JsonException>(() => new CloudflareLlmService(Client(bad))
            .GenerateGroundedAsync(prompt, CancellationToken.None));
    }

    [Fact]
    public async Task Verifier_retries_one_malformed_decision_then_keeps_strict_validation()
    {
        var candidate = new HybridSearchResult(7, "src/api.ts", 1, 3,
            "headers.set('Authorization', token)", 0.5, 1, 1, 0.03);
        var attempts = 0;
        var handler = new StubHandler(_ => Reply(new { success = true,
            result = new { response = ++attempts == 1
                ? "{\"status\":\"supported\",\"evidenceChunkIds\":[99],\"reason\":\"bad\"}"
                : "{\"status\":\"supported\",\"evidenceChunkIds\":[7],\"reason\":\"header\"}" } }));
        var result = await new CloudflareEvidenceVerificationService(Client(handler), Config())
            .VerifyAsync("Where is the header set?", [candidate], CancellationToken.None);
        Assert.Equal(EvidenceVerificationStatus.Supported, result.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Grounded_generation_retries_one_truncated_json_response()
    {
        var prompt = new RagGenerationPrompt("Use source only.", "Where is Bearer set?",
            "Chunk ID: 7\nheaders.set('Authorization', token)", [7], 0, 100,
            new RagGenerationOptions(4096, 1200, 512, 5));
        var attempts = 0;
        var handler = new StubHandler(_ => Reply(new { success = true,
            result = new { response = ++attempts == 1
                ? "{\"summary\":"
                : "{\"summary\":\"header\",\"explanation\":\"assigned\",\"citationChunkIds\":[7]}" } }));
        var result = await new CloudflareLlmService(Client(handler))
            .GenerateGroundedAsync(prompt, CancellationToken.None);
        Assert.Equal([7L], result.SourceIds);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Daily_quota_is_not_retried_and_has_safe_error()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"errors\":[{\"code\":3036,\"message\":\"secret\"}]}")
        });
        var error = await Assert.ThrowsAsync<CloudflareAiException>(() =>
            new CloudflareEmbeddingService(Client(handler)).GenerateAsync("hello", CancellationToken.None));
        Assert.Equal(429, error.StatusCode);
        Assert.Contains("quota", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Temporary_capacity_error_is_retried_with_a_bound()
    {
        var vector = new float[1024];
        vector[0] = 1;
        var attempts = 0;
        var handler = new StubHandler(_ =>
            ++attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("{\"errors\":[{\"code\":3040}]}" ) }
                : Reply(new { success = true, result = new { data = new[] { vector } } }));
        var result = await new CloudflareEmbeddingService(Client(handler))
            .GenerateAsync("hello", CancellationToken.None);
        Assert.Equal(1024, result.Dimensions);
        Assert.Equal(3, handler.Calls);
    }

    private static CloudflareAiClient Client(StubHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.cloudflare.com/") }, Config());

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Cloudflare:AccountId"] = new string('a', 32),
            ["Cloudflare:ApiToken"] = "test-token"
        }).Build();

    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return reply(request);
        }
    }
}
