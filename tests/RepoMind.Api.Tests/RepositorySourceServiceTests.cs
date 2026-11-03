using Npgsql;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class RepositorySourceServiceTests
{
    [Fact]
    public void Overlap_is_deduplicated_and_missing_lines_are_visible()
    {
        var content = RepositorySourceService.Assemble("src/a.ts", [
            new StoredSourceChunk(1, 1, 3, "one\ntwo\nthree"),
            new StoredSourceChunk(2, 3, 5, "three\nfour\nfive"),
            new StoredSourceChunk(3, 7, 7, "seven")]);
        Assert.False(content.Complete);
        Assert.Equal("indexed_coverage", content.Fidelity);
        Assert.Equal("three", content.Lines[2].Content);
        Assert.Equal("gap", content.Lines[5].Status);
        Assert.Single(content.GapRanges);
        Assert.Equal(6, content.GapRanges[0].StartLine);
        Assert.Equal(5, content.CoveredRanges[0].EndLine);
    }

    [Fact]
    public void Conflicting_overlap_is_never_silently_merged()
    {
        var content = RepositorySourceService.Assemble("a.cs", [
            new StoredSourceChunk(1, 1, 2, "class A {}\n<string>"),
            new StoredSourceChunk(2, 2, 3, "different\nlast")]);
        Assert.Equal("conflict", content.Lines[1].Status);
        Assert.Null(content.Lines[1].Content);
        Assert.Equal(2, content.ConflictRanges[0].StartLine);
        Assert.Equal("class A {}", content.Lines[0].Content);
    }

    [Fact]
    public void Split_fragments_are_not_presented_as_complete_lines()
    {
        var content = RepositorySourceService.Assemble("a.ts", [
            new StoredSourceChunk(1, 1, 1, new string('x', 2_000)),
            new StoredSourceChunk(2, 1, 1, "tail")]);
        Assert.Equal("gap", content.Lines[0].Status);
    }

    [Theory]
    [InlineData("../secret.cs")]
    [InlineData("C:/secret.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b.cs")]
    [InlineData("a//b.cs")]
    [InlineData("")]
    public void Rejects_non_indexed_path_forms(string path) =>
        Assert.False(RepositorySourceService.ValidPath(path));

    [Fact]
    public async Task Database_lists_only_files_in_the_selected_repository()
    {
        var connection = Environment.GetEnvironmentVariable("REPOMIND_TEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=repomind;Username=repomind;Password=localdev";
        await using var dataSource = NpgsqlDataSource.Create(connection);
        long first = 0, second = 0;
        try
        {
            first = await InsertRepository(dataSource);
            second = await InsertRepository(dataSource);
            var service = new RepositorySourceService(dataSource);
            Assert.Empty(await service.ListAsync(first, CancellationToken.None));
            Assert.False(await service.ExistsAsync(long.MaxValue, CancellationToken.None));
            Assert.Null(await service.GetContentAsync(first, "src/a.ts", CancellationToken.None));

            await InsertChunk(dataSource, first, "src/a.ts", 1, 2, "a\n<b>");
            await InsertChunk(dataSource, first, "src/a.ts", 2, 3, "<b>\nc");
            await InsertChunk(dataSource, first, "src/b.vue", 1, 1, "<template />");
            await InsertChunk(dataSource, second, "src/private.cs", 1, 1, "private");

            var files = await service.ListAsync(first, CancellationToken.None);
            Assert.Equal(["src/a.ts", "src/b.vue"], files.Select(x => x.Path));
            Assert.Null(await service.GetContentAsync(first, "src/private.cs", CancellationToken.None));
            var content = await service.GetContentAsync(first, "src/a.ts", CancellationToken.None);
            Assert.NotNull(content);
            Assert.Equal("<b>", content.Lines[1].Content);
            Assert.Equal("c", content.Lines[2].Content);
        }
        finally
        {
            foreach (var id in new[] { first, second }.Where(id => id > 0))
            {
                await using var chunks = dataSource.CreateCommand("DELETE FROM repository_chunks WHERE repository_id = $1");
                chunks.Parameters.AddWithValue(id);
                await chunks.ExecuteNonQueryAsync();
                await using var repository = dataSource.CreateCommand("DELETE FROM repositories WHERE id = $1");
                repository.Parameters.AddWithValue(id);
                await repository.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<long> InsertRepository(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repositories (name, status) VALUES ('day13-test', 'completed') RETURNING id");
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
    }

    private static async Task InsertChunk(NpgsqlDataSource dataSource, long repositoryId,
        string path, int start, int end, string content)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repository_chunks (repository_id, source_path, start_line, end_line, content, model, embedding) " +
            "VALUES ($1, $2, $3, $4, $5, 'embeddinggemma', " +
            "('[' || repeat('0,', 767) || '0]')::vector)");
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(path);
        command.Parameters.AddWithValue(start);
        command.Parameters.AddWithValue(end);
        command.Parameters.AddWithValue(content);
        await command.ExecuteNonQueryAsync();
    }
}
