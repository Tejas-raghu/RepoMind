using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class OllamaLlmServiceTests
{
    [Fact]
    public async Task Grounded_generation_sends_separate_sections_and_returns_nullable_usage()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"message":{"content":"{\"summary\":\"Bearer is set.\",\"explanation\":\"The client adds it.\",\"citationChunkIds\":[42]}"},"done":true,"prompt_eval_count":75,"eval_count":20}""");
        var prompt = new RagGenerationPromptBuilder(new ConfigurationBuilder().Build()).Build("Bearer?",
            [new RepoMind.Api.Contracts.RepositorySearchResult(42, "src/apiClient.ts", 1, 2, "headers.set('Authorization', token);", 0.8)]);
        var result = await CreateService(handler).GenerateGroundedAsync(prompt, CancellationToken.None);
        Assert.Equal("Bearer is set.", result.Summary);
        Assert.Equal([42L], result.SourceIds);
        Assert.Equal(75, result.PromptTokens);
        Assert.Equal(20, result.GeneratedTokens);
        Assert.True(prompt.EstimatedInputTokens >= result.PromptTokens);
        using var request = JsonDocument.Parse(handler.Body!);
        var root = request.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Contains("untrusted data", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("Question:\nBearer?", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Contains("src/apiClient.ts", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(42, root.GetProperty("format").GetProperty("properties")
            .GetProperty("citationChunkIds").GetProperty("items").GetProperty("enum")[0].GetInt64());
    }

    [Theory]
    [InlineData("""{"message":{"content":"not json"},"done":true}""")]
    [InlineData("""{"message":{"content":"{\"summary\":\"ok\",\"explanation\":\"x\",\"citationChunkIds\":[\"42\"]}"},"done":true}""")]
    [InlineData("""{"message":{"content":"{\"summary\":\"ok\",\"explanation\":\"x\",\"citationChunkIds\":[99]}"},"done":true}""")]
    [InlineData("""{"message":{"content":"{\"summary\":\"ok\",\"explanation\":\"x\",\"citationChunkIds\":[42,42]}"},"done":true}""")]
    [InlineData("[]")]
    public async Task Grounded_generation_rejects_malformed_output(string body)
    {
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var prompt = new RagGenerationPromptBuilder(new ConfigurationBuilder().Build()).Build("Bearer?",
            [new RepoMind.Api.Contracts.RepositorySearchResult(42, "source.cs", 1, 1, "source", 0.8)]);
        await Assert.ThrowsAnyAsync<JsonException>(() => CreateService(handler)
            .GenerateGroundedAsync(prompt, CancellationToken.None));
    }

    [Fact]
    public async Task Sends_chat_settings_and_returns_actual_metadata()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"Dependency injection supplies dependencies."},"done":true,"prompt_eval_count":12,"eval_count":7,"total_duration":1234567890}""");
        var service = CreateService(handler);

        var result = await service.ChatAsync("Explain dependency injection", CancellationToken.None);

        Assert.Equal("Dependency injection supplies dependencies.", result.Answer);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(7, result.GeneratedTokens);
        Assert.Equal(1234.56789, result.DurationMs);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://localhost:11434/api/chat", handler.Url);
        using var request = JsonDocument.Parse(handler.Body!);
        var root = request.RootElement;
        Assert.Equal("qwen3:4b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Explain dependency injection", root.GetProperty("messages")[0].GetProperty("content").GetString());
        var options = root.GetProperty("options");
        Assert.Equal(4096, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(200, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(0.2, options.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":{\"content\":\"ok\"},\"done\":true,\"eval_count\":1,\"total_duration\":100}")]
    [InlineData("{\"message\":{\"content\":\"ok\"},\"done\":true,\"prompt_eval_count\":1,\"eval_count\":-1,\"total_duration\":100}")]
    public async Task Rejects_missing_or_invalid_metadata(string body)
    {
        var service = CreateService(new StubHandler(HttpStatusCode.OK, body));
        await Assert.ThrowsAsync<JsonException>(() => service.ChatAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task Propagates_ollama_http_failure()
    {
        var service = CreateService(new StubHandler(HttpStatusCode.NotFound, "{\"error\":\"model not found\"}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.ChatAsync("hello", CancellationToken.None));
    }

    private static OllamaLlmService CreateService(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Ollama:ChatModel"] = "qwen3:4b" }).Build();
        return new OllamaLlmService(client, configuration);
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
