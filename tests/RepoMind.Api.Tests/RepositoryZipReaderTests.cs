using System.IO.Compression;
using System.Text;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class RepositoryZipReaderTests
{
    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../outside.cs")]
    [InlineData("/absolute.cs")]
    [InlineData("C:\\absolute.cs")]
    public async Task Absolute_and_traversal_paths_reject_the_archive(string path)
    {
        using var zip = MakeZip((path, "class Unsafe {}"));
        await Assert.ThrowsAsync<UnsafeZipPathException>(() =>
            new RepositoryZipReader().ReadAsync(zip, CancellationToken.None));
    }

    [Fact]
    public async Task Excluded_paths_and_binary_files_are_skipped()
    {
        using var zip = MakeZip(
            ("src/App.ts", "export const answer = 42;"),
            ("node_modules/pkg/a.js", "ignored"),
            (".git/config", "ignored"),
            ("dist/app.js", "ignored"),
            ("src/credentials.json", "ignored"),
            ("src/config.json", "ignored"),
            ("src/.env", "ignored"),
            ("src/binary.cs", "zero\0byte"),
            ("src/control.ts", "bad\u0001byte"),
            ("src/image.png", "ignored"));

        var result = await new RepositoryZipReader().ReadAsync(zip, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal("src/App.ts", result.Files[0].Path);
        Assert.Equal(9, result.FilesSkipped);
    }

    [Fact]
    public async Task Valid_small_archive_reads_supported_files_without_extraction()
    {
        using var zip = MakeZip(
            ("src/Program.cs", "class Program {}\n"),
            ("README.md", "# Example\n"),
            ("src/App.vue", "<template>Hello</template>"),
            ("maps/view.html", "<html><body>Map</body></html>"));

        var result = await new RepositoryZipReader().ReadAsync(zip, CancellationToken.None);

        Assert.Equal(4, result.Files.Count);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal("class Program {}\n", result.Files[0].Content);
        Assert.Contains(result.Files, file => file.Path == "src/App.vue");
        Assert.Contains(result.Files, file => file.Path == "maps/view.html");
    }

    [Fact]
    public async Task Oversized_file_is_skipped()
    {
        using var zip = MakeZip(
            ("large.cs", new string('x', RepositoryZipReader.MaxFileBytes + 1)),
            ("small.cs", "class Small {}"));

        var result = await new RepositoryZipReader().ReadAsync(zip, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal("small.cs", result.Files[0].Path);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public async Task Only_first_hundred_supported_files_are_processed()
    {
        var files = Enumerable.Range(1, 101)
            .Select(i => ($"src/file{i}.cs", "class Example {}"))
            .ToArray();
        using var zip = MakeZip(files);

        var result = await new RepositoryZipReader().ReadAsync(zip, CancellationToken.None);

        Assert.Equal(100, result.Files.Count);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public async Task Overall_uncompressed_budget_skips_later_files()
    {
        var files = Enumerable.Range(1, 80)
            .Select(i => ($"src/file{i}.cs", new string('x', 150 * 1024)))
            .ToArray();
        using var zip = MakeZip(files);

        var result = await new RepositoryZipReader().ReadAsync(zip, CancellationToken.None);

        Assert.Equal(68, result.Files.Count);
        Assert.Equal(12, result.FilesSkipped);
    }

    [Fact]
    public async Task Symlink_entries_are_skipped()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var link = archive.CreateEntry("src/link.cs");
            link.ExternalAttributes = 0xA000 << 16;
            using var writer = new StreamWriter(link.Open());
            writer.Write("class Linked {}");
        }
        zip.Position = 0;

        var result = await new RepositoryZipReader().ReadAsync(zip, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.FilesSkipped);
    }

    private static MemoryStream MakeZip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
