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

    [Fact]
    public void Implementation_questions_prefer_code_and_matching_declarations()
    {
        var readme = Result(1, "README.md", "Route guard documentation", 0.033);
        var guard = Result(2, "src/router/guards.ts", "router.beforeEach((to) => {", 0.016);
        Assert.Equal(2, HybridSearchService.PrioritizeImplementation(
            "How are protected routes secured?", [readme, guard])[0].ChunkId);

        var caller = Result(3, "src/services/authService.ts", "apiClient.post('/auth/login')", 0.0323);
        var handler = Result(4, "server/AuthController.cs",
            "public async Task<ActionResult<LoginResult>> Login(LoginRequest request)", 0.0320);
        Assert.Equal(4, HybridSearchService.PrioritizeImplementation(
            "Where is backend login implemented?", [caller, handler])[0].ChunkId);

        var component = Result(5, "src/components/TaskBoard.vue", "isOverdue(task)", 0.0328);
        var definition = Result(6, "src/services/taskService.ts",
            "export function isOverdue(task: Task): boolean {", 0.0320);
        Assert.Equal(6, HybridSearchService.PrioritizeImplementation(
            "How are overdue tasks identified?", [component, definition])[0].ChunkId);

        var bearerImplementation = Result(7, "src/services/apiClient.ts",
            "headers.set('Authorization', `Bearer ${token}`)", 0.0323);
        var tokenReader = Result(8, "src/services/authService.ts",
            "export function getAccessToken() { return token; }", 0.0315);
        Assert.Equal(7, HybridSearchService.PrioritizeImplementation(
            "Where is the Bearer token added to requests?", [bearerImplementation, tokenReader])[0].ChunkId);

        Assert.Equal(6, HybridSearchService.PrioritizeImplementation(
            "Which function decides if a task is overdue?", [component, definition])[0].ChunkId);

        var headerReader = new HybridSearchResult(9, "src/authService.ts", 1, 20,
            "getSession()", 0.4, 1, 3, 0.032266);
        var headerWriter = new HybridSearchResult(10, "src/apiClient.ts", 1, 20,
            "headers.set('Authorization', token)", 0.4, 4, 1, 0.032018);
        Assert.Equal(10, HybridSearchService.PrioritizeImplementation(
            "Where does the client attach the Authorization header?", [headerReader, headerWriter])[0].ChunkId);

        var routeGuard = new HybridSearchResult(11, "src/router/guards.ts", 1, 20,
            "return { name: 'login' };", 0.4, 1, 2, 0.032522);
        var loginHandler = new HybridSearchResult(12, "server/AuthController.cs", 1, 20,
            "public LoginResult Login(LoginRequest request)", 0.4, 4, 1, 0.032018);
        Assert.Equal(11, HybridSearchService.PrioritizeImplementation(
            "Which code redirects unauthenticated users to login?", [routeGuard, loginHandler])[0].ChunkId);
    }

    [Fact]
    public void Explicit_documentation_questions_keep_rrf_order()
    {
        var readme = Result(1, "README.md", "Architecture notes", 0.033);
        var code = Result(2, "src/api.ts", "export function api() {}", 0.016);
        Assert.Equal([1L, 2L], HybridSearchService.PrioritizeImplementation(
            "What does the README say about the API?", [readme, code]).Select(x => x.ChunkId));
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

    private static HybridSearchResult Result(long id, string path, string content, double score) =>
        new(id, path, 1, 20, content, 0.4, 1, 1, score);
}
