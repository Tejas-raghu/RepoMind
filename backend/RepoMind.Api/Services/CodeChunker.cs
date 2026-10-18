namespace RepoMind.Api.Services;

public sealed record CodeChunk(string Path, int StartLine, int EndLine, string Content);

public static class CodeChunker
{
    // Long generated lines can exceed embeddinggemma's 2,048-token context.
    private const int MaxEmbeddingCharacters = 2_000;
    private const int MaxHtmlLineCharacters = 8_192;

    public static IReadOnlyList<CodeChunk> Chunk(string path, string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (normalized.EndsWith('\n')) lines = lines[..^1];

        // Generated map pages can contain enormous inline data literals. Those
        // lines are data rather than useful code, and dominate local CPU work.
        // Keep their positions so references to later HTML lines stay correct.
        if (Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase))
        {
            for (var index = 0; index < lines.Length; index++)
                if (lines[index].Length > MaxHtmlLineCharacters) lines[index] = string.Empty;
        }

        var chunks = new List<CodeChunk>();
        for (var start = 0; start < lines.Length; start += 50)
        {
            var end = Math.Min(start + 60, lines.Length);
            var text = string.Join('\n', lines[start..end]);
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (text.Length <= MaxEmbeddingCharacters)
            {
                chunks.Add(new CodeChunk(path, start + 1, end, text));
                continue;
            }

            // Preserve line metadata while splitting oversized windows, including
            // files with a single very long generated JavaScript or JSON line.
            var line = start + 1;
            for (var offset = 0; offset < text.Length; offset += MaxEmbeddingCharacters)
            {
                var piece = text.Substring(offset, Math.Min(MaxEmbeddingCharacters, text.Length - offset));
                var newlines = piece.Count(character => character == '\n');
                var lastLine = line + newlines - (piece.EndsWith('\n') ? 1 : 0);
                if (!string.IsNullOrWhiteSpace(piece))
                    chunks.Add(new CodeChunk(path, line, lastLine, piece));
                line += newlines;
            }
        }

        return chunks;
    }
}
