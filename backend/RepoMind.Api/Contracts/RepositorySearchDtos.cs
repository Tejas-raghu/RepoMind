namespace RepoMind.Api.Contracts;

public sealed record RepositorySearchRequest(string? Query, int? TopK)
{
    public const int MaxQueryLength = 500;

    public Dictionary<string, string[]> Validate(long repositoryId)
    {
        var errors = new Dictionary<string, string[]>();
        if (repositoryId <= 0)
            errors["repositoryId"] = ["Repository ID must be positive."];
        if (string.IsNullOrWhiteSpace(Query))
            errors["query"] = ["Query must not be empty."];
        else if (Query.Length > MaxQueryLength)
            errors["query"] = [$"Query must be at most {MaxQueryLength} characters."];
        if (TopK is null or < 1 or > 20)
            errors["topK"] = ["topK must be between 1 and 20."];
        return errors;
    }
}

public sealed record RepositorySearchResult(
    long ChunkId, string FilePath, int StartLine, int EndLine, string Content, double Similarity);

public sealed record RepositorySearchResponse(
    long RepositoryId, IReadOnlyList<RepositorySearchResult> Results);
