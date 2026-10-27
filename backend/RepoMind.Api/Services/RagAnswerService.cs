using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class RagAnswerService(IRagEvidenceVerifier verifier, IRagContextBuilder contextBuilder,
    ILlmService llm, IConfiguration configuration, IHostEnvironment environment,
    ILogger<RagAnswerService> logger) : IRagAnswerService
{
    private readonly int timeoutSeconds = GetTimeout(configuration);

    public async Task<RagAnswerResponse?> AnswerAsync(long repositoryId, string query, int topK,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var prepared = await verifier.PrepareAsync(repositoryId, query, topK, cancellationToken);
        if (prepared is null) return null;
        var decision = prepared.Verification;

        RagAnswerResponse Failure(string status, string message) => new(status, message, [], null, null, null)
        {
            DurationMs = elapsed.Elapsed.TotalMilliseconds,
            VerifierDurationMs = decision.VerifierDurationMs,
            RerankingStatus = prepared.Search.RerankingStatus
        };

        if (decision.Status != EvidenceVerificationStatus.Supported || !decision.Answerable)
            return Failure(decision.Status switch
            {
                EvidenceVerificationStatus.InsufficientEvidence => "insufficient_evidence",
                EvidenceVerificationStatus.Uncertain => "uncertain",
                _ => "verification_unavailable"
            }, decision.Reason);

        var retrieved = prepared.Search.Results.ToDictionary(x => x.ChunkId);
        var evidenceIds = decision.EvidenceChunkIds;
        if (evidenceIds.Count == 0 || evidenceIds.Distinct().Count() != evidenceIds.Count ||
            evidenceIds.Any(id => !retrieved.ContainsKey(id)))
            return Failure("verification_unavailable", "Verified source references were invalid.");

        var chunks = evidenceIds.Select(id => retrieved[id]).Select(x => new RepositorySearchResult(
            x.ChunkId, x.FilePath, x.StartLine, x.EndLine, x.Content, x.CosineSimilarity)).ToArray();
        var context = contextBuilder.BuildVerified(repositoryId, query, chunks);
        if (context.ChunksOmittedDueToBudget || context.Sources.Count != chunks.Length ||
            context.Sources.Select(x => x.ChunkId).Except(evidenceIds).Any())
            return Failure("insufficient_context", "Verified evidence could not fit the answer context.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        GroundedGenerationResult generated;
        try
        {
            generated = await llm.GenerateGroundedAsync(query, context.Context, evidenceIds, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("generation_unavailable", "The local answer model timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            logger.LogWarning("Grounded answer generation failed: {ErrorType}", exception.GetType().Name);
            return Failure("generation_unavailable", "The local answer model returned no usable answer.");
        }

        var allowed = context.Sources.Select(x => x.ChunkId).ToHashSet();
        var cited = Regex.Matches(generated.Answer ?? "", @"\[source:(\d+)\]")
            .Select(match => long.TryParse(match.Groups[1].Value, out var id) ? id : -1)
            .ToHashSet();
        var allMarkers = Regex.Matches(generated.Answer ?? "", @"\[source:[^\]]*\]").Count;
        if (string.IsNullOrWhiteSpace(generated.Answer) || generated.SourceIds.Count == 0 ||
            generated.SourceIds.Distinct().Count() != generated.SourceIds.Count ||
            generated.SourceIds.Any(id => !allowed.Contains(id)) ||
            (cited.Count > 0 && !cited.SetEquals(generated.SourceIds)) ||
            !cited.IsSubsetOf(allowed) || allMarkers != cited.Count)
        {
            if (environment.IsDevelopment())
                logger.LogInformation("RAG generation rejected: allowed IDs {Allowed}, listed IDs {Listed}, inline IDs {Inline}",
                    string.Join(',', allowed), string.Join(',', generated.SourceIds), string.Join(',', cited));
            return Failure("generation_unavailable", "The answer contained invalid source references.");
        }

        // Structured IDs are the authority. If the model omitted inline markers, add only
        // the validated IDs; never infer a source from free-form answer text.
        var answer = cited.Count == 0
            ? generated.Answer.TrimEnd() + " " + string.Join(' ', generated.SourceIds.Select(id => $"[source:{id}]"))
            : generated.Answer;
        cited = generated.SourceIds.ToHashSet();

        var sources = chunks.Where(x => cited.Contains(x.ChunkId))
            .Select(x => new RagAnswerSource(x.ChunkId, x.FilePath, x.StartLine, x.EndLine, x.Content))
            .ToArray();
        if (environment.IsDevelopment())
            logger.LogInformation("RAG answer repository {RepositoryId}: evidence {EvidenceCount}, cited {CitedCount}, reranking {RerankingStatus}, elapsed {DurationMs} ms",
                repositoryId, evidenceIds.Count, sources.Length, prepared.Search.RerankingStatus,
                elapsed.Elapsed.TotalMilliseconds);
        return new RagAnswerResponse("answered", answer, sources, generated.Model,
            generated.PromptTokens, generated.GeneratedTokens)
        {
            DurationMs = elapsed.Elapsed.TotalMilliseconds,
            VerifierDurationMs = decision.VerifierDurationMs,
            RerankingStatus = prepared.Search.RerankingStatus
        };
    }

    private static int GetTimeout(IConfiguration configuration)
    {
        var seconds = configuration.GetValue<int?>("RagAnswer:TimeoutSeconds") ?? 90;
        if (seconds is < 1 or > 180)
            throw new InvalidOperationException("RagAnswer:TimeoutSeconds must be between 1 and 180.");
        return seconds;
    }
}
