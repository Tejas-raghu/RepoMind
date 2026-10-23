using RepoMind.Api.Contracts;

namespace RepoMind.Api.Tests;

public sealed class LlmChatRequestTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Rejects_empty_prompts(string? prompt)
    {
        Assert.Contains("prompt", new LlmChatRequest(prompt).Validate().Keys);
    }

    [Fact]
    public void Enforces_character_limit()
    {
        Assert.Empty(new LlmChatRequest(new string('a', LlmChatRequest.MaxPromptLength)).Validate());
        Assert.Contains("prompt", new LlmChatRequest(new string('a', LlmChatRequest.MaxPromptLength + 1)).Validate().Keys);
    }
}
