using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

// Shared helpers for tests that use the real local PostgreSQL + pgvector database.
// Every repository created here is removed, with its chunks and agent runs, by CleanupAsync.
internal static class TestDatabase
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("REPOMIND_TEST_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=repomind;Username=repomind;Password=localdev";

    public static NpgsqlDataSource Create()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseVector();
        return builder.Build();
    }

    public static async Task<long> AddRepositoryAsync(NpgsqlDataSource dataSource, string prefix = "test")
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repositories (name, status, embedding_provider, embedding_model) " +
            "VALUES ($1, 'completed', 'ollama', 'embeddinggemma') RETURNING id");
        command.Parameters.AddWithValue($"{prefix}-{Guid.NewGuid():N}");
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Test repository insert returned no ID."));
    }

    // Stores one chunk covering startLine..(startLine + lines - 1) with a unit vector
    // whose first coordinate is the cosine similarity to StubEmbeddings' query vector.
    public static async Task<long> AddChunkAsync(NpgsqlDataSource dataSource, long repositoryId,
        string path, string content, int startLine = 1, float similarity = 1)
    {
        var vector = new float[768];
        vector[0] = similarity;
        vector[1] = MathF.Sqrt(1 - similarity * similarity);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repository_chunks " +
            "(repository_id, source_path, start_line, end_line, content, model, embedding) " +
            "VALUES ($1, $2, $3, $4, $5, 'embeddinggemma', $6) RETURNING id");
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(path);
        command.Parameters.AddWithValue(startLine);
        command.Parameters.AddWithValue(startLine + content.Split('\n').Length - 1);
        command.Parameters.AddWithValue(content);
        command.Parameters.AddWithValue(new Vector(vector));
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Test chunk insert returned no ID."));
    }

    public static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public static async Task CleanupAsync(NpgsqlDataSource dataSource, IEnumerable<long> repositoryIds)
    {
        var ids = repositoryIds.ToArray();
        if (ids.Length == 0) return;
        foreach (var sql in new[]
        {
            "DELETE FROM agent_runs WHERE repository_id = ANY($1)",
            "DELETE FROM repository_chunks WHERE repository_id = ANY($1)",
            "DELETE FROM repositories WHERE id = ANY($1)"
        })
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(ids);
            await command.ExecuteNonQueryAsync();
        }
    }
}

// Deterministic 768-dimensional query vector: [1, 0, 0, ...].
public sealed class StubEmbeddings : IEmbeddingService
{
    public int Calls { get; private set; }
    public Exception? Failure { get; set; }

    public Task<EmbeddingResponse> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        Calls++;
        if (Failure is not null) return Task.FromException<EmbeddingResponse>(Failure);
        var vector = new float[768];
        vector[0] = 1;
        return Task.FromResult(new EmbeddingResponse("embeddinggemma", vector));
    }

    public async Task<IReadOnlyList<EmbeddingResponse>> GenerateManyAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var results = new List<EmbeddingResponse>();
        foreach (var text in texts) results.Add(await GenerateAsync(text, cancellationToken));
        return results;
    }
}
