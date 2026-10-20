using RepoMind.Api.Contracts;

namespace RepoMind.Api.Tests;

public sealed class RepositorySearchRequestTests
{
    [Fact]
    public void Valid_request_has_no_errors()
    {
        Assert.Empty(new RepositorySearchRequest("Where is authentication implemented?", 5)
            .Validate(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Repository_id_must_be_positive(long repositoryId)
    {
        var errors = new RepositorySearchRequest("authentication", 5).Validate(repositoryId);
        Assert.Contains("repositoryId", errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Query_must_have_text(string? query)
    {
        var errors = new RepositorySearchRequest(query, 5).Validate(1);
        Assert.Contains("query", errors.Keys);
    }

    [Fact]
    public void Query_cannot_exceed_five_hundred_characters()
    {
        var errors = new RepositorySearchRequest(new string('a', 501), 5).Validate(1);
        Assert.Contains("query", errors.Keys);
        Assert.Empty(new RepositorySearchRequest(new string('a', 500), 5).Validate(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(21)]
    public void Top_k_must_be_between_one_and_twenty(int? topK)
    {
        var errors = new RepositorySearchRequest("authentication", topK).Validate(1);
        Assert.Contains("topK", errors.Keys);
        Assert.Empty(new RepositorySearchRequest("authentication", 1).Validate(1));
        Assert.Empty(new RepositorySearchRequest("authentication", 20).Validate(1));
    }
}
