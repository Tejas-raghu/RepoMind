using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class CloudflareEvidenceVerificationService(CloudflareAiClient client,
    IConfiguration configuration) : IEvidenceVerificationService
{
    public async Task<EvidenceVerificationResult> VerifyAsync(string query,
        IReadOnlyList<HybridSearchResult> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count is < 1 or > 5 || candidates.Select(x => x.ChunkId).Distinct().Count() != candidates.Count)
            throw new ArgumentException("Supply one to five unique candidates.", nameof(candidates));
        var budget = configuration.GetValue<int?>("EvidenceVerification:MaxCharacters") ?? 8_000;
        var (prompt, included, limited) = OllamaEvidenceVerificationService.BuildPrompt(query, candidates, budget);
        if (included.Count == 0)
            return new EvidenceVerificationResult(EvidenceVerificationStatus.Uncertain, false, [],
                "The source chunks exceed the verification context budget.");
        var ids = included.Select(x => x.ChunkId).ToArray();
        var request = new
        {
            messages = new[]
            {
                new { role = "system", content = OllamaEvidenceVerificationService.SystemInstruction +
                    " Return ONLY a JSON object with exactly: reason (at most 120 characters), status " +
                    "(supported, insufficient_evidence, or uncertain), evidenceChunkIds (integer array). " +
                    "Allowed IDs: " + string.Join(',', ids) + ". For no direct evidence, use " +
                    "insufficient_evidence and an empty ID array. No Markdown fences or extra keys." },
                new { role = "user", content = prompt }
            },
            stream = false, max_tokens = 800, temperature = 0.0
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var document = await client.RunAsync(CloudflareLlmService.Model, request,
                    cancellationToken);
                using var output = JsonDocument.Parse(CloudflareLlmService.Content(document.RootElement));
                return OllamaEvidenceVerificationService.ParseDecision(output.RootElement, ids, limited);
            }
            catch (JsonException) when (attempt == 0) { }
        }
        throw new JsonException("Cloudflare verification output remained invalid after one retry.");
    }
}
