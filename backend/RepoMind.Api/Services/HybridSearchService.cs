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

    private static readonly HashSet<string> QuestionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "added", "attach", "attaches", "code", "created", "decides", "defined",
        "function", "identified", "implemented", "method", "secured", "uses"
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
        var prioritized = PrioritizeImplementation(query, fused);
        if (!rerankingEnabled || prioritized.Count == 0)
            return new HybridSearchResponse(repositoryId, query, prioritized.Take(topK).ToArray())
            { RerankingStatus = rerankingEnabled ? "no_results" : "disabled" };

        var candidates = prioritized.Take(rerankingCandidates).ToArray();
        try
        {
            var scores = await reranker.ScoreAsync(query, candidates, cancellationToken);
            var ordered = Rerank(prioritized, scores, candidates.Length, topK);
            if (environment.IsDevelopment())
                logger.LogInformation("Reranking rank comparison for repository {RepositoryId}: RRF IDs {Before}; prioritized IDs {Prioritized}; final IDs {After}",
                    repositoryId, string.Join(',', fused.Take(topK).Select(x => x.ChunkId)),
                    string.Join(',', prioritized.Take(topK).Select(x => x.ChunkId)),
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
            prioritized.Take(topK).ToArray()) { RerankingStatus = status };
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

    // Code exploration questions should surface implementation before prose or
    // dependency manifests. A matching declaration beats a nearby call site.
    public static IReadOnlyList<HybridSearchResult> PrioritizeImplementation(
        string query, IReadOnlyList<HybridSearchResult> fused)
    {
        if (Regex.IsMatch(query, @"\b(readme|documentation|docs?)\b", RegexOptions.IgnoreCase))
            return fused;
        // Prefer the most specific subject word over common question verbs.
        // Scope words have a separate path signal below.
        var focus = ExtractTerms(query)
            .Where(term => term is not ("backend" or "server" or "frontend" or "client") &&
                !QuestionWords.Contains(term))
            .OrderByDescending(term => term.Length).FirstOrDefault();
        var backend = Regex.IsMatch(query, @"\b(backend|server)\b", RegexOptions.IgnoreCase);
        var frontend = Regex.IsMatch(query, @"\b(frontend|client)\b", RegexOptions.IgnoreCase);
        return fused.OrderByDescending(result => IsCode(result.FilePath))
            .ThenByDescending(result => result.RrfScore +
                (HasMatchingDeclaration(result.Content, focus) ? 0.002 : 0) +
                (IsCode(result.FilePath) && result.LexicalRank == 1 ? 0.0004 : 0) +
                (backend && IsBackend(result.FilePath) || frontend && IsFrontend(result.FilePath)
                    ? 0.001 : 0))
            .ThenByDescending(result => result.RrfScore)
            .ThenBy(result => result.ChunkId)
            .ToArray();
    }

    private static bool IsCode(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".vue";

    private static bool IsBackend(string path) => path.Contains("/server/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/backend/", StringComparison.OrdinalIgnoreCase);

    private static bool IsFrontend(string path) => path.Contains("/src/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/frontend/", StringComparison.OrdinalIgnoreCase);

    private static bool HasMatchingDeclaration(string content, string? focus)
    {
        if (focus is null) return false;
        var declarations = Regex.Matches(content,
            @"\b(?:export\s+)?(?:async\s+)?function\s+([A-Za-z_]\w*)\s*\(|" +
            @"(?m)^\s*(?:public|private|internal|protected)\s+[^;\n]*?\b([A-Za-z_]\w*)\s*\(");
        foreach (Match declaration in declarations)
        {
            var name = declaration.Groups[1].Success ? declaration.Groups[1].Value : declaration.Groups[2].Value;
            var words = Regex.Matches(name, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+")
                .Select(match => match.Value.ToLowerInvariant());
            if (words.Contains(focus)) return true;
        }
        return false;
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
