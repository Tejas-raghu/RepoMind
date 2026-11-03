using Npgsql;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed record StoredSourceChunk(long Id, int StartLine, int EndLine, string Content);

public sealed class RepositorySourceService(NpgsqlDataSource dataSource)
{
    public const int MaxLines = 5_000;
    public const int MaxStoredCharacters = 500_000;

    public static bool ValidPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 1_024 &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        !path.Any(char.IsControl) &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    public async Task<bool> ExistsAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM repositories WHERE id = $1 AND status = 'completed')");
        command.Parameters.AddWithValue(repositoryId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<IReadOnlyList<IndexedFile>> ListAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT source_path FROM (SELECT DISTINCT source_path FROM repository_chunks " +
            "WHERE repository_id = $1) files ORDER BY source_path COLLATE \"C\"");
        command.Parameters.AddWithValue(repositoryId);
        var files = new List<IndexedFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new IndexedFile(reader.GetString(0)));
        return files;
    }

    public async Task<IndexedFileContent?> GetContentAsync(
        long repositoryId, string path, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, start_line, end_line, content FROM repository_chunks " +
            "WHERE repository_id = $1 AND source_path = $2 ORDER BY id ASC");
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(path);
        var chunks = new List<StoredSourceChunk>();
        var characters = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var content = reader.GetString(3);
            characters += content.Length;
            if (characters > MaxStoredCharacters || reader.GetInt32(2) > MaxLines || chunks.Count >= 5_000)
                throw new SourceTooLargeException();
            chunks.Add(new StoredSourceChunk(reader.GetInt64(0), reader.GetInt32(1),
                reader.GetInt32(2), content));
        }
        return chunks.Count == 0 ? null : Assemble(path, chunks);
    }

    public static IndexedFileContent Assemble(string path, IReadOnlyList<StoredSourceChunk> chunks)
    {
        var ordered = chunks.OrderBy(chunk => chunk.Id).ToArray();
        var maximum = ordered.Max(chunk => chunk.EndLine);
        if (maximum is < 1 or > MaxLines) throw new SourceTooLargeException();
        var values = new Dictionary<int, string>();
        var conflicts = new HashSet<int>();
        var previousWasSplit = false;
        StoredSourceChunk? previous = null;
        foreach (var chunk in ordered)
        {
            if (chunk.StartLine < 1 || chunk.EndLine < chunk.StartLine)
                throw new InvalidDataException("Stored source line range is invalid.");
            var continuation = previousWasSplit && previous is not null &&
                chunk.StartLine >= previous.EndLine && chunk.StartLine <= previous.EndLine + 1;
            var split = chunk.Content.Length == 2_000 || continuation;
            if (split)
            {
                // This piece may begin or end inside an original line. Only another
                // complete overlapping chunk can safely provide that line.
            }
            else
            {
                var lines = chunk.Content.Split('\n');
                if (lines.Length != chunk.EndLine - chunk.StartLine + 1)
                {
                    // Inconsistent line metadata cannot be mapped to original lines.
                }
                else
                {
                    for (var index = 0; index < lines.Length; index++)
                    {
                        var number = chunk.StartLine + index;
                        // HTML ingestion replaces very long lines with empty strings.
                        // A blank cannot be distinguished from a skipped original line.
                        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                            lines[index].Length == 0) continue;
                        if (values.TryGetValue(number, out var current) && current != lines[index])
                            conflicts.Add(number);
                        else values[number] = lines[index];
                    }
                }
            }
            previousWasSplit = chunk.Content.Length == 2_000;
            previous = chunk;
        }

        var result = new List<SourceLine>(maximum);
        for (var number = 1; number <= maximum; number++)
        {
            if (conflicts.Contains(number)) result.Add(new SourceLine(number, null, "conflict"));
            else if (values.TryGetValue(number, out var content))
                result.Add(new SourceLine(number, content, "covered"));
            else result.Add(new SourceLine(number, null, "gap"));
        }
        // Original bytes, trailing newline, and excluded HTML literals were not retained.
        return new IndexedFileContent(path, false, "indexed_coverage", result,
            Ranges(result, "covered"), Ranges(result, "gap"), Ranges(result, "conflict"));
    }

    private static IReadOnlyList<SourceRange> Ranges(IReadOnlyList<SourceLine> lines, string status)
    {
        var ranges = new List<SourceRange>();
        int? start = null;
        foreach (var line in lines)
        {
            if (line.Status == status) start ??= line.Number;
            else if (start is not null)
            {
                ranges.Add(new SourceRange(start.Value, line.Number - 1));
                start = null;
            }
        }
        if (start is not null) ranges.Add(new SourceRange(start.Value, lines[^1].Number));
        return ranges;
    }
}

public sealed class SourceTooLargeException : Exception;
