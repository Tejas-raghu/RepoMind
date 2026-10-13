namespace RepoMind.Api.Contracts;

public sealed record EmbeddingRequest(string? Text);

public sealed record EmbeddingResponse(string Model, float[] Embedding)
{
    public int Dimensions => Embedding.Length;
}
