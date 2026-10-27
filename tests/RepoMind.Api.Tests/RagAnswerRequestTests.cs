using RepoMind.Api.Contracts;

namespace RepoMind.Api.Tests;

public sealed class RagAnswerRequestTests
{
    [Fact]
    public void Validates_repository_query_and_top_k()
    {
        Assert.Empty(new RagAnswerRequest("Bearer?", null).Validate(1));
        var errors = new RagAnswerRequest(" ", 6).Validate(0);
        Assert.Contains("repositoryId", errors.Keys);
        Assert.Contains("query", errors.Keys);
        Assert.Contains("topK", errors.Keys);
        Assert.Contains("query", new RagAnswerRequest(new string('x', 501), 1).Validate(1).Keys);
        Assert.Contains("topK", new RagAnswerRequest("Bearer?", 0).Validate(1).Keys);
    }
}
