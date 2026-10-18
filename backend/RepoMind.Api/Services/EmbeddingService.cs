using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class EmbeddingService(HttpClient httpClient, IConfiguration configuration) : IEmbeddingService
{
    private readonly string model = configuration["Ollama:EmbeddingModel"]
        ?? throw new InvalidOperationException("Ollama:EmbeddingModel is required.");

    public async Task<EmbeddingResponse> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        // Reject overlong input rather than silently embedding only part of the text.
        var request = new OllamaEmbedRequest(model, text, Truncate: false);
        using var response = await httpClient.PostAsJsonAsync("api/embed", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken);
        // /api/embed returns one vector per input, including for a single string.
        if (result?.Embeddings is not { Length: 1 } || result.Embeddings[0] is not { Length: > 0 } embedding
            || embedding.Any(value => !float.IsFinite(value)))
        {
            throw new JsonException("Ollama returned an invalid embedding.");
        }

        return new EmbeddingResponse(model, embedding);
    }

    public async Task<IReadOnlyList<EmbeddingResponse>> GenerateManyAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0) return [];

        var request = new OllamaBatchEmbedRequest(model, texts, Truncate: false);
        using var response = await httpClient.PostAsJsonAsync("api/embed", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken);
        if (result?.Embeddings is not { } vectors || vectors.Length != texts.Count ||
            vectors.Any(vector => vector is not { Length: > 0 } ||
                vector.Any(value => !float.IsFinite(value))))
        {
            throw new JsonException("Ollama returned invalid batch embeddings.");
        }

        return vectors.Select(vector => new EmbeddingResponse(model, vector)).ToArray();
    }

    private sealed record OllamaEmbedRequest(string Model, string Input, bool Truncate);
    private sealed record OllamaBatchEmbedRequest(string Model, IReadOnlyList<string> Input, bool Truncate);
    private sealed record OllamaEmbedResponse(float[][]? Embeddings);
}
