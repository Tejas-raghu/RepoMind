using System.IO.Compression;
using System.Text;
using Npgsql;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

// Real PostgreSQL + pgvector with a deterministic embedding stub.
public sealed class RepositoryUploadIntegrationTests : IAsyncLifetime
{
    private readonly NpgsqlDataSource dataSource = TestDatabase.Create();
    private readonly List<long> repositories = [];

    [Fact]
    public async Task Upload_redacts_secret_values_before_storage_and_keeps_line_numbers()
    {
        var upload = new RepositoryUploadService(new RepositoryZipReader(), new StubEmbeddings(), dataSource);
        using var zip = Zip(("src/config.ts",
            "export const region = 'eu'\nexport const awsKey = 'AKIAABCDEFGHIJKLMNOP'\nexport const done = true\n"));

        var result = await upload.UploadAsync("redaction-test.zip", zip, CancellationToken.None);
        repositories.Add(result.RepositoryId);

        Assert.Equal((1, 1, 1), (result.FilesIndexed, result.ChunksCreated, result.SecretsRedacted));
        await using var command = dataSource.CreateCommand(
            "SELECT content, start_line, end_line, embedding_provider, embedding_model FROM repository_chunks c " +
            "JOIN repositories r ON r.id = c.repository_id WHERE c.repository_id = $1");
        command.Parameters.AddWithValue(result.RepositoryId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var content = reader.GetString(0);
        Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", content);
        Assert.Contains(SecretRedactor.Marker, content.Split('\n')[1]);
        Assert.Equal((1, 3), (reader.GetInt32(1), reader.GetInt32(2)));
        Assert.Equal(("ollama", "embeddinggemma"), (reader.GetString(3), reader.GetString(4)));
    }

    [Fact]
    public async Task Failed_chunk_insert_rolls_back_the_whole_repository()
    {
        var upload = new RepositoryUploadService(new RepositoryZipReader(), new StubEmbeddings(), dataSource);
        var name = "rollback-test-" + Guid.NewGuid().ToString("N");
        var vector = new EmbeddingResponse("embeddinggemma", new float[768]);
        // PostgreSQL text cannot store NUL, so the second insert fails inside the transaction.
        var chunks = new[] { new CodeChunk("a.ts", 1, 1, "const ok = 1"), new CodeChunk("b.ts", 1, 1, "bad\0text") };
        var embeddings = chunks.ToDictionary(chunk => chunk.Content, _ => vector);

        await Assert.ThrowsAnyAsync<NpgsqlException>(() => upload.PersistAsync(name, vector, "embedding",
            chunks, embeddings, CancellationToken.None));

        Assert.Equal(0, await TestDatabase.CountAsync(dataSource, "SELECT count(*) FROM repositories WHERE name = $1", name));
    }

    [Fact]
    public async Task Cancelled_upload_stores_nothing()
    {
        var upload = new RepositoryUploadService(new RepositoryZipReader(), new StubEmbeddings(), dataSource);
        var name = "cancelled-" + Guid.NewGuid().ToString("N");
        using var zip = Zip(("src/a.ts", "export const a = 1\n"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            upload.UploadAsync(name + ".zip", zip, cancelled.Token));

        Assert.Equal(0, await TestDatabase.CountAsync(dataSource, "SELECT count(*) FROM repositories WHERE name = $1", name));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestDatabase.CleanupAsync(dataSource, repositories);
        await dataSource.DisposeAsync();
    }

    internal static MemoryStream Zip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        stream.Position = 0;
        return stream;
    }
}
