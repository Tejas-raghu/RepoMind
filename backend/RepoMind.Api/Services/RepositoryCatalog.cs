using Npgsql;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class RepositoryCatalog(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<RepositorySummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, name FROM repositories WHERE status = 'completed' ORDER BY id DESC");
        var repositories = new List<RepositorySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            repositories.Add(new RepositorySummary(reader.GetInt64(0), reader.GetString(1)));
        return repositories;
    }
}
