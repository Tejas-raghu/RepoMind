using Npgsql;
using Pgvector;

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
}
