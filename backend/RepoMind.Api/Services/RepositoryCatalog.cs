using Npgsql;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class RepositoryCatalog(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<RepositorySummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, name, embedding_provider, embedding_model FROM repositories " +
            "WHERE status = 'completed' ORDER BY id DESC");
        var repositories = new List<RepositorySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            repositories.Add(new RepositorySummary(reader.GetInt64(0), reader.GetString(1))
            {
                EmbeddingProvider = reader.IsDBNull(2) ? null : reader.GetString(2),
                EmbeddingModel = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        return repositories;
    }
}
