using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using Microsoft.Extensions.Configuration;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class RepositorySearchStoreTests
{
    [Fact]
    public async Task Search_is_scoped_ordered_limited_and_returns_source_metadata()
    {
        var connectionString = Environment.GetEnvironmentVariable("REPOMIND_TEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=repomind;Username=repomind;Password=localdev";
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        await using var dataSource = builder.Build();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["RepositorySearch:MinimumSimilarity"] = "0.35" })
            .Build();
        var store = new RepositorySearchStore(dataSource, configuration);
        var repositoryIds = new List<long>();

        try
        {
            var first = await AddRepositoryAsync(dataSource);
            repositoryIds.Add(first);
            var second = await AddRepositoryAsync(dataSource);
            repositoryIds.Add(second);
            var empty = await AddRepositoryAsync(dataSource);
            repositoryIds.Add(empty);
            var unrelated = await AddRepositoryAsync(dataSource);
            repositoryIds.Add(unrelated);

            var exact = UnitVector(1, 0);
            var close = UnitVector(1, 1);
            var opposite = UnitVector(-1, 0);
            var exactId = await AddChunkAsync(dataSource, first, "src/Auth.cs", 4, 12,
                "AuthenticateUser", "embeddinggemma", exact);
            var closeId = await AddChunkAsync(dataSource, first, "src/Login.cs", 20, 30,
                "LoginHandler", "embeddinggemma", close);
            await AddChunkAsync(dataSource, first, "src/Other.cs", 1, 2,
                "Unrelated", "embeddinggemma", opposite);
            await AddChunkAsync(dataSource, first, "src/AboveCutoff.cs", 1, 2,
                "Above cutoff", "embeddinggemma", UnitVector(0.4f, 0.916515f));
            await AddChunkAsync(dataSource, first, "src/BelowCutoff.cs", 1, 2,
                "Below cutoff", "embeddinggemma", UnitVector(0.3f, 0.953939f));
            await AddChunkAsync(dataSource, second, "src/Private.cs", 1, 1,
                "Other repository", "embeddinggemma", exact);
            await AddChunkAsync(dataSource, first, "src/OtherModel.cs", 1, 1,
                "Other model", "different-model", exact);
            await AddChunkAsync(dataSource, unrelated, "src/Unrelated.cs", 1, 3,
                "No related code", "embeddinggemma", opposite);

            Assert.True(await store.ExistsAsync(first, CancellationToken.None));
            Assert.True(await store.ExistsAsync(empty, CancellationToken.None));
            Assert.False(await store.ExistsAsync(long.MaxValue, CancellationToken.None));
            Assert.True(await store.HasChunksAsync(first, CancellationToken.None));
            Assert.False(await store.HasChunksAsync(empty, CancellationToken.None));
            Assert.True(await store.HasChunksAsync(unrelated, CancellationToken.None));
            Assert.Empty(await store.SearchAsync(empty, "embeddinggemma", exact, 5,
                CancellationToken.None));
            Assert.Empty(await store.SearchAsync(unrelated, "embeddinggemma", exact, 5,
                CancellationToken.None));

            var topTwo = await store.SearchAsync(first, "embeddinggemma", exact, 2,
                CancellationToken.None);
            Assert.Equal(2, topTwo.Count);
            Assert.Equal([exactId, closeId], topTwo.Select(result => result.ChunkId));
            Assert.Equal("src/Auth.cs", topTwo[0].FilePath);
            Assert.Equal((4, 12), (topTwo[0].StartLine, topTwo[0].EndLine));
            Assert.Equal("AuthenticateUser", topTwo[0].Content);
            Assert.Equal(1.0, topTwo[0].Similarity, 5);
            Assert.True(topTwo[0].Similarity > topTwo[1].Similarity);

            var topOne = await store.SearchAsync(first, "embeddinggemma", exact, 1,
                CancellationToken.None);
            Assert.Single(topOne);
            Assert.Equal(exactId, topOne[0].ChunkId);

            var allFirst = await store.SearchAsync(first, "embeddinggemma", exact, 20,
                CancellationToken.None);
            Assert.Equal(3, allFirst.Count);
            Assert.Contains(allFirst, result => result.Content == "Above cutoff");
            Assert.DoesNotContain(allFirst, result => result.Content == "Other repository" ||
                result.Content == "Other model" || result.Content == "Unrelated" ||
                result.Content == "Below cutoff");
            Assert.All(allFirst, result => Assert.InRange(result.Similarity, -1.00001, 1.00001));

            var stricterConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["RepositorySearch:MinimumSimilarity"] = "0.5" })
                .Build();
            var stricterStore = new RepositorySearchStore(dataSource, stricterConfiguration);
            var stricterMatches = await stricterStore.SearchAsync(first, "embeddinggemma", exact, 20,
                CancellationToken.None);
            Assert.Equal([exactId, closeId], stricterMatches.Select(result => result.ChunkId));
        }
        finally
        {
            foreach (var repositoryId in repositoryIds)
            {
                await using var deleteChunks = dataSource.CreateCommand(
                    "DELETE FROM repository_chunks WHERE repository_id = $1");
                deleteChunks.Parameters.AddWithValue(repositoryId);
                await deleteChunks.ExecuteNonQueryAsync();
                await using var deleteRepository = dataSource.CreateCommand(
                    "DELETE FROM repositories WHERE id = $1");
                deleteRepository.Parameters.AddWithValue(repositoryId);
                await deleteRepository.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<long> AddRepositoryAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repositories (name, status) VALUES ($1, 'completed') RETURNING id");
        command.Parameters.AddWithValue("day7-search-test-" + Guid.NewGuid().ToString("N"));
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Test repository insert returned no ID."));
    }

    private static async Task<long> AddChunkAsync(NpgsqlDataSource dataSource,
        long repositoryId, string path, int startLine, int endLine, string content,
        string model, float[] embedding)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repository_chunks " +
            "(repository_id, source_path, start_line, end_line, content, model, embedding) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id");
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(path);
        command.Parameters.AddWithValue(startLine);
        command.Parameters.AddWithValue(endLine);
        command.Parameters.AddWithValue(content);
        command.Parameters.AddWithValue(model);
        command.Parameters.AddWithValue(new Vector(embedding));
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Test chunk insert returned no ID."));
    }

    private static float[] UnitVector(float first, float second)
    {
        var values = new float[768];
        values[0] = first;
        values[1] = second;
        return values;
    }
}
