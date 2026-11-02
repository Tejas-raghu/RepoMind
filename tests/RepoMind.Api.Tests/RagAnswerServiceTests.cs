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
    public async Task Supported_answer_uses_one_snapshot_and_trusted_source()
    {
        var llm = new FakeLlm(new GroundedGenerationResult("Bearer header is set.",
            "The client adds the token.", [42], "qwen3:4b", 50, 20, 100));
        var retrieval = new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42]));
        var result = await Create(retrieval, llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("answered", result?.Status);
        Assert.Equal(1, retrieval.Calls);
        Assert.Equal(1, llm.Calls);
        Assert.Equal([42L], llm.Prompt!.IncludedChunkIds);
        Assert.EndsWith("[source:42]", result!.Answer);
        Assert.Equal(Chunk.Content, result.Sources[0].Content);
        Assert.Equal((Chunk.FilePath, Chunk.StartLine, Chunk.EndLine),
            (result.Sources[0].FilePath, result.Sources[0].StartLine, result.Sources[0].EndLine));
        Assert.Equal((50, 20), (result.PromptTokens, result.GeneratedTokens));
    }

    [Fact]
    public async Task Matching_inline_marker_is_rendered_once()
    {
        var llm = new FakeLlm(new GroundedGenerationResult("Bearer header is set [source:42].",
            "The client adds the token.", [42], "qwen3:4b", null, null, null));
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])), llm)
            .AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("answered", result?.Status);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result!.Answer, @"\[source:42\]").Cast<System.Text.RegularExpressions.Match>());
    }

    [Fact]
    public async Task Generated_insufficiency_is_not_presented_as_an_answer()
    {
        var llm = new FakeLlm(new GroundedGenerationResult("Cannot determine from the supplied code.",
            "", [42], "qwen3:4b", null, null, null));
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])), llm)
            .AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("insufficient_evidence", result?.Status);
        Assert.Empty(result!.Sources);
    }

    [Theory]
    [InlineData(EvidenceVerificationStatus.InsufficientEvidence, "insufficient_evidence")]
    [InlineData(EvidenceVerificationStatus.Uncertain, "uncertain")]
    [InlineData(EvidenceVerificationStatus.VerificationUnavailable, "verification_unavailable")]
    public async Task Unverified_requests_never_generate(EvidenceVerificationStatus status, string expected)
    {
        var llm = new FakeLlm(null);
        var result = await Create(new FakeRetrieval(Prepared(status, [])), llm)
            .AnswerAsync(7, "Stripe?", 5, CancellationToken.None);
        Assert.Equal(expected, result?.Status);
        Assert.Equal(0, llm.Calls);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(0)]
    public async Task Unknown_or_missing_citation_is_rejected(long id)
    {
        var llm = new FakeLlm(new GroundedGenerationResult("Claim", "", id == 0 ? [] : [id],
            "qwen3:4b", null, null, null));
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])), llm)
            .AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("generation_unavailable", result?.Status);
    }

    [Fact]
    public async Task Excluded_verified_citation_is_rejected()
    {
        var large = Chunk with { ChunkId = 99, Content = new string('x', 6000) };
        var retrieval = new FakeRetrieval(new VerifiedRetrieval(
            new HybridSearchResponse(7, "Bearer?", [Chunk, large]),
            new EvidenceVerificationResponse(7, "Bearer?", EvidenceVerificationStatus.Supported,
                true, [42, 99], [], "Supported")));
        var llm = new FakeLlm(new GroundedGenerationResult("Claim", "", [99],
            "qwen3:4b", null, null, null));
        var result = await Create(retrieval, llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("generation_unavailable", result?.Status);
        Assert.Equal([42L], llm.Prompt!.IncludedChunkIds);
        Assert.Equal(1, llm.Prompt.OmittedChunks);
    }

    [Fact]
    public async Task Oversized_evidence_never_generates()
    {
        var huge = Chunk with { Content = new string('x', 8000) };
        var llm = new FakeLlm(null);
        var retrieval = new FakeRetrieval(new VerifiedRetrieval(
            new HybridSearchResponse(7, "Bearer?", [huge]),
            new EvidenceVerificationResponse(7, "Bearer?", EvidenceVerificationStatus.Supported,
                true, [42], [], "Supported")));
        var result = await Create(retrieval, llm).AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
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
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [42])), llm)
            .AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("generation_unavailable", result?.Status);
        Assert.Empty(result!.Sources);
    }

    [Fact]
    public async Task Missing_repository_and_foreign_verifier_id_never_generate()
    {
        var llm = new FakeLlm(null);
        Assert.Null(await Create(new FakeRetrieval(null), llm)
            .AnswerAsync(999, "Bearer?", 5, CancellationToken.None));
        var result = await Create(new FakeRetrieval(Prepared(EvidenceVerificationStatus.Supported, [99])), llm)
            .AnswerAsync(7, "Bearer?", 5, CancellationToken.None);
        Assert.Equal("verification_unavailable", result?.Status);
        Assert.Equal(0, llm.Calls);
    }

    private static RagAnswerService Create(IRagEvidenceVerifier retrieval, ILlmService llm)
    {
        var config = new ConfigurationBuilder().Build();
        return new RagAnswerService(retrieval, new RagGenerationPromptBuilder(config), llm,
            config, new TestHostEnvironment(), NullLogger<RagAnswerService>.Instance);
    }

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

    private sealed class FakeLlm(GroundedGenerationResult? result) : ILlmService
    {
        public int Calls { get; private set; }
        public RagGenerationPrompt? Prompt { get; private set; }
        public string? Failure { get; init; }
        public Task<LlmChatResponse> ChatAsync(string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
        public Task<GroundedGenerationResult> GenerateGroundedAsync(RagGenerationPrompt prompt,
            CancellationToken cancellationToken)
        {
            Calls++;
            Prompt = prompt;
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
