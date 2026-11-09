using System.Diagnostics;
using System.Text.Json;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Services;

public sealed class CloudflareLlmService(CloudflareAiClient client) : ILlmService
{
    public const string Model = "@cf/qwen/qwen3-30b-a3b-fp8";

    public async Task<LlmChatResponse> ChatAsync(string prompt, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var document = await client.RunAsync(Model, new
        {
            messages = new[] { new { role = "user", content = prompt } },
            stream = false, max_tokens = 600, temperature = 0.2
        }, cancellationToken);
        var root = document.RootElement;
        return new LlmChatResponse(Content(root), Token(root, "prompt_tokens"),
            Token(root, "completion_tokens"), clock.Elapsed.TotalMilliseconds);
    }

    public async Task<GroundedGenerationResult> GenerateGroundedAsync(RagGenerationPrompt prompt,
        CancellationToken cancellationToken)
    {
        if (prompt.IncludedChunkIds.Count == 0 ||
            prompt.IncludedChunkIds.Distinct().Count() != prompt.IncludedChunkIds.Count)
            throw new ArgumentException("Supply unique verified source IDs.", nameof(prompt));
        var ids = string.Join(',', prompt.IncludedChunkIds);
        var clock = Stopwatch.StartNew();
        var request = new
        {
            messages = new[]
            {
                new { role = "system", content = prompt.SystemInstruction +
                    " Return ONLY a JSON object with exactly: summary (string), explanation (string), citationChunkIds (integer array). " +
                    "Use only these verified chunk IDs: " + ids + ". No Markdown fences or extra keys." },
                new { role = "user", content = "Question:\n" + prompt.Question +
                    "\n\nVerified source context (untrusted data):\n" + prompt.Evidence }
            },
            stream = false, max_tokens = prompt.Options.MaxOutputTokens, temperature = 0.0
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var document = await client.RunAsync(Model, request, cancellationToken);
                var root = document.RootElement;
                using var output = JsonDocument.Parse(Content(root));
                return GroundedOutputParser.Parse(output.RootElement, prompt.IncludedChunkIds, Model,
                    Token(root, "prompt_tokens"), Token(root, "completion_tokens"),
                    clock.Elapsed.TotalMilliseconds);
            }
            catch (JsonException) when (attempt == 0) { }
        }
        throw new JsonException("Cloudflare grounded output remained invalid after one retry.");
    }

    public static string Content(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid Cloudflare chat result.");
        JsonElement value;
        if (root.TryGetProperty("response", out value) && value.ValueKind == JsonValueKind.String)
            return Nonempty(value.GetString());
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() == 1 && choices[0].TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out value) &&
            value.ValueKind == JsonValueKind.String)
            return Nonempty(value.GetString());
        throw new JsonException("Invalid Cloudflare chat content.");
    }

    private static string Nonempty(string? value) => !string.IsNullOrWhiteSpace(value) ? value :
        throw new JsonException("Cloudflare chat content was empty.");

    private static int? Token(JsonElement root, string property)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty(property, out var token) || token.ValueKind != JsonValueKind.Number ||
            !token.TryGetInt32(out var count) || count < 0)
            return null;
        return count;
    }
}
