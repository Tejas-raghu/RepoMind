using System.Text.Json.Serialization;

namespace RepoMind.Api.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceVerificationStatus>))]
public enum EvidenceVerificationStatus
{
    [JsonStringEnumMemberName("supported")] Supported,
    [JsonStringEnumMemberName("insufficient_evidence")] InsufficientEvidence,
    [JsonStringEnumMemberName("uncertain")] Uncertain,
    [JsonStringEnumMemberName("verification_unavailable")] VerificationUnavailable
}

public sealed record EvidenceVerificationRequest(string? Query, int? TopK)
{
    public Dictionary<string, string[]> Validate(long repositoryId)
    {
        var errors = new RepositorySearchRequest(Query, TopK ?? 5).Validate(repositoryId);
        if (TopK > 5) errors["topK"] = ["topK must be between 1 and 5."];
        return errors;
    }
}

public sealed record EvidenceVerificationResult(EvidenceVerificationStatus Status, bool Answerable,
    IReadOnlyList<long> EvidenceChunkIds, string Reason);

public sealed record EvidenceSource(long ChunkId, string FilePath, int StartLine, int EndLine);

public sealed record EvidenceVerificationResponse(long RepositoryId, string Query,
    EvidenceVerificationStatus Status, bool Answerable, IReadOnlyList<long> EvidenceChunkIds,
    IReadOnlyList<EvidenceSource> Sources, string Reason)
{
    public double? VerifierDurationMs { get; init; }
    public string RerankingStatus { get; init; } = "disabled";
}
