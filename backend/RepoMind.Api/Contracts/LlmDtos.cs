namespace RepoMind.Api.Contracts;

public sealed record LlmChatRequest(string? Prompt)
{
    public const int MaxPromptLength = 2_000;

    public Dictionary<string, string[]> Validate()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
            return new() { ["prompt"] = ["Prompt must not be empty."] };
        if (Prompt.Length > MaxPromptLength)
            return new() { ["prompt"] = [$"Prompt must be at most {MaxPromptLength} characters."] };
        return new();
    }
}

public sealed record LlmChatResponse(string Answer, int PromptTokens, int GeneratedTokens, double DurationMs);
