using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class RagContextIntegrationTests
{
    [Fact]
    public async Task Builder_uses_hybrid_rank_and_handles_missing_and_empty_repositories()
    {
        var connectionString = Environment.GetEnvironmentVariable("REPOMIND_TEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=repomind;Username=repomind;Password=localdev";
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        await using var dataSource = dataSourceBuilder.Build();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RepositorySearch:MinimumSimilarity"] = "0.35",
            ["RagContext:MaxCharacters"] = "10000",
            ["HybridSearch:VectorCandidates"] = "15",
            ["HybridSearch:LexicalCandidates"] = "15"
        }).Build();
        var embeddings = new StubEmbeddings();
        var store = new RepositorySearchStore(dataSource, config);
        var repositoryIds = new List<long>();

        try
        {
            var selected = await AddRepositoryAsync(dataSource);
            repositoryIds.Add(selected);
            var other = await AddRepositoryAsync(dataSource);
            repositoryIds.Add(other);
            var empty = await AddRepositoryAsync(dataSource);
            repositoryIds.Add(empty);

            var readmeId = await AddChunkAsync(dataSource, selected, "README.md",
                "Where is the Bearer token added to requests?");
            var codeId = await AddChunkAsync(dataSource, selected, "src/apiClient.ts",
                "headers.set('Authorization', `Bearer ${token}`);", 0.32f);
            var otherId = await AddChunkAsync(dataSource, selected, "src/Other.ts", "const bearer = 'other';", 0.31f);
            var privateId = await AddChunkAsync(dataSource, other, "src/Private.cs", "Bearer private");

            var builderVerifier = new StubVerifier(new EvidenceVerificationResult(
                EvidenceVerificationStatus.Supported, true, [codeId], "code evidence"));
            var builder = new RagContextBuilder(new RagEvidenceVerifier(
                new HybridSearchService(store, embeddings, new NoOpReranker(), config,
                    new TestHostEnvironment(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance),
                builderVerifier), config);

            var found = await builder.BuildAsync(selected,
                "Where is the Bearer token added to requests?", 3, CancellationToken.None);
            Assert.NotNull(found);
            Assert.Single(found.Sources);
            Assert.Contains("headers.set('Authorization'", found.Context);
            Assert.DoesNotContain("Bearer private", found.Context);
            Assert.DoesNotContain("README.md", found.Context);
            Assert.Equal("src/apiClient.ts", found.Sources[0].FilePath);
            Assert.Equal(EvidenceVerificationStatus.Supported, found.Verification?.Status);
            Assert.True(found.Verification?.Answerable);
            Assert.Equal(1, builderVerifier.Calls);
            Assert.Equal((4, 12), (found.Sources[0].StartLine, found.Sources[0].EndLine));
            Assert.Equal(1, found.Sources[0].SourceNumber);
            Assert.Equal(1, embeddings.Calls);

            var enabledConfig = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Reranking:Enabled"] = "true",
                    ["Reranking:MaxCandidates"] = "3"
                }).Build();
            var recordingVerifier = new RecordingVerifier(codeId);
            var rerankedBuilder = new RagContextBuilder(new RagEvidenceVerifier(
                new HybridSearchService(store, embeddings, new PathReranker(), enabledConfig,
                    new TestHostEnvironment(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance),
                recordingVerifier), config);
            var rerankedContext = await rerankedBuilder.BuildAsync(selected,
                "Where is the Bearer token added to requests?", 3, CancellationToken.None);
            Assert.NotNull(rerankedContext);
            Assert.Equal([otherId, codeId, readmeId], recordingVerifier.CandidateIds);
            Assert.Equal("applied", rerankedContext.RerankingStatus);
            Assert.Single(rerankedContext.Sources);
            Assert.Equal(codeId, rerankedContext.Sources[0].ChunkId);
            Assert.Equal(2, embeddings.Calls);

            var queryVector = new float[768];
            queryVector[0] = 1;
            var day7Results = await new RepositorySearchStore(dataSource, config).SearchAsync(
                selected, "embeddinggemma", queryVector, 5, CancellationToken.None);
            Assert.Single(day7Results);
            Assert.Equal("README.md", day7Results[0].FilePath);

            var verifier = new RagEvidenceVerifier(new HybridSearchService(store, embeddings,
                new NoOpReranker(), config, new TestHostEnvironment(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance),
                new StubVerifier(new EvidenceVerificationResult(EvidenceVerificationStatus.Supported,
                    true, [codeId], "untrusted model reason")));
            var verified = await verifier.VerifyAsync(selected,
                "Where is the Bearer token added to requests?", 3, CancellationToken.None);
            Assert.NotNull(verified);
            Assert.Equal(EvidenceVerificationStatus.Supported, verified.Status);
            Assert.True(verified.Answerable);
            Assert.Equal([codeId], verified.EvidenceChunkIds);
            Assert.Equal("src/apiClient.ts", verified.Sources[0].FilePath);
            Assert.Equal((4, 12), (verified.Sources[0].StartLine, verified.Sources[0].EndLine));
            Assert.DoesNotContain("untrusted model reason", verified.Reason);
            Assert.DoesNotContain("Private", verified.Sources[0].FilePath);

            var invalid = new RagEvidenceVerifier(new HybridSearchService(store, embeddings,
                new NoOpReranker(), config, new TestHostEnvironment(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance),
                new StubVerifier(new EvidenceVerificationResult(EvidenceVerificationStatus.Supported,
                    true, [privateId], "private")));
            var rejected = await invalid.VerifyAsync(selected, "Bearer", 3, CancellationToken.None);
            Assert.NotNull(rejected);
            Assert.Equal(EvidenceVerificationStatus.VerificationUnavailable, rejected.Status);
            Assert.Empty(rejected.Sources);
            Assert.False(rejected.Answerable);

            foreach (var failure in new Exception[]
            {
                new JsonException("malformed model JSON"),
                new TaskCanceledException("local timeout"),
                new HttpRequestException("model unavailable")
            })
            {
                var failing = new RagEvidenceVerifier(new HybridSearchService(store, embeddings,
                    new NoOpReranker(), config, new TestHostEnvironment(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance),
                    new FailureVerifier(failure));
                var unavailable = await failing.VerifyAsync(selected, "Bearer", 3,
                    CancellationToken.None);
                Assert.NotNull(unavailable);
                Assert.Equal(EvidenceVerificationStatus.VerificationUnavailable, unavailable.Status);
                Assert.False(unavailable.Answerable);
                Assert.Empty(unavailable.EvidenceChunkIds);
                Assert.Empty(unavailable.Sources);
            }

            var notCalled = new StubVerifier(new EvidenceVerificationResult(
                EvidenceVerificationStatus.Supported, true, [codeId], "should not be called"));
            var emptyVerifier = new RagEvidenceVerifier(new HybridSearchService(store, embeddings,
                new NoOpReranker(), config, new TestHostEnvironment(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance), notCalled);
            var emptyVerification = await emptyVerifier.VerifyAsync(empty, "Bearer", 5,
                CancellationToken.None);
            Assert.NotNull(emptyVerification);
            Assert.Equal(EvidenceVerificationStatus.InsufficientEvidence, emptyVerification.Status);
            Assert.Equal(0, notCalled.Calls);
            Assert.Null(await emptyVerifier.VerifyAsync(long.MaxValue, "Bearer", 5,
                CancellationToken.None));

            var noResults = await builder.BuildAsync(empty, "Bearer", 5, CancellationToken.None);
            Assert.NotNull(noResults);
            Assert.Empty(noResults.Sources);
            Assert.Equal(7, embeddings.Calls);

            var unavailableBuilder = new RagContextBuilder(new RagEvidenceVerifier(
                new HybridSearchService(store, embeddings, new NoOpReranker(), config,
                    new TestHostEnvironment(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance),
                new FailureVerifier(new HttpRequestException("model unavailable"))), config);
            var noEvidenceContext = await unavailableBuilder.BuildAsync(selected, "Bearer", 3,
                CancellationToken.None);
            Assert.NotNull(noEvidenceContext);
            Assert.Empty(noEvidenceContext.Sources);
            Assert.DoesNotContain("headers.set", noEvidenceContext.Context);
            Assert.Equal(EvidenceVerificationStatus.VerificationUnavailable,
                noEvidenceContext.Verification?.Status);
            Assert.False(noEvidenceContext.Verification?.Answerable);

            var hugeId = await AddChunkAsync(dataSource, selected, "src/huge.ts",
                new string('x', 2_000));
            var tightConfig = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["RagContext:MaxCharacters"] = "1000" }).Build();
            var tightBuilder = new RagContextBuilder(new RagEvidenceVerifier(
                new HybridSearchService(store, embeddings, new NoOpReranker(), config,
                    new TestHostEnvironment(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance),
                new StubVerifier(new EvidenceVerificationResult(EvidenceVerificationStatus.Supported,
                    true, [hugeId], "large evidence"))), tightConfig);
            var tooLarge = await tightBuilder.BuildAsync(selected, "x", 5, CancellationToken.None);
            Assert.NotNull(tooLarge);
            Assert.Empty(tooLarge.Sources);
            Assert.True(tooLarge.InsufficientBudget);
            Assert.Equal(EvidenceVerificationStatus.Uncertain, tooLarge.Verification?.Status);
            Assert.False(tooLarge.Verification?.Answerable);
            Assert.DoesNotContain("src/huge.ts", tooLarge.Context);

            Assert.Null(await builder.BuildAsync(long.MaxValue, "Bearer", 5, CancellationToken.None));
            Assert.Equal(9, embeddings.Calls);
        }
        finally
        {
            foreach (var repositoryId in repositoryIds)
            {
                await using var deleteChunks = dataSource.CreateCommand(
                    "DELETE FROM repository_chunks WHERE repository_id = $1");
                deleteChunks.Parameters.AddWithValue(repositoryId);
                await deleteChunks.ExecuteNonQueryAsync();
                await using var deleteRepository = dataSource.CreateCommand(
                    "DELETE FROM repositories WHERE id = $1");
                deleteRepository.Parameters.AddWithValue(repositoryId);
                await deleteRepository.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class StubVerifier(EvidenceVerificationResult result) : IEvidenceVerificationService
    {
        public int Calls { get; private set; }
        public Task<EvidenceVerificationResult> VerifyAsync(string query,
            IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FailureVerifier(Exception failure) : IEvidenceVerificationService
    {
        public Task<EvidenceVerificationResult> VerifyAsync(string query,
            IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken) =>
            Task.FromException<EvidenceVerificationResult>(failure);
    }

    private sealed class PathReranker : IRerankingService
    {
        public Task<IReadOnlyDictionary<long, double>> ScoreAsync(string query,
            IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, double>>(candidates.ToDictionary(
                x => x.ChunkId, x => x.FilePath switch
                {
                    "src/Other.ts" => 99.0,
                    "src/apiClient.ts" => 90.0,
                    _ => 5.0
                }));
    }

    private sealed class RecordingVerifier(long evidenceId) : IEvidenceVerificationService
    {
        public long[] CandidateIds { get; private set; } = [];
        public Task<EvidenceVerificationResult> VerifyAsync(string query,
            IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken)
        {
            CandidateIds = candidates.Select(x => x.ChunkId).ToArray();
            return Task.FromResult(new EvidenceVerificationResult(
                EvidenceVerificationStatus.Supported, true, [evidenceId], "direct evidence"));
        }
    }

    private sealed class StubEmbeddings : IEmbeddingService
    {
        public int Calls { get; private set; }

        public Task<EmbeddingResponse> GenerateAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            var vector = new float[768];
            vector[0] = 1;
            return Task.FromResult(new EmbeddingResponse("embeddinggemma", vector));
        }

        public Task<IReadOnlyList<EmbeddingResponse>> GenerateManyAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task<long> AddRepositoryAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO repositories (name, status) VALUES ($1, 'completed') RETURNING id");
        command.Parameters.AddWithValue("day9-rag-test-" + Guid.NewGuid().ToString("N"));
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Test repository insert returned no ID."));
    }

    private static async Task<long> AddChunkAsync(NpgsqlDataSource dataSource,
        long repositoryId, string path, string content, float similarity = 1)
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
