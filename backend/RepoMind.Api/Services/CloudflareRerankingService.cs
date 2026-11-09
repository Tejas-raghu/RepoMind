using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class CloudflareRerankingService(CloudflareAiClient client) : IRerankingService
{
    public const string Model = "@cf/baai/bge-reranker-base";
    // BGE takes at most 512 tokens per query/context pair. This conservative
    // character cap leaves room for the query and metadata; original chunks are unchanged.
    public const int MaxCandidateCharacters = 700;

    public async Task<IReadOnlyDictionary<long, double>> ScoreAsync(string query,
        IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count is < 1 or > 8 ||
            candidates.Select(x => x.ChunkId).Distinct().Count() != candidates.Count)
            throw new ArgumentException("Supply one to eight unique candidates.", nameof(candidates));
        var contexts = candidates.Select(candidate => new
        {
            text = (candidate.FilePath + "\n" + candidate.Content)[..
                Math.Min(MaxCandidateCharacters, candidate.FilePath.Length + 1 + candidate.Content.Length)]
        }).ToArray();
        using var document = await client.RunAsync(Model,
            new { query, contexts, top_k = candidates.Count }, cancellationToken);
        return ParseScores(document.RootElement, candidates);
    }

    public static IReadOnlyDictionary<long, double> ParseScores(JsonElement root,
        IReadOnlyList<HybridSearchResult> candidates)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Array ||
            response.GetArrayLength() != candidates.Count)
            throw new JsonException("Invalid Cloudflare reranking response.");
        var scores = new Dictionary<long, double>();
        foreach (var item in response.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number ||
                !idValue.TryGetInt32(out var index) ||
                index < 0 || index >= candidates.Count ||
                !item.TryGetProperty("score", out var scoreValue) || scoreValue.ValueKind != JsonValueKind.Number ||
                !scoreValue.TryGetDouble(out var raw) || !double.IsFinite(raw))
                throw new JsonException("Invalid Cloudflare reranking score or index.");
            var score = 100.0 / (1.0 + Math.Exp(-raw));
            if (!scores.TryAdd(candidates[index].ChunkId, score))
                throw new JsonException("Duplicate Cloudflare reranking index.");
        }
        return scores;
    }
}
