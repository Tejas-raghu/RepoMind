using RepoMind.Api.Contracts;
using RepoMind.Api.Services;
using System.Text.Json;

namespace RepoMind.Api.Tests;

public sealed class HybridSearchTests
{
    [Fact]
    public void Rrf_deduplicates_and_promotes_a_chunk_found_by_both_lists()
    {
        var readme = Chunk(1, "README.md");
        var code = Chunk(2, "apiClient.ts");
        var other = Chunk(3, "Other.cs");
        var results = HybridSearchService.Fuse([readme, code], [code, other, readme], 5);

        Assert.Equal([2L, 1L, 3L], results.Select(result => result.ChunkId));
        Assert.Equal(3, results.Count);
        Assert.Equal((2, 1), (results[0].VectorRank, results[0].LexicalRank));
        Assert.Equal(1.0 / 62 + 1.0 / 61, results[0].RrfScore, 12);
        Assert.Equal("apiClient.ts", results[0].FilePath);
        Assert.Equal((4, 12, "source"),
            (results[0].StartLine, results[0].EndLine, results[0].Content));
    }

    [Fact]
    public void Rrf_ties_are_deterministic_and_top_k_is_applied_after_fusion()
    {
        var first = Chunk(1, "A.cs");
        var second = Chunk(2, "B.cs");
        var results = HybridSearchService.Fuse([first, second], [second, first], 1);
        Assert.Single(results);
        Assert.Equal(1, results[0].ChunkId);
    }

    [Fact]
    public void Empty_lists_return_no_results()
    {
        Assert.Empty(HybridSearchService.Fuse([], [], 5));
    }

    [Fact]
    public void Reranker_moves_third_candidate_to_first_and_preserves_stored_sources()
    {
        var fused = new HybridSearchResult[]
        {
            new(1, "README.md", 1, 8, "Documentation", 0.8, 1, 1, 0.033),
            new(2, "Other.ts", 2, 9, "other code", 0.7, 2, 2, 0.032),
            new(3, "apiClient.ts", 10, 20, "headers.set('Authorization', `Bearer ${token}`)", 0.4, 3, 3, 0.031)
        };
        var reordered = HybridSearchService.Rerank(fused,
            new Dictionary<long, double> { [1] = 10, [2] = 20, [3] = 95 }, 3, 3);
        Assert.Equal([3L, 2L, 1L], reordered.Select(x => x.ChunkId));
        Assert.Same(fused[2], reordered[0]);
        Assert.Equal(("apiClient.ts", 10, 20, 0.4, 0.031),
            (reordered[0].FilePath, reordered[0].StartLine, reordered[0].EndLine,
                reordered[0].CosineSimilarity, reordered[0].RrfScore));
        Assert.Contains("headers.set", reordered[0].Content);
    }

    [Fact]
    public void Reranker_ties_use_rrf_then_chunk_id_and_keep_unscored_tail()
    {
        var fused = new HybridSearchResult[]
        {
            new(2, "B", 1, 1, "B", 0, 1, 1, 0.2),
            new(3, "C", 1, 1, "C", 0, 2, 2, 0.1),
            new(1, "A", 1, 1, "A", 0, 3, 3, 0.1),
            new(4, "D", 1, 1, "D", 0, 4, 4, 0.01)
        };
        var reordered = HybridSearchService.Rerank(fused,
            new Dictionary<long, double> { [2] = 50, [3] = 90, [1] = 90 }, 3, 4);
        Assert.Equal([1L, 3L, 2L, 4L], reordered.Select(x => x.ChunkId));
    }

    [Fact]
    public void Invalid_mocked_scores_are_rejected()
    {
        var fused = HybridSearchService.Fuse([Chunk(1, "A"), Chunk(2, "B")], [], 2);
        Assert.Throws<JsonException>(() => HybridSearchService.Rerank(fused,
            new Dictionary<long, double> { [1] = 50, [99] = 80 }, 2, 2));
        Assert.Throws<JsonException>(() => HybridSearchService.Rerank(fused,
            new Dictionary<long, double> { [1] = double.NaN, [2] = 80 }, 2, 2));
    }

    [Fact]
    public void Technical_terms_are_kept_and_common_question_words_removed()
    {
        Assert.Equal(["bearer", "authorization", "jwt", "cs0234"],
            HybridSearchService.ExtractTerms("Where is Bearer Authorization JWT CS0234?"));
    }

    [Theory]
    [InlineData(0, "query", null, "repositoryId")]
    [InlineData(1, " ", null, "query")]
    [InlineData(1, "query", 0, "topK")]
    [InlineData(1, "query", 21, "topK")]
    public void Request_rejects_invalid_input(long id, string query, int? topK, string field)
    {
        Assert.Contains(field, new HybridSearchRequest(query, topK).Validate(id).Keys);
    }

    [Fact]
    public void Request_defaults_to_five_and_limits_query_length()
    {
        Assert.Empty(new HybridSearchRequest("query", null).Validate(1));
        Assert.Contains("query", new HybridSearchRequest(new string('x', 501), 5).Validate(1).Keys);
    }

    private static RepositorySearchResult Chunk(long id, string path) =>
        new(id, path, 4, 12, "source", 0.4);
}
