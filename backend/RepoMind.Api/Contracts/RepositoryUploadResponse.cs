namespace RepoMind.Api.Contracts;

public sealed record RepositoryUploadResponse(
    long RepositoryId, string Status, int FilesIndexed, int ChunksCreated, int FilesSkipped);
