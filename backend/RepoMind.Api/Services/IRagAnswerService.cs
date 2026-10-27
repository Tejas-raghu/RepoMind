using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface IRagAnswerService
{
    Task<RagAnswerResponse?> AnswerAsync(long repositoryId, string query, int topK,
        CancellationToken cancellationToken);
}
