using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class EmbeddingServiceTests
{
    [Fact]
    public async Task Batch_embeddings_keep_the_input_order()
    {
        var handler = new StubHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Ollama:EmbeddingModel"] = "embeddinggemma" }).Build();
        var service = new EmbeddingService(client, configuration);

        var result = await service.GenerateManyAsync(["first", "second"], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(1f, result[0].Embedding[0]);
        Assert.Equal(1f, result[1].Embedding[1]);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("first", request.RootElement.GetProperty("input")[0].GetString());
        Assert.Equal("second", request.RootElement.GetProperty("input")[1].GetString());
        Assert.False(request.RootElement.GetProperty("truncate").GetBoolean());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"embeddings\":[[1,0],[0,1]]}", Encoding.UTF8, "application/json")
            };
        }
    }
}
