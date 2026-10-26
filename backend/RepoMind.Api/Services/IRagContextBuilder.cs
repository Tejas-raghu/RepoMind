using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface IRagContextBuilder
{
    Task<RagContextResponse?> BuildAsync(long repositoryId, string query, int topK,
        CancellationToken cancellationToken);
}
