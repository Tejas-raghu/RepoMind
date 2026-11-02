using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public interface ILlmService
{
    Task<LlmChatResponse> ChatAsync(string prompt, CancellationToken cancellationToken);
    Task<GroundedGenerationResult> GenerateGroundedAsync(RagGenerationPrompt prompt,
        CancellationToken cancellationToken);
}
