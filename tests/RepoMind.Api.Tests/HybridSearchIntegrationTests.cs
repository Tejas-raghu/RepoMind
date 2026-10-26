using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class HybridSearchIntegrationTests
{
    [Fact]
    public async Task Candidate_searches_are_repository_scoped_and_match_technical_terms()
    {
        var connectionString = Environment.GetEnvironmentVariable("REPOMIND_TEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=repomind;Username=repomind;Password=localdev";
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        await using var dataSource = dataSourceBuilder.Build();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RepositorySearch:MinimumSimilarity"] = "0.35",
                ["HybridSearch:VectorCandidates"] = "15",
                ["HybridSearch:LexicalCandidates"] = "15"
            }).Build();
        var store = new RepositorySearchStore(dataSource, configuration);
        var embedding = new StubEmbeddings();
        var hybrid = new HybridSearchService(store, embedding, new NoOpReranker(), configuration,
            new TestHostEnvironment(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance);
        var ids = new List<long>();
        try
        {
            var selected = await AddRepositoryAsync(dataSource);
            ids.Add(selected);
            var other = await AddRepositoryAsync(dataSource);
            ids.Add(other);
            var empty = await AddRepositoryAsync(dataSource);
            ids.Add(empty);
            var codeId = await AddChunkAsync(dataSource, selected, "src/apiClient.ts",
                "headers.set('Authorization', `Bearer ${token}`); // CS0234", 0.32f);
            await AddChunkAsync(dataSource, other, "src/Private.ts",
                "headers.set('Authorization', 'Bearer private'); // CS0234", 1);

            var originalSearch = await store.SearchAsync(selected, "embeddinggemma",
                StubEmbeddings.QueryVector, 5, CancellationToken.None);
            Assert.Empty(originalSearch);
            var vector = await store.SearchVectorCandidatesAsync(selected, "embeddinggemma",
                StubEmbeddings.QueryVector, 15, CancellationToken.None);
            Assert.Single(vector);
            Assert.Equal(codeId, vector[0].ChunkId);

            var terms = HybridSearchService.ExtractTerms("authorization BEARER cs0234");
            var lexical = await store.SearchLexicalCandidatesAsync(selected, "embeddinggemma",
                StubEmbeddings.QueryVector, terms, 15, CancellationToken.None);
            Assert.Single(lexical);
            Assert.Equal(codeId, lexical[0].ChunkId);
            Assert.Equal("src/apiClient.ts", lexical[0].FilePath);
            var pathMatch = await store.SearchLexicalCandidatesAsync(selected, "embeddinggemma",
                StubEmbeddings.QueryVector, ["apiclient"], 15, CancellationToken.None);
            Assert.Single(pathMatch);
            Assert.Equal(codeId, pathMatch[0].ChunkId);
            Assert.Empty(await store.SearchLexicalCandidatesAsync(empty, "embeddinggemma",
                StubEmbeddings.QueryVector, terms, 15, CancellationToken.None));
            Assert.Empty(await store.SearchLexicalCandidatesAsync(selected, "embeddinggemma",
                StubEmbeddings.QueryVector, [], 15, CancellationToken.None));

            var result = await hybrid.SearchAsync(selected, "authorization BEARER cs0234", 5,
                CancellationToken.None);
            Assert.NotNull(result);
            Assert.Single(result.Results);
            Assert.Equal(codeId, result.Results[0].ChunkId);
            Assert.Equal((1, 1), (result.Results[0].VectorRank, result.Results[0].LexicalRank));
            Assert.Equal(selected, result.RepositoryId);
            Assert.DoesNotContain("private", result.Results[0].Content);

            var enabledConfig = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Reranking:Enabled"] = "true" }).Build();
            var scoring = new TestReranker((_, chunks) =>
                chunks.ToDictionary(x => x.ChunkId, _ => 95.0));
            var enabled = new HybridSearchService(store, embedding, scoring, enabledConfig,
                new TestHostEnvironment(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance);
            var reranked = await enabled.SearchAsync(selected, "authorization BEARER cs0234", 5,
                CancellationToken.None);
            Assert.NotNull(reranked);
            Assert.Equal("applied", reranked.RerankingStatus);
            Assert.Equal(codeId, reranked.Results[0].ChunkId);
            Assert.Equal([codeId], scoring.LastCandidateIds!);
            Assert.Equal("authorization BEARER cs0234", scoring.LastQuery);
            Assert.All(reranked.Results, x => Assert.DoesNotContain("private", x.Content));

            var timeout = new HybridSearchService(store, embedding,
                new TestReranker((_, _) => throw new TaskCanceledException()), enabledConfig,
                new TestHostEnvironment(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance);
            var fallback = await timeout.SearchAsync(selected, "authorization BEARER cs0234", 5,
                CancellationToken.None);
            Assert.NotNull(fallback);
            Assert.Equal("timeout", fallback.RerankingStatus);
            Assert.Equal(result.Results, fallback.Results);

            var emptyResult = await hybrid.SearchAsync(empty, "Bearer", 5, CancellationToken.None);
            Assert.NotNull(emptyResult);
            Assert.Empty(emptyResult.Results);
            Assert.Null(await hybrid.SearchAsync(long.MaxValue, "Bearer", 5, CancellationToken.None));
            Assert.Equal(3, embedding.Calls);

            await AddChunkAsync(dataSource, selected, "src/Opposite.cs", "unrelated", -1);
            var unfiltered = await store.SearchVectorCandidatesAsync(selected, "embeddinggemma",
                StubEmbeddings.QueryVector, 15, CancellationToken.None);
            Assert.Equal(2, unfiltered.Count);
            Assert.Equal("src/Opposite.cs", unfiltered[1].FilePath);
        }
        finally
        {
            foreach (var id in ids)
            {
                await using var deleteChunks = dataSource.CreateCommand(
                    "DELETE FROM repository_chunks WHERE repository_id = $1");
                deleteChunks.Parameters.AddWithValue(id);
                await deleteChunks.ExecuteNonQueryAsync();
                await using var deleteRepository = dataSource.CreateCommand(
                    "DELETE FROM repositories WHERE id = $1");
                deleteRepository.Parameters.AddWithValue(id);
                await deleteRepository.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class TestReranker(Func<string, IReadOnlyList<HybridSearchResult>,
        IReadOnlyDictionary<long, double>> score) : IRerankingService
    {
        public string? LastQuery { get; private set; }
        public long[]? LastCandidateIds { get; private set; }
        public Task<IReadOnlyDictionary<long, double>> ScoreAsync(string query,
            IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken)
        {
            LastQuery = query;
            LastCandidateIds = candidates.Select(x => x.ChunkId).ToArray();
            return Task.FromResult(score(query, candidates));
        }
    }

    private sealed class StubEmbeddings : IEmbeddingService
    {
        public static float[] QueryVector
        {
            get
            {
                var vector = new float[768];
                vector[0] = 1;
                return vector;
            }
        }

        public int Calls { get; private set; }

        public Task<EmbeddingResponse> GenerateAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new EmbeddingResponse("embeddinggemma", QueryVector));
        }

        public Task<IReadOnlyList<EmbeddingResponse>> GenerateManyAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task<long> AddRepositoryAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repositories (name, status) VALUES ($1, 'completed') RETURNING id");
        command.Parameters.AddWithValue("day95-hybrid-test-" + Guid.NewGuid().ToString("N"));
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Test repository insert returned no ID."));
    }

    private static async Task<long> AddChunkAsync(NpgsqlDataSource dataSource,
        long repositoryId, string path, string content, float similarity)
    {
        var vector = new float[768];
        vector[0] = similarity;
        vector[1] = MathF.Sqrt(1 - similarity * similarity);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repository_chunks " +
            "(repository_id, source_path, start_line, end_line, content, model, embedding) " +
            "VALUES ($1, $2, 4, 12, $3, 'embeddinggemma', $4) RETURNING id");
        command.Parameters.AddWithValue(repositoryId);
        command.Parameters.AddWithValue(path);
        command.Parameters.AddWithValue(content);
        command.Parameters.AddWithValue(new Vector(vector));
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Test chunk insert returned no ID."));
    }
}
