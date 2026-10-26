namespace RepoMind.Api.Contracts;

public sealed record RagContextRequest(string? Query, int? TopK)
{
    public Dictionary<string, string[]> Validate(long repositoryId)
    {
        var errors = new RepositorySearchRequest(Query, TopK).Validate(repositoryId);
        if (TopK > 10)
            errors["topK"] = ["topK must be between 1 and 10."];
        return errors;
    }
}

public sealed record RagSource(long ChunkId, int SourceNumber, string FilePath,
    int StartLine, int EndLine, double Similarity);

public sealed record RagContextResponse(long RepositoryId, string Query, string Context,
    IReadOnlyList<RagSource> Sources, int ChunksUsed, int ContextCharacters,
    bool ChunksOmittedDueToBudget, bool InsufficientBudget)
{
    public string RerankingStatus { get; init; } = "disabled";
    public EvidenceVerificationResponse? Verification { get; init; }
}
