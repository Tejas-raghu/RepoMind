using System.Text.RegularExpressions;
using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class HybridSearchService(
    RepositorySearchStore store, IEmbeddingService embeddings, IRerankingService reranker,
    IConfiguration configuration, IHostEnvironment environment, ILogger<HybridSearchService> logger)
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "at", "by", "does", "for", "from", "how", "in", "is",
        "of", "on", "the", "to", "what", "where", "which", "with"
    };

    private readonly int vectorCandidates = CandidateCount(configuration, "HybridSearch:VectorCandidates");
    private readonly int lexicalCandidates = CandidateCount(configuration, "HybridSearch:LexicalCandidates");
    private readonly bool rerankingEnabled = configuration.GetValue<bool>("Reranking:Enabled");
    private readonly int rerankingCandidates = RerankingCandidateCount(configuration);

    public async Task<HybridSearchResponse?> SearchAsync(long repositoryId, string query,
        int topK, CancellationToken cancellationToken)
    {
        if (!await store.ExistsAsync(repositoryId, cancellationToken)) return null;
        if (!await store.HasChunksAsync(repositoryId, cancellationToken))
            return new HybridSearchResponse(repositoryId, query, []);

        var embedding = await embeddings.GenerateAsync(query, cancellationToken);
        var vector = await store.SearchVectorCandidatesAsync(repositoryId, embedding.Model,
            embedding.Embedding, vectorCandidates, cancellationToken);
        var lexical = await store.SearchLexicalCandidatesAsync(repositoryId, embedding.Model,
            embedding.Embedding, ExtractTerms(query), lexicalCandidates, cancellationToken);
        // Fuse and deduplicate before asking the LLM to reorder candidates.
        var fused = Fuse(vector, lexical, Math.Max(topK, rerankingCandidates));
        if (!rerankingEnabled || fused.Count == 0)
            return new HybridSearchResponse(repositoryId, query, fused.Take(topK).ToArray())
            { RerankingStatus = rerankingEnabled ? "no_results" : "disabled" };

        var candidates = fused.Take(rerankingCandidates).ToArray();
        try
        {
            var scores = await reranker.ScoreAsync(query, candidates, cancellationToken);
            var ordered = Rerank(fused, scores, candidates.Length, topK);
            if (environment.IsDevelopment())
                logger.LogInformation("Reranking rank comparison for repository {RepositoryId}: RRF IDs {Before}; final IDs {After}",
                    repositoryId, string.Join(',', fused.Take(topK).Select(x => x.ChunkId)),
                    string.Join(',', ordered.Select(x => x.ChunkId)));
            return new HybridSearchResponse(repositoryId, query, ordered) { RerankingStatus = "applied" };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fallback("timeout");
        }
        catch (HttpRequestException)
        {
            return Fallback("unavailable");
        }
        catch (JsonException)
        {
            return Fallback("invalid_response");
        }

        HybridSearchResponse Fallback(string status) => new(repositoryId, query,
            fused.Take(topK).ToArray()) { RerankingStatus = status };
    }

    public static string[] ExtractTerms(string query) => Regex.Matches(query, "[A-Za-z][A-Za-z0-9_]*|[0-9]+")
        .Select(match => match.Value.ToLowerInvariant())
        .Where(term => term.Length > 1 && !StopWords.Contains(term))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<HybridSearchResult> Fuse(
        IReadOnlyList<RepositorySearchResult> vector,
        IReadOnlyList<RepositorySearchResult> lexical, int topK)
    {
        var byId = new Dictionary<long, (RepositorySearchResult Chunk, int? VectorRank,
            int? LexicalRank, double Score)>();
        for (var i = 0; i < vector.Count; i++)
        {
            var chunk = vector[i];
            byId[chunk.ChunkId] = (chunk, i + 1, null, 1.0 / (60 + i + 1));
        }
        for (var i = 0; i < lexical.Count; i++)
        {
            var chunk = lexical[i];
            if (byId.TryGetValue(chunk.ChunkId, out var existing))
                byId[chunk.ChunkId] = (existing.Chunk, existing.VectorRank, i + 1,
                    existing.Score + 1.0 / (60 + i + 1));
            else
                byId[chunk.ChunkId] = (chunk, null, i + 1, 1.0 / (60 + i + 1));
        }

        return byId.Values
            .Select(item => new HybridSearchResult(item.Chunk.ChunkId, item.Chunk.FilePath,
                item.Chunk.StartLine, item.Chunk.EndLine, item.Chunk.Content,
                item.Chunk.Similarity, item.VectorRank, item.LexicalRank, item.Score))
            .OrderByDescending(item => item.RrfScore)
            .ThenBy(item => item.VectorRank ?? int.MaxValue)
            .ThenBy(item => item.LexicalRank ?? int.MaxValue)
            .ThenBy(item => item.ChunkId)
            .Take(topK)
            .ToArray();
    }

    public static IReadOnlyList<HybridSearchResult> Rerank(
        IReadOnlyList<HybridSearchResult> fused, IReadOnlyDictionary<long, double> scores,
        int candidateCount, int topK)
    {
        var candidates = fused.Take(candidateCount).ToArray();
        if (scores.Count != candidates.Length || candidates.Any(x => !scores.TryGetValue(x.ChunkId,
            out var score) || !double.IsFinite(score) || score is < 0 or > 100) ||
            scores.Keys.Except(candidates.Select(x => x.ChunkId)).Any())
            throw new JsonException("Invalid reranking scores.");
        return candidates.OrderByDescending(x => scores[x.ChunkId])
            .ThenByDescending(x => x.RrfScore).ThenBy(x => x.ChunkId)
            .Concat(fused.Skip(candidates.Length)).Take(topK).ToArray();
    }

    private static int CandidateCount(IConfiguration configuration, string key)
    {
        var count = configuration.GetValue<int?>(key) ?? 15;
        if (count is < 1 or > 100)
            throw new InvalidOperationException($"{key} must be between 1 and 100.");
        return count;
    }

    private static int RerankingCandidateCount(IConfiguration configuration)
    {
        var count = configuration.GetValue<int?>("Reranking:MaxCandidates") ?? 8;
        if (count is < 1 or > 8)
            throw new InvalidOperationException("Reranking:MaxCandidates must be between 1 and 8.");
        return count;
    }
}
