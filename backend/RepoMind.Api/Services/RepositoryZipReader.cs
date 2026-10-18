using System.IO.Compression;
using System.Text;

namespace RepoMind.Api.Services;

public sealed record SourceFile(string Path, string Content);
public sealed record ZipReadResult(IReadOnlyList<SourceFile> Files, int FilesSkipped);

public sealed class UnsafeZipPathException : Exception
{
    public UnsafeZipPathException() : base("ZIP contains an absolute or traversal path.") { }
}

public sealed class RepositoryZipReader
{
    public const int MaxUploadBytes = 5 * 1024 * 1024;
    public const int MaxFiles = 100;
    public const int MaxFileBytes = 250 * 1024;
    public const int MaxTotalBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".vue", ".json", ".md", ".html"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "dist", "build", "bin", "obj", "coverage"
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ZipReadResult> ReadAsync(Stream zipStream, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var files = new List<SourceFile>();
        var skipped = 0;
        var totalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.FullName.Replace('\\', '/');
            if (IsAbsoluteOrTraversal(path)) throw new UnsafeZipPathException();
            if (entry.Name.Length == 0 || path.EndsWith('/')) continue;

            if (IsNonRegularEntry(entry) || IsExcluded(path) || files.Count >= MaxFiles ||
                entry.Length > MaxFileBytes || entry.Length > MaxTotalBytes - totalBytes)
            {
                skipped++;
                continue;
            }

            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            var tooLarge = false;
            int read;
            while ((read = await stream.ReadAsync(block, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxFileBytes || buffer.Length + read > MaxTotalBytes - totalBytes)
                {
                    tooLarge = true;
                    break;
                }
                buffer.Write(block, 0, read);
            }
            if (tooLarge)
            {
                skipped++;
                continue;
            }

            var bytes = buffer.ToArray();
            if (bytes.Any(value => (value < 32 && value is not (9 or 10 or 13)) || value == 127))
            {
                skipped++;
                continue;
            }

            try
            {
                var content = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(content))
                {
                    skipped++;
                    continue;
                }
                files.Add(new SourceFile(path, content));
                totalBytes += bytes.Length;
            }
            catch (DecoderFallbackException)
            {
                skipped++;
            }
        }

        return new ZipReadResult(files, skipped);
    }

    private static bool IsAbsoluteOrTraversal(string path)
    {
        if (path.StartsWith('/') ||
            (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')) return true;
        return path.Split('/').Any(part => part == "..");
    }

    private static bool IsNonRegularEntry(ZipArchiveEntry entry)
    {
        var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
        return fileType != 0 && fileType != 0x8000;
    }

    private static bool IsExcluded(string path)
    {
        var parts = path.Split('/');
        if (parts.Any(part => part.Length == 0 || part == "." ||
            part.Contains(':') || part.Any(char.IsControl))) return true;
        if (parts.SkipLast(1).Any(part => part.StartsWith('.') || ExcludedDirectories.Contains(part)))
            return true;

        var name = parts[^1];
        if (name.StartsWith('.') || !Extensions.Contains(Path.GetExtension(name))) return true;
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("appsettings") || lower is "config.json" or "settings.json" ||
            lower.Contains("secret") ||
            lower.Contains("credential") || lower.Contains("private") ||
            lower.Contains("password") || lower.Contains("keyfile") ||
            lower.Contains("apikey") || lower.Contains("connectionstring") ||
            lower.Contains("token");
    }
}
