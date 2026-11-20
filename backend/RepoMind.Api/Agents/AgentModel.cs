using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoMind.Api.Services;

namespace RepoMind.Api.Agents;

// SchemaDescription repeats the schema in words for providers without schema enforcement.
// AllowReasoning marks judgment-heavy steps (review, explanation) where a reasoning
// model should think first; short decisions such as planning leave it off.
public sealed record AgentModelRequest(string SystemInstruction, string UserContent, object Schema,
    string SchemaDescription, int MaxOutputTokens, int ContextWindowTokens, bool AllowReasoning = false);

public sealed record AgentModelResult(JsonElement Output, string Model, int? PromptTokens,
    int? GeneratedTokens);

// Agents need one capability from a model: a JSON object matching a schema.
// Callers validate every field; schema enforcement is a convenience, not trust.
public interface IAgentModel
{
    string ModelName { get; }
    Task<AgentModelResult> CompleteJsonAsync(AgentModelRequest request, CancellationToken cancellationToken);
}

public sealed class OllamaAgentModel(HttpClient httpClient, IConfiguration configuration) : IAgentModel
{
    public string ModelName { get; } = configuration["Ollama:ChatModel"]
        ?? throw new InvalidOperationException("Ollama:ChatModel is required.");

    public async Task<AgentModelResult> CompleteJsonAsync(AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model = ModelName, stream = false, think = false,
            messages = new[]
            {
                new { role = "system", content = request.SystemInstruction },
                new { role = "user", content = request.UserContent }
            },
            format = request.Schema,
            options = new { temperature = 0.0, num_ctx = request.ContextWindowTokens,
                num_predict = request.MaxOutputTokens }
        };
        using var response = await httpClient.PostAsJsonAsync("api/chat", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var envelope = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = envelope.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(content.GetString()))
            throw new JsonException("Invalid Ollama agent response envelope.");
        using var output = JsonDocument.Parse(content.GetString()!);
        return new AgentModelResult(output.RootElement.Clone(), ModelName,
            Count(root, "prompt_eval_count"), Count(root, "eval_count"));
    }

    private static int? Count(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var count) && count >= 0 ? count : null;
}

public sealed class CloudflareAgentModel(CloudflareAiClient client) : IAgentModel
{
    // Reasoning models on Workers AI think before answering, and reasoning tokens count
    // against max_tokens. Measured live with Qwen3: a 200-token planner budget was spent
    // entirely on hidden reasoning (finish_reason "length", empty content); Qwen3's
    // "/no_think" switch then needed 53 tokens. Without reasoning the reviewer answered
    // "no issues" in 34 tokens, so judgment steps keep reasoning and get a budget large
    // enough for thinking plus the JSON answer. "/no_think" is Qwen3-specific; other
    // models (gpt-oss-20b: planner 1.8 s, review 6.0 s) rely on the token floors alone.
    public const string NoThink = " /no_think";
    public const int MinimumOutputTokens = 1_200;
    public const int MinimumReasoningOutputTokens = 4_000;

    public string ModelName => client.ChatModel;

    private bool SupportsNoThink => client.ChatModel.Contains("/qwen3", StringComparison.OrdinalIgnoreCase);

    public async Task<AgentModelResult> CompleteJsonAsync(AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            messages = new[]
            {
                new { role = "system", content = request.SystemInstruction +
                    " Return ONLY one JSON object: " + request.SchemaDescription +
                    " No Markdown fences, no extra keys." },
                new { role = "user", content = request.UserContent +
                    (request.AllowReasoning || !SupportsNoThink ? "" : NoThink) }
            },
            stream = false, temperature = 0.0,
            max_tokens = Math.Max(request.MaxOutputTokens,
                request.AllowReasoning ? MinimumReasoningOutputTokens : MinimumOutputTokens)
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var document = await client.RunAsync(ModelName, body, cancellationToken);
                var root = document.RootElement;
                using var output = JsonDocument.Parse(StripWrapper(CloudflareLlmService.Content(root)));
                if (output.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Cloudflare agent output was not a JSON object.");
                return new AgentModelResult(output.RootElement.Clone(), ModelName,
                    Usage(root, "prompt_tokens"), Usage(root, "completion_tokens"));
            }
            catch (JsonException) when (attempt == 0) { }
        }
        throw new JsonException("Cloudflare agent output remained invalid after one retry.");
    }

    // Qwen sometimes wraps JSON in a reasoning block or a Markdown fence.
    public static string StripWrapper(string content)
    {
        var text = Regex.Replace(content, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        var fence = Regex.Match(text, @"^```(?:json)?\s*(?<body>.*?)\s*```$", RegexOptions.Singleline);
        return fence.Success ? fence.Groups["body"].Value : text;
    }

    private static int? Usage(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var count) && count >= 0 ? count : null;
}
