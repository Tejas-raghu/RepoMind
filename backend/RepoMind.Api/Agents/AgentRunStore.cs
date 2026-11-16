using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Agents;

// Stores each completed run as JSON so traces can be reopened without rerunning
// a model. Every query is scoped by repository ID: a run ID from another
// repository behaves exactly like a missing run.
public sealed class AgentRunStore(NpgsqlDataSource dataSource)
{
    public const int MaxListLimit = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(long Id, DateTimeOffset CreatedAt)> SaveAsync(AgentRunResponse response,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO agent_runs (repository_id, task, route, status, summary, duration_ms, response) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id, created_at");
        command.Parameters.AddWithValue(response.RepositoryId);
        command.Parameters.AddWithValue(response.Task);
        command.Parameters.AddWithValue(response.Route);
        command.Parameters.AddWithValue(response.Status);
        command.Parameters.AddWithValue(response.Summary);
        command.Parameters.AddWithValue(response.DurationMs);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(response with { RunId = null, CreatedAt = null }, Json) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Agent run insert returned no ID.");
        return (reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    public async Task<IReadOnlyList<AgentRunSummary>> ListAsync(long repositoryId, int limit,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, task, route, status, summary, duration_ms, created_at FROM agent_runs " +
            "WHERE repository_id = $1 ORDER BY id DESC LIMIT $2");
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, MaxListLimit));
        var runs = new List<AgentRunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            runs.Add(new AgentRunSummary(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetDouble(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        return runs;
    }

    public async Task<AgentRunResponse?> GetAsync(long repositoryId, long runId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT response::text, created_at FROM agent_runs WHERE id = $1 AND repository_id = $2");
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(repositoryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var response = JsonSerializer.Deserialize<AgentRunResponse>(reader.GetString(0), Json)
            ?? throw new JsonException("Stored agent run is empty.");
        return response with { RunId = runId, CreatedAt = reader.GetFieldValue<DateTimeOffset>(1) };
    }
}
