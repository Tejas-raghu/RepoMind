using Npgsql;
using Pgvector;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class RepositoryUploadService(
    RepositoryZipReader zipReader, IEmbeddingService embeddings, NpgsqlDataSource dataSource)
{
    public async Task<RepositoryUploadResponse> UploadAsync(
        string fileName, Stream zipStream, CancellationToken cancellationToken)
    {
        var source = await zipReader.ReadAsync(zipStream, cancellationToken);
        if (source.Files.Count == 0)
            throw new InvalidDataException("ZIP has no supported, safe, nonempty source files.");

        var chunks = source.Files
            .SelectMany(file => CodeChunker.Chunk(file.Path, file.Content))
            .ToArray();
        // Overlapping windows and similar files may contain identical text.
        // Embed each distinct text once, then store every source chunk.
        var uniqueTexts = chunks.Select(chunk => chunk.Content)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (uniqueTexts.Length == 0)
            throw new InvalidDataException("ZIP has no indexable source chunks.");
        var embeddingsByText = new Dictionary<string, EmbeddingResponse>(uniqueTexts.Length,
            StringComparer.Ordinal);
        for (var offset = 0; offset < uniqueTexts.Length; offset += 16)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = uniqueTexts.Skip(offset).Take(16).ToArray();
            var vectors = await embeddings.GenerateManyAsync(batch, cancellationToken);
            for (var index = 0; index < batch.Length; index++)
            {
                embeddingsByText.Add(batch[index], vectors[index]);
            }
        }
        var profile = embeddingsByText.Values.First();
        foreach (var result in embeddingsByText.Values)
        {
            EmbeddingCompatibility.Validate(result);
            if (result.Model != profile.Model || result.Provider != profile.Provider ||
                result.Dimensions != profile.Dimensions)
                throw new InvalidDataException("Embedding model changed during upload.");
        }
        var column = EmbeddingCompatibility.Column(profile.Model, profile.Dimensions);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long repositoryId;
        await using (var command = new NpgsqlCommand(
            "INSERT INTO repositories (name, status, embedding_provider, embedding_model) " +
            "VALUES ($1, 'completed', $2, $3) RETURNING id",
            connection, transaction))
        {
            command.Parameters.AddWithValue(Path.GetFileNameWithoutExtension(fileName));
            command.Parameters.AddWithValue(profile.Provider);
            command.Parameters.AddWithValue(profile.Model);
            repositoryId = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Repository insert returned no ID."));
        }

        foreach (var chunk in chunks)
        {
            var embedding = embeddingsByText[chunk.Content];
            await using var command = new NpgsqlCommand(
                "INSERT INTO repository_chunks " +
                $"(repository_id, source_path, start_line, end_line, content, model, {column}) " +
                "VALUES ($1, $2, $3, $4, $5, $6, $7)", connection, transaction);
            command.Parameters.AddWithValue(repositoryId);
            command.Parameters.AddWithValue(chunk.Path);
            command.Parameters.AddWithValue(chunk.StartLine);
            command.Parameters.AddWithValue(chunk.EndLine);
            command.Parameters.AddWithValue(chunk.Content);
            command.Parameters.AddWithValue(embedding.Model);
            command.Parameters.AddWithValue(new Vector(embedding.Embedding));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RepositoryUploadResponse(repositoryId, "completed", source.Files.Count,
            chunks.Length, source.FilesSkipped);
    }
}
