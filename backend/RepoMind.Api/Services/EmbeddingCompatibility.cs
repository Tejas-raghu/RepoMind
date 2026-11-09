using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class EmbeddingModelMismatchException(string message) : Exception(message);

public static class EmbeddingCompatibility
{
    public static string Column(string model, int dimensions)
    {
        if (model == "@cf/qwen/qwen3-embedding-0.6b" && dimensions == 1024)
            return "embedding_cf";
        if (!model.StartsWith("@cf/", StringComparison.Ordinal) && dimensions == 768)
            return "embedding";
        throw new InvalidDataException("Embedding model or vector dimension is not supported by the database schema.");
    }

    public static void Validate(EmbeddingResponse result)
    {
        if (result.Embedding.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Embedding contains a non-finite value.");
        _ = Column(result.Model, result.Dimensions);
        if (result.Provider is not ("ollama" or "cloudflare") ||
            result.Provider == "cloudflare" && !result.Model.StartsWith("@cf/", StringComparison.Ordinal) ||
            result.Provider == "ollama" && result.Model.StartsWith("@cf/", StringComparison.Ordinal))
            throw new InvalidDataException("Embedding provider and model do not match.");
    }
}
