using Npgsql;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class RepositoryCatalogTests
{
    [Fact]
    public async Task Lists_only_completed_repositories_with_stored_names()
    {
        var connectionString = Environment.GetEnvironmentVariable("REPOMIND_TEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=repomind;Username=repomind;Password=localdev";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var completedName = "day12-list-" + Guid.NewGuid().ToString("N");
        var pendingName = "day12-pending-" + Guid.NewGuid().ToString("N");
        var ids = new List<long>();
        try
        {
            ids.Add(await InsertAsync(dataSource, completedName, "completed"));
            ids.Add(await InsertAsync(dataSource, pendingName, "pending"));
            var list = await new RepositoryCatalog(dataSource).ListAsync(CancellationToken.None);
            Assert.Contains(list, item => item.Id == ids[0] && item.Name == completedName);
            Assert.DoesNotContain(list, item => item.Id == ids[1]);
        }
        finally
        {
            foreach (var id in ids)
            {
                await using var command = dataSource.CreateCommand("DELETE FROM repositories WHERE id = $1");
                command.Parameters.AddWithValue(id);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<long> InsertAsync(NpgsqlDataSource dataSource, string name, string status)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repositories (name, status) VALUES ($1, $2) RETURNING id");
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(status);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
    }
}
