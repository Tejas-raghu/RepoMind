using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public static class GroundedOutputParser
{
    public static GroundedGenerationResult Parse(JsonElement answerRoot,
        IReadOnlyList<long> sourceIds, string model, int? promptTokens,
        int? generatedTokens, double? durationMs)
    {
        if (answerRoot.ValueKind != JsonValueKind.Object || answerRoot.EnumerateObject().Count() != 3 ||
            !answerRoot.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String ||
            !answerRoot.TryGetProperty("explanation", out var explanation) || explanation.ValueKind != JsonValueKind.String ||
            !answerRoot.TryGetProperty("citationChunkIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid grounded generation output.");
        var summaryText = summary.GetString()!;
        var explanationText = explanation.GetString()!;
        if (summaryText.Length > 600 || explanationText.Length > 2400 || ids.GetArrayLength() > sourceIds.Count)
            throw new JsonException("Grounded generation fields exceeded their limits.");
        var citedIds = new List<long>();
        foreach (var value in ids.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id))
                throw new JsonException("Invalid source ID.");
            citedIds.Add(id);
        }
        if (citedIds.Count != citedIds.Distinct().Count() || citedIds.Any(id => !sourceIds.Contains(id)))
            throw new JsonException("Invalid citation IDs.");
        return new GroundedGenerationResult(summaryText, explanationText, citedIds, model,
            promptTokens, generatedTokens, durationMs);
    }
}
