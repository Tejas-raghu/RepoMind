namespace RepoMind.Api.Contracts;

public sealed record RagAnswerRequest(string? Query, int? TopK)
{
    public Dictionary<string, string[]> Validate(long repositoryId) =>
        new EvidenceVerificationRequest(Query, TopK).Validate(repositoryId);
}

public sealed record RagAnswerSource(long ChunkId, string FilePath, int StartLine,
    int EndLine, string Content);

public sealed record RagAnswerResponse(string Status, string Answer,
    IReadOnlyList<RagAnswerSource> Sources, string? Model, int? PromptTokens,
    int? GeneratedTokens)
{
    public double? DurationMs { get; init; }
    public double? VerifierDurationMs { get; init; }
    public string? RerankingStatus { get; init; }
}

public sealed record GroundedGenerationResult(string Answer, IReadOnlyList<long> SourceIds,
    string Model, int? PromptTokens, int? GeneratedTokens, double? DurationMs);
