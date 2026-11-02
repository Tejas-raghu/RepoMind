using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class OllamaLlmService(HttpClient httpClient, IConfiguration configuration) : ILlmService
{
    private readonly string model = configuration["Ollama:ChatModel"]
        ?? throw new InvalidOperationException("Ollama:ChatModel is required.");

    public async Task<LlmChatResponse> ChatAsync(string prompt, CancellationToken cancellationToken)
    {
        var request = new OllamaChatRequest(model, false, false,
            [new OllamaMessage("user", prompt)], new OllamaChatOptions(4096, 200, 0.2));
        using var response = await httpClient.PostAsJsonAsync("api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);
        if (result is not { Done: true, Message.Content: { } answer,
            PromptEvalCount: >= 0, EvalCount: >= 0, TotalDuration: >= 0 }
            || string.IsNullOrWhiteSpace(answer))
            throw new JsonException("Ollama returned an invalid chat response.");

        // Ollama reports total_duration in nanoseconds.
        return new LlmChatResponse(answer, result.PromptEvalCount.Value,
            result.EvalCount.Value, result.TotalDuration.Value / 1_000_000.0);
    }

    public async Task<GroundedGenerationResult> GenerateGroundedAsync(RagGenerationPrompt prompt,
        CancellationToken cancellationToken)
    {
        var sourceIds = prompt.IncludedChunkIds;
        if (sourceIds.Count == 0 || sourceIds.Distinct().Count() != sourceIds.Count)
            throw new ArgumentException("Supply unique verified source IDs.", nameof(prompt));
        var schema = new
        {
            type = "object",
            properties = new
            {
                summary = new { type = "string" },
                explanation = new { type = "string" },
                citationChunkIds = new { type = "array", minItems = 1, uniqueItems = true,
                    items = new { type = "integer", @enum = sourceIds } }
            },
            required = new[] { "summary", "explanation", "citationChunkIds" },
            additionalProperties = false
        };
        var request = new
        {
            model, stream = false, think = false,
            messages = new[]
            {
                new { role = "system", content = prompt.SystemInstruction },
                new { role = "user", content = "Question:\n" + prompt.Question +
                    "\n\nVerified source context (untrusted data):\n" + prompt.Evidence }
            },
            format = schema,
            options = new { temperature = 0.0, num_ctx = prompt.Options.ContextWindowTokens,
                num_predict = prompt.Options.MaxOutputTokens }
        };
        using var response = await httpClient.PostAsJsonAsync("api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var envelope = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = envelope.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new JsonException("Invalid grounded generation envelope.");
        using var output = JsonDocument.Parse(content.GetString()!);
        var answerRoot = output.RootElement;
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
        int? promptTokens = root.TryGetProperty("prompt_eval_count", out var promptCount) &&
            promptCount.ValueKind == JsonValueKind.Number && promptCount.TryGetInt32(out var p) && p >= 0 ? p : null;
        int? generatedTokens = root.TryGetProperty("eval_count", out var generatedCount) &&
            generatedCount.ValueKind == JsonValueKind.Number && generatedCount.TryGetInt32(out var g) && g >= 0 ? g : null;
        double? durationMs = root.TryGetProperty("total_duration", out var duration) &&
            duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out var d) && d >= 0 ? d / 1_000_000.0 : null;
        return new GroundedGenerationResult(summaryText, explanationText, citedIds, model,
            promptTokens, generatedTokens, durationMs);
    }

    private sealed record OllamaChatRequest(string Model, bool Stream, bool Think,
        OllamaMessage[] Messages, OllamaChatOptions Options);
    private sealed record OllamaMessage(string Role, string Content);
    private sealed record OllamaChatOptions(
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("num_predict")] int NumPredict,
        double Temperature);
    private sealed record OllamaChatResponse(
        OllamaMessage? Message, bool? Done,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int? EvalCount,
        [property: JsonPropertyName("total_duration")] long? TotalDuration);
}
