namespace RepoMind.Api.Contracts;

public sealed record RepositorySummary(long Id, string Name)
{
    // Null for an empty legacy repository that never recorded a provider.
    public string? EmbeddingProvider { get; init; }
    public string? EmbeddingModel { get; init; }
}

public sealed record SystemInfoResponse(string Provider, string EmbeddingModel, string ChatModel,
    bool RerankingEnabled, bool AgentModelPlanning, bool PublicDemo);
