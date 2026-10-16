using Npgsql;
using Pgvector;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class EmbeddingStore(NpgsqlDataSource dataSource)
{
    public async Task<long> SaveAsync(string content, string model, float[] embedding,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO stored_embeddings (content, model, embedding) " +
            "VALUES ($1, $2, $3) RETURNING id");
        command.Parameters.AddWithValue(content);
        command.Parameters.AddWithValue(model);
        command.Parameters.AddWithValue(new Vector(embedding));

        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The insert returned no ID."));
    }

    public async Task<IReadOnlyList<SimilarEmbeddingResponse>> SearchAsync(
        string model, float[] embedding, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, content, model, 1 - (embedding <=> $1) AS similarity " +
            "FROM stored_embeddings WHERE model = $2 " +
            "ORDER BY embedding <=> $1 LIMIT 1");
        command.Parameters.AddWithValue(new Vector(embedding));
        command.Parameters.AddWithValue(model);

        var matches = new List<SimilarEmbeddingResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add(new SimilarEmbeddingResponse(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3)));
        }

        return matches;
    }
}
