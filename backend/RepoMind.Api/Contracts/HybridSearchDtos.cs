namespace RepoMind.Api.Contracts;

public sealed record HybridSearchRequest(string? Query, int? TopK)
{
    public Dictionary<string, string[]> Validate(long repositoryId)
    {
        var errors = new RepositorySearchRequest(Query, TopK ?? 5).Validate(repositoryId);
        return errors;
    }
}

public sealed record HybridSearchResult(
    long ChunkId, string FilePath, int StartLine, int EndLine, string Content,
    double CosineSimilarity, int? VectorRank, int? LexicalRank, double RrfScore);

public sealed record HybridSearchResponse(
    long RepositoryId, string Query, IReadOnlyList<HybridSearchResult> Results)
{
    public string RerankingStatus { get; init; } = "disabled";
}
