using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface IRagContextBuilder
{
    Task<RagContextResponse?> BuildAsync(long repositoryId, string query, int topK,
        CancellationToken cancellationToken);
    RagContextResponse BuildVerified(long repositoryId, string query,
        IReadOnlyList<RepositorySearchResult> verifiedChunks);
}
