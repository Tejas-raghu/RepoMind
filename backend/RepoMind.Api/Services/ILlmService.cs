using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface ILlmService
{
    Task<LlmChatResponse> ChatAsync(string prompt, CancellationToken cancellationToken);
    Task<GroundedGenerationResult> GenerateGroundedAsync(string question, string context,
        IReadOnlyList<long> sourceIds, CancellationToken cancellationToken);
}
