using Npgsql;
using Pgvector;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class RepositorySearchStore(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    // Provisional cutoff for the local sample repositories; tune with labeled queries.
    public const double DefaultMinimumSimilarity = 0.35;
    private readonly double minimumSimilarity = configuration.GetValue<double?>(
        "RepositorySearch:MinimumSimilarity") ?? DefaultMinimumSimilarity;

    public async Task<bool> ExistsAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM repositories WHERE id = $1)");
        command.Parameters.AddWithValue(repositoryId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Repository existence query returned no result."));
    }

    public async Task<bool> HasChunksAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM repository_chunks WHERE repository_id = $1)");
        command.Parameters.AddWithValue(repositoryId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Repository chunk query returned no result."));
    }

    public async Task<IReadOnlyList<RepositorySearchResult>> SearchAsync(
        long repositoryId, string model, float[] embedding, int topK,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, source_path, start_line, end_line, content, " +
            "1 - (embedding <=> $1) AS similarity " +
            "FROM repository_chunks " +
            "WHERE repository_id = $2 AND model = $3 " +
            "AND 1 - (embedding <=> $1) >= $5 " +
            "ORDER BY embedding <=> $1 ASC, id ASC LIMIT $4");
        command.Parameters.AddWithValue(new Vector(embedding));
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(model);
        command.Parameters.AddWithValue(topK);
        command.Parameters.AddWithValue(minimumSimilarity);

        var results = new List<RepositorySearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RepositorySearchResult(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetDouble(5)));
        }
        return results;
    }
}
