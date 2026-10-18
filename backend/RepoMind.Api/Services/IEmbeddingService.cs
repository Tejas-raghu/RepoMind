using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface IEmbeddingService
{
    Task<EmbeddingResponse> GenerateAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<EmbeddingResponse>> GenerateManyAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
