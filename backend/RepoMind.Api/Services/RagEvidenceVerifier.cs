using System.Diagnostics;
using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed record VerifiedRetrieval(HybridSearchResponse Search,
    EvidenceVerificationResponse Verification);

public interface IRagEvidenceVerifier
{
    Task<VerifiedRetrieval?> PrepareAsync(long repositoryId, string query, int topK,
        CancellationToken cancellationToken);
}

public sealed class RagEvidenceVerifier(HybridSearchService search, IEvidenceVerificationService verifier)
    : IRagEvidenceVerifier
{
    public async Task<EvidenceVerificationResponse?> VerifyAsync(long repositoryId, string query,
        int topK, CancellationToken cancellationToken)
        => (await PrepareAsync(repositoryId, query, topK, cancellationToken))?.Verification;

    public async Task<VerifiedRetrieval?> PrepareAsync(long repositoryId, string query,
        int topK, CancellationToken cancellationToken)
    {
        var retrieved = await search.SearchAsync(repositoryId, query, topK, cancellationToken);
        if (retrieved is null) return null;
        // The verifier receives the same final ranking, capped at five complete chunks.
        var candidates = retrieved.Results.Take(5).ToArray();
        var stopwatch = new Stopwatch();
        var verifierCalled = false;
        if (candidates.Length == 0)
            return Result(new EvidenceVerificationResult(EvidenceVerificationStatus.InsufficientEvidence,
                false, [], "No source chunks were retrieved."));

        verifierCalled = true;
        stopwatch.Start();
        try
        {
            var decision = await verifier.VerifyAsync(query, candidates, cancellationToken);
            stopwatch.Stop();
            ValidateDecision(decision, candidates);
            if (retrieved.Results.Count > candidates.Length &&
                decision.Status == EvidenceVerificationStatus.InsufficientEvidence)
                decision = new EvidenceVerificationResult(EvidenceVerificationStatus.Uncertain,
                    false, [], "Some retrieved chunks were not verified.");
            return Result(decision);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Unavailable();
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            return Unavailable();
        }
        catch (JsonException)
        {
            stopwatch.Stop();
            return Unavailable();
        }

        VerifiedRetrieval Unavailable() => Result(new EvidenceVerificationResult(
            EvidenceVerificationStatus.VerificationUnavailable, false, [],
            "The local evidence verifier is unavailable."));

        VerifiedRetrieval Result(EvidenceVerificationResult decision)
        {
            var byId = candidates.ToDictionary(x => x.ChunkId);
            var sources = decision.EvidenceChunkIds.Select(id =>
            {
                var chunk = byId[id];
                return new EvidenceSource(id, chunk.FilePath, chunk.StartLine, chunk.EndLine);
            }).ToArray();
            // Never expose model-authored text, which may quote secrets or repository instructions.
            var safeReason = decision.Status switch
            {
                EvidenceVerificationStatus.Supported => "Direct source evidence was identified.",
                EvidenceVerificationStatus.InsufficientEvidence => "No supplied source directly supports the request.",
                EvidenceVerificationStatus.Uncertain => "The supplied source evidence is partial or ambiguous.",
                _ => "The local evidence verifier is unavailable."
            };
            var verification = new EvidenceVerificationResponse(repositoryId, query, decision.Status,
                decision.Answerable, decision.EvidenceChunkIds, sources, safeReason)
            { VerifierDurationMs = verifierCalled ? stopwatch.Elapsed.TotalMilliseconds : null,
                RerankingStatus = retrieved.RerankingStatus };
            return new VerifiedRetrieval(retrieved, verification);
        }
    }

    private static void ValidateDecision(EvidenceVerificationResult decision,
        IReadOnlyList<HybridSearchResult> retrieved)
    {
        var allowed = retrieved.Select(x => x.ChunkId).ToHashSet();
        if (decision.Status is not (EvidenceVerificationStatus.Supported or
            EvidenceVerificationStatus.InsufficientEvidence or EvidenceVerificationStatus.Uncertain) ||
            decision.EvidenceChunkIds.Distinct().Count() != decision.EvidenceChunkIds.Count ||
            decision.EvidenceChunkIds.Any(id => !allowed.Contains(id)) ||
            (decision.Status == EvidenceVerificationStatus.Supported) != decision.Answerable ||
            (decision.Status == EvidenceVerificationStatus.Supported && decision.EvidenceChunkIds.Count == 0) ||
            (decision.Status != EvidenceVerificationStatus.Supported && decision.EvidenceChunkIds.Count > 0))
            throw new JsonException("Invalid evidence verification result.");
    }
}
