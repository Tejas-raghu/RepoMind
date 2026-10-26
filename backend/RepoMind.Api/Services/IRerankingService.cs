using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface IRerankingService
{
    Task<IReadOnlyDictionary<long, double>> ScoreAsync(string query,
        IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken);
}
