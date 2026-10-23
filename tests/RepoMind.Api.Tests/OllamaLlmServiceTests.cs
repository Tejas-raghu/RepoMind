using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class OllamaLlmServiceTests
{
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
