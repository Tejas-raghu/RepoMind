using RepoMind.Api.Contracts;

namespace RepoMind.Api.Tests;

public sealed class EvidenceVerificationRequestTests
{
    [Theory]
    [InlineData(0, "query", null, "repositoryId")]
    [InlineData(1, " ", null, "query")]
    [InlineData(1, "query", 0, "topK")]
    [InlineData(1, "query", 6, "topK")]
    public void Rejects_invalid_request(long id, string query, int? topK, string field) =>
        Assert.Contains(field, new EvidenceVerificationRequest(query, topK).Validate(id).Keys);

    [Fact]
    public void Defaults_to_five_and_limits_query_length()
    {
        Assert.Empty(new EvidenceVerificationRequest("question", null).Validate(1));
        Assert.Contains("query", new EvidenceVerificationRequest(new string('x', 501), 5).Validate(1).Keys);
    }
}
