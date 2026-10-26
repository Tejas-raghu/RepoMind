using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class OllamaRerankingServiceTests
{
    private static readonly HybridSearchResult[] Candidates =
    [
        new(1, "README.md", 1, 10, "Overview", 0.8, 1, 2, 0.032),
        new(2, "src/apiClient.ts", 3, 8, "headers.set('Authorization', `Bearer ${token}`)", 0.4, 3, 1, 0.031)
    ];

    [Fact]
    public async Task Sends_bounded_untrusted_code_with_schema_and_parses_scores()
    {
        var handler = new Handler(Envelope("""{"scores":[{"chunkId":1,"score":20},{"chunkId":2,"score":95,"justification":"Sets header"}]}"""));
        var scores = await Create(handler).ScoreAsync("Where is Bearer added?", Candidates, CancellationToken.None);
        Assert.Equal(95, scores[2]);
        Assert.Equal(20, scores[1]);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        var root = request.RootElement;
        Assert.Equal("qwen3:4b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal("object", root.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(2, root.GetProperty("format").GetProperty("properties").GetProperty("scores").GetProperty("minItems").GetInt32());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        var prompt = root.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("Where is Bearer added?", prompt);
        Assert.Contains("src/apiClient.ts", prompt);
        Assert.Contains("Bearer", prompt);
        Assert.True(prompt.Length <= 2_000);
        Assert.Contains("untrusted data", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"scores\":[{\"chunkId\":1,\"score\":40},{\"chunkId\":99,\"score\":80}]}")]
    [InlineData("{\"scores\":[{\"chunkId\":1,\"score\":40},{\"chunkId\":1,\"score\":80}]}")]
    [InlineData("{\"scores\":[{\"chunkId\":1,\"score\":40}]}")]
    [InlineData("{\"scores\":[{\"chunkId\":1,\"score\":-1},{\"chunkId\":2,\"score\":80}]}")]
    [InlineData("{\"scores\":[{\"chunkId\":1,\"score\":101},{\"chunkId\":2,\"score\":80}]}")]
    [InlineData("{\"scores\":[{\"chunkId\":1,\"score\":1e999},{\"chunkId\":2,\"score\":80}]}")]
    public async Task Rejects_invalid_json_ids_or_scores(string output)
    {
        var service = Create(new Handler(Envelope(output)));
        await Assert.ThrowsAnyAsync<JsonException>(() => service.ScoreAsync("question", Candidates, CancellationToken.None));
    }

    [Fact]
    public void Prompt_caps_long_source_content()
    {
        var source = Candidates[0] with { Content = new string('x', 30_000) };
        var prompt = OllamaRerankingService.BuildPrompt("question", [source, Candidates[1]], 2_000);
        Assert.True(prompt.Length <= 2_000);
        Assert.Contains("src/apiClient.ts", prompt);
    }

    private static OllamaRerankingService Create(Handler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:ChatModel"] = "qwen3:4b", ["Reranking:PromptMaxCharacters"] = "2000"
        }).Build();
        return new OllamaRerankingService(client, config);
    }

    private static string Envelope(string content) => JsonSerializer.Serialize(new
    { message = new { role = "assistant", content }, done = true });

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("http://localhost:11434/api/chat", request.RequestUri?.ToString());
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
