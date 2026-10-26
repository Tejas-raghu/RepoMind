using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface IEvidenceVerificationService
{
    Task<EvidenceVerificationResult> VerifyAsync(string query,
        IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken);
}
