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
