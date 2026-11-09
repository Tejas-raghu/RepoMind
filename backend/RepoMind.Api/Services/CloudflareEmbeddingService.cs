using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class CloudflareEmbeddingService(CloudflareAiClient client) : IEmbeddingService
{
    public const string Model = "@cf/qwen/qwen3-embedding-0.6b";
    // Conservative character bound for the model's token window; the API remains authoritative.
    public const int MaxInputCharacters = 8_000;

    public async Task<EmbeddingResponse> GenerateAsync(string text, CancellationToken cancellationToken) =>
        (await GenerateManyAsync([text], cancellationToken))[0];

    public async Task<IReadOnlyList<EmbeddingResponse>> GenerateManyAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0) return [];
        if (texts.Count > 16 || texts.Any(text => string.IsNullOrWhiteSpace(text) ||
            text.Length > MaxInputCharacters))
            throw new InvalidDataException("Cloudflare embedding input must contain 1-16 nonempty texts, each at most 8000 characters.");
        using var document = await client.RunAsync(Model, new { text = texts }, cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() != texts.Count)
            throw new JsonException("Cloudflare returned an invalid embedding batch.");
        if (root.TryGetProperty("shape", out var shape) &&
            (shape.ValueKind != JsonValueKind.Array || shape.GetArrayLength() != 2 ||
             shape[0].ValueKind != JsonValueKind.Number || !shape[0].TryGetInt32(out var rows) || rows != texts.Count ||
             shape[1].ValueKind != JsonValueKind.Number || !shape[1].TryGetInt32(out var columns) || columns != 1024))
            throw new JsonException("Cloudflare returned an invalid embedding shape.");
        var results = new List<EmbeddingResponse>(texts.Count);
        foreach (var vector in data.EnumerateArray())
        {
            if (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() != 1024)
                throw new JsonException("Cloudflare embedding must have 1024 dimensions.");
            var values = new float[1024];
            var index = 0;
            foreach (var item in vector.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number ||
                    !item.TryGetSingle(out var value) || !float.IsFinite(value))
                    throw new JsonException("Cloudflare embedding contains an invalid value.");
                values[index++] = value;
            }
            results.Add(new EmbeddingResponse(Model, values) { Provider = "cloudflare" });
        }
        return results;
    }
}
