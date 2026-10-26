using RepoMind.Api.Contracts;

namespace RepoMind.Api.Tests;

public sealed class RagContextRequestTests
{
    [Fact]
    public void Valid_request_has_no_errors()
    {
        Assert.Empty(new RagContextRequest("Where is authentication?", 5).Validate(1));
    }

    [Theory]
    [InlineData(0, "ok", 5, "repositoryId")]
    [InlineData(-1, "ok", 5, "repositoryId")]
    [InlineData(1, null, 5, "query")]
    [InlineData(1, "  ", 5, "query")]
    [InlineData(1, "ok", null, "topK")]
    [InlineData(1, "ok", 0, "topK")]
    [InlineData(1, "ok", 11, "topK")]
    public void Rejects_invalid_input(long repositoryId, string? query, int? topK, string field)
    {
        Assert.Contains(field, new RagContextRequest(query, topK).Validate(repositoryId).Keys);
    }

    [Fact]
    public void Limits_query_to_five_hundred_characters()
    {
        Assert.Empty(new RagContextRequest(new string('a', 500), 10).Validate(1));
        Assert.Contains("query", new RagContextRequest(new string('a', 501), 1).Validate(1).Keys);
    }
}
