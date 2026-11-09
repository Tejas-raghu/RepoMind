using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class RagAnswerService(IRagEvidenceVerifier verifier, RagGenerationPromptBuilder promptBuilder,
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
        var prompt = promptBuilder.Build(query, chunks);
        if (prompt.IncludedChunkIds.Count == 0)
            return Failure("insufficient_context", "No complete verified evidence fits the answer context.");
        if (prompt.OmittedChunks > 0)
            logger.LogInformation("RAG answer omitted {OmittedCount} verified chunks for repository {RepositoryId}",
                prompt.OmittedChunks, repositoryId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        GroundedGenerationResult generated;
        try
        {
            generated = await llm.GenerateGroundedAsync(prompt, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("generation_unavailable", "The answer model timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            logger.LogWarning("Grounded answer generation failed: {ErrorType}: {Reason}",
                exception.GetType().Name, exception.Message);
            return Failure("generation_unavailable", "The answer model returned no usable answer.");
        }

        logger.LogInformation("RAG prompt estimate {EstimatedTokens}, actual {ActualTokens}, output {OutputTokens}, omitted chunks {OmittedCount}",
            prompt.EstimatedInputTokens, generated.PromptTokens, generated.GeneratedTokens,
            prompt.OmittedChunks);
        if (generated.PromptTokens is { } actual &&
            actual + prompt.Options.MaxOutputTokens + prompt.Options.ReservedTokens >
            prompt.Options.ContextWindowTokens)
            return Failure("generation_unavailable", "The answer prompt exceeded the configured model context.");

        if (Regex.IsMatch(generated.Summary, @"\b(cannot answer|cannot determine|insufficient evidence|not enough evidence|no relevant source)\b",
            RegexOptions.IgnoreCase))
            return Failure("insufficient_evidence", "The supplied evidence does not support an answer.");

        var allowed = prompt.IncludedChunkIds.ToHashSet();
        var prose = generated.Summary + " " + generated.Explanation;
        var markers = Regex.Matches(prose, @"\[source:([^\]]*)\]");
        var markerIds = new HashSet<long>();
        var validMarkers = true;
        foreach (Match marker in markers)
        {
            if (!long.TryParse(marker.Groups[1].Value, out var markerId) ||
                !allowed.Contains(markerId)) validMarkers = false;
            else markerIds.Add(markerId);
        }
        if (string.IsNullOrWhiteSpace(generated.Summary) || generated.SourceIds.Count == 0 ||
            generated.SourceIds.Distinct().Count() != generated.SourceIds.Count ||
            generated.SourceIds.Any(id => !allowed.Contains(id)) ||
            generated.Summary.Length > 600 || generated.Explanation.Length > 2400 ||
            !validMarkers || (markers.Count > 0 && !markerIds.SetEquals(generated.SourceIds)))
        {
            logger.LogWarning("RAG generation rejected: summary chars {SummaryLength}, explanation chars {ExplanationLength}, " +
                "listed IDs {Listed}, marker IDs {Markers}, valid markers {ValidMarkers}, allowed IDs {Allowed}",
                generated.Summary.Length, generated.Explanation.Length,
                string.Join(',', generated.SourceIds), string.Join(',', markerIds), validMarkers,
                string.Join(',', allowed));
            return Failure("generation_unavailable", "The answer contained invalid source references.");
        }

        // Citations are rendered from validated IDs, never from model-written markers.
        var summary = Regex.Replace(generated.Summary, @"\s*\[source:[^\]]*\]", "").Trim();
        var explanation = Regex.Replace(generated.Explanation, @"\s*\[source:[^\]]*\]", "").Trim();
        var answer = summary +
            (string.IsNullOrWhiteSpace(explanation) ? "" : " " + explanation) +
            " " + string.Join(' ', generated.SourceIds.Select(id => $"[source:{id}]"));
        var cited = generated.SourceIds.ToHashSet();

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
            RerankingStatus = prepared.Search.RerankingStatus,
            Summary = summary,
            Explanation = explanation
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
