using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class CodeChunkerTests
{
    [Fact]
    public void Sixty_lines_with_ten_line_overlap_keep_correct_line_numbers()
    {
        var source = string.Join("\r\n", Enumerable.Range(1, 120).Select(i => $"line {i}"));

        var chunks = CodeChunker.Chunk("src/App.cs", source);

        Assert.Equal(3, chunks.Count);
        Assert.Equal((1, 60), (chunks[0].StartLine, chunks[0].EndLine));
        Assert.Equal((51, 110), (chunks[1].StartLine, chunks[1].EndLine));
        Assert.Equal((101, 120), (chunks[2].StartLine, chunks[2].EndLine));
        Assert.All(chunks, chunk => Assert.Equal("src/App.cs", chunk.Path));
        Assert.Contains("line 51", chunks[0].Content);
        Assert.StartsWith("line 51", chunks[1].Content);
        Assert.EndsWith("line 120", chunks[2].Content);
    }

    [Fact]
    public void Whitespace_only_chunks_are_skipped()
    {
        Assert.Empty(CodeChunker.Chunk("empty.cs", " \n\t\n"));
    }

    [Fact]
    public void Very_long_line_is_split_without_losing_content_or_line_number()
    {
        var source = new string('x', 4_501);

        var chunks = CodeChunker.Chunk("source.cs", source);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal((1, 1), (chunk.StartLine, chunk.EndLine)));
        Assert.Equal(source, string.Concat(chunks.Select(chunk => chunk.Content)));
        Assert.All(chunks, chunk => Assert.True(chunk.Content.Length <= 2_000));
    }

    [Fact]
    public void Generated_html_data_line_is_skipped_without_changing_line_numbers()
    {
        var source = "<h1>Map</h1>\n" + new string('x', 8_193) + "\n<script>initialize();</script>";

        var chunks = CodeChunker.Chunk("map.html", source);

        Assert.Single(chunks);
        Assert.Equal((1, 3), (chunks[0].StartLine, chunks[0].EndLine));
        Assert.Contains("<h1>Map</h1>", chunks[0].Content);
        Assert.Contains("<script>initialize();</script>", chunks[0].Content);
        Assert.DoesNotContain(new string('x', 100), chunks[0].Content);
    }
}
