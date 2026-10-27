using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class RagAnswerServiceTests
{
    private static readonly HybridSearchResult Chunk = new(42, "src/apiClient.ts", 4, 12,
        "headers.set('Authorization', `Bearer ${token}`);", 0.8, 1, 1, 0.03);

    [Fact]
    public async Task Supported_answer_uses_one_snapshot_and_stored_source()
    {
        var llm = new FakeLlm(new GroundedGenerationResult(
            "The client sets the Bearer header [source:42].", [42], "qwen3:4b", 50, 20, 100));
        var context = new FakeContext();
        var retrieval = new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42]));
        var service = Create(retrieval, context, llm);

        var result = await service.AnswerAsync(7, "Where is the Bearer token added?", 5,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("answered", result.Status);
        Assert.Equal(1, retrieval.Calls);
        Assert.Equal(1, context.Calls);
        Assert.Equal(1, llm.Calls);
        Assert.Equal([42L], context.ReceivedIds);
        Assert.Equal(Chunk.Content, result.Sources[0].Content);
        Assert.Equal((Chunk.FilePath, Chunk.StartLine, Chunk.EndLine),
            (result.Sources[0].FilePath, result.Sources[0].StartLine, result.Sources[0].EndLine));
        Assert.Equal((50, 20), (result.PromptTokens, result.GeneratedTokens));
    }

    [Theory]
    [InlineData(EvidenceVerificationStatus.InsufficientEvidence, "insufficient_evidence")]
    [InlineData(EvidenceVerificationStatus.Uncertain, "uncertain")]
    [InlineData(EvidenceVerificationStatus.VerificationUnavailable, "verification_unavailable")]
    public async Task Unverified_requests_never_generate(EvidenceVerificationStatus status,
        string expected)
    {
        var llm = new FakeLlm(null);
        var context = new FakeContext();
        var result = await Create(new FakeRetrieval(Prepared(status, [])), context, llm)
            .AnswerAsync(7, "Stripe payments?", 5, CancellationToken.None);
        Assert.Equal(expected, result?.Status);
        Assert.Empty(result!.Sources);
        Assert.Equal(0, llm.Calls);
        Assert.Equal(0, context.Calls);
    }

    [Theory]
    [InlineData("The header is set [source:99].", 99)]
    [InlineData("The header is set [source:42].", 99)]
    [InlineData("The header is set [source:99].", 42)]
    public async Task Fabricated_or_missing_citations_are_rejected(string answer, long id)
    {
        var llm = new FakeLlm(new GroundedGenerationResult(answer, [id], "qwen3:4b", null, null, null));
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])),
            new FakeContext(), llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("generation_unavailable", result?.Status);
        Assert.Empty(result!.Sources);
    }

    [Fact]
    public async Task Structured_source_ids_add_missing_inline_citation()
    {
        var llm = new FakeLlm(new GroundedGenerationResult("The header is set.", [42],
            "qwen3:4b", null, null, null));
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])),
            new FakeContext(), llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("answered", result?.Status);
        Assert.EndsWith("[source:42]", result!.Answer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_oversized_context_never_generates(bool oversized)
    {
        var llm = new FakeLlm(null);
        var context = new FakeContext { Omit = oversized, Empty = !oversized };
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])),
            context, llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("insufficient_context", result?.Status);
        Assert.Equal(0, llm.Calls);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("timeout")]
    public async Task Generation_failures_are_safe(string failure)
    {
        var llm = new FakeLlm(null) { Failure = failure };
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])),
            new FakeContext(), llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("generation_unavailable", result?.Status);
        Assert.Empty(result!.Sources);
    }

    [Fact]
    public async Task Missing_repository_and_foreign_verifier_id_never_generate()
    {
        var llm = new FakeLlm(null);
        Assert.Null(await Create(new FakeRetrieval(null), new FakeContext(), llm)
            .AnswerAsync(999, "Bearer?", 5, CancellationToken.None));
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [99])),
            new FakeContext(), llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("verification_unavailable", result?.Status);
        Assert.Equal(0, llm.Calls);
    }

    private static RagAnswerService Create(IRagEvidenceVerifier retrieval, IRagContextBuilder context,
        ILlmService llm) => new(retrieval, context, llm,
        new ConfigurationBuilder().Build(), new TestHostEnvironment(),
        NullLogger<RagAnswerService>.Instance);

    private static VerifiedRetrieval Prepared(EvidenceVerificationStatus status, long[] ids) => new(
        new HybridSearchResponse(7, "Bearer?", [Chunk]),
        new EvidenceVerificationResponse(7, "Bearer?", status,
            status == EvidenceVerificationStatus.Supported, ids, [], "Safe reason"));

    private sealed class FakeRetrieval(VerifiedRetrieval? result) : IRagEvidenceVerifier
    {
        public int Calls { get; private set; }
        public Task<VerifiedRetrieval?> PrepareAsync(long repositoryId, string query, int topK,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeContext : IRagContextBuilder
    {
        public int Calls { get; private set; }
        public long[] ReceivedIds { get; private set; } = [];
        public bool Omit { get; init; }
        public bool Empty { get; init; }
        public RagContextResponse BuildVerified(long repositoryId, string query,
            IReadOnlyList<RepositorySearchResult> verifiedChunks)
        {
            Calls++;
            ReceivedIds = verifiedChunks.Select(x => x.ChunkId).ToArray();
            var sources = Empty ? [] : verifiedChunks.Select((x, i) =>
                new RagSource(x.ChunkId, i + 1, x.FilePath, x.StartLine, x.EndLine, x.Similarity)).ToArray();
            return new RagContextResponse(repositoryId, query, "Verified source context", sources,
                sources.Length, 23, Omit, Empty);
        }
        public Task<RagContextResponse?> BuildAsync(long repositoryId, string query, int topK,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No second retrieval allowed.");
    }

    private sealed class FakeLlm(GroundedGenerationResult? result) : ILlmService
    {
        public int Calls { get; private set; }
        public string? Failure { get; init; }
        public Task<LlmChatResponse> ChatAsync(string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
        public Task<GroundedGenerationResult> GenerateGroundedAsync(string question, string context,
            IReadOnlyList<long> sourceIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Failure switch
            {
                "http" => Task.FromException<GroundedGenerationResult>(new HttpRequestException()),
                "json" => Task.FromException<GroundedGenerationResult>(new JsonException()),
                "timeout" => Task.FromException<GroundedGenerationResult>(new TaskCanceledException()),
                _ => Task.FromResult(result!)
            };
        }
    }
}
