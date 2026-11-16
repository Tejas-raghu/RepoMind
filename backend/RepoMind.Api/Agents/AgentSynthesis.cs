using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Agents;

public sealed record ParsedFindings(bool Sufficient, string Summary, IReadOnlyList<AgentFinding> Findings,
    int Rejected);

public sealed record SpecialistResult(string Status, string Summary, IReadOnlyList<AgentFinding> Findings);

// Shared by the Code Analysis and Review agents. The model sees numbered lines
// and cites an evidence ID plus a line range. Each citation is checked against
// the evidence the tools actually read, and the response path comes from that
// evidence record, never from model text. Invalid findings are dropped and
// counted in the trace instead of being shown.
public static class AgentSynthesis
{
    public const int MaxFindings = 5;
    // Tight caps keep a five-finding answer within the output budget of a CPU-bound model.
    public const int MaxSummaryLength = 500;
    public const int MaxTitleLength = 120;
    public const int MaxDetailLength = 400;
    private static readonly string[] Severities = ["low", "medium", "high"];

    public static string EvidenceBlocks(IReadOnlyList<AgentEvidence> evidence)
    {
        var builder = new StringBuilder();
        foreach (var item in evidence)
        {
            builder.Append($"[{item.Id}] {item.FilePath} lines {item.StartLine}-{item.EndLine}\n");
            var lines = item.Content.Split('\n');
            for (var index = 0; index < lines.Length; index++)
                builder.Append(item.StartLine + index).Append("| ").Append(lines[index]).Append('\n');
            builder.Append($"[end {item.Id}]\n");
        }
        return builder.ToString();
    }

    public static AgentModelRequest Request(string system, string user, IReadOnlyList<AgentEvidence> evidence,
        bool withSeverity, AgentOptions options)
    {
        var ids = evidence.Select(item => item.Id).ToArray();
        object finding = withSeverity
            ? new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", maxLength = MaxTitleLength },
                    detail = new { type = "string", maxLength = MaxDetailLength },
                    severity = new { type = "string", @enum = Severities },
                    evidenceId = new { type = "string", @enum = ids },
                    startLine = new { type = "integer" },
                    endLine = new { type = "integer" }
                },
                required = new[] { "title", "detail", "severity", "evidenceId", "startLine", "endLine" },
                additionalProperties = false
            }
            : new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", maxLength = MaxTitleLength },
                    detail = new { type = "string", maxLength = MaxDetailLength },
                    evidenceId = new { type = "string", @enum = ids },
                    startLine = new { type = "integer" },
                    endLine = new { type = "integer" }
                },
                required = new[] { "title", "detail", "evidenceId", "startLine", "endLine" },
                additionalProperties = false
            };
        var schema = new
        {
            type = "object",
            // Property order is generation order: the model writes its explanation
            // before committing to the sufficiency verdict.
            properties = new
            {
                summary = new { type = "string", maxLength = MaxSummaryLength },
                findings = new { type = "array", maxItems = MaxFindings, items = finding },
                sufficient = new { type = "boolean" }
            },
            required = new[] { "summary", "findings", "sufficient" },
            additionalProperties = false
        };
        var description = "{\"summary\": string (max 500 chars), \"findings\": array of at most 5 " +
            "{\"title\": string, \"detail\": string, " + (withSeverity ? "\"severity\": \"low\"|\"medium\"|\"high\", " : "") +
            "\"evidenceId\": one of " + string.Join('/', ids) + ", \"startLine\": integer, \"endLine\": integer}, \"sufficient\": boolean}";
        return new AgentModelRequest(system, user, schema, description, options.MaxOutputTokens,
            options.ContextWindowTokens, AllowReasoning: true);
    }

    public static ParsedFindings Parse(JsonElement root, IReadOnlyList<AgentEvidence> evidence, bool withSeverity)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sufficient", out var sufficient) ||
            sufficient.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("summary", out var summaryValue) || summaryValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("findings", out var findingsValue) || findingsValue.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid agent synthesis output.");
        var summary = summaryValue.GetString()!.Trim();
        if (summary.Length is 0 or > MaxSummaryLength)
            throw new JsonException("Agent summary is empty or too long.");

        var byId = evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var findings = new List<AgentFinding>();
        var rejected = 0;
        foreach (var item in findingsValue.EnumerateArray())
        {
            if (findings.Count >= MaxFindings) { rejected++; continue; }
            if (TryFinding(item, byId, withSeverity) is { } finding) findings.Add(finding);
            else rejected++;
        }
        return new ParsedFindings(sufficient.GetBoolean(), summary, findings, rejected);
    }

    private static AgentFinding? TryFinding(JsonElement item, IReadOnlyDictionary<string, AgentEvidence> byId,
        bool withSeverity)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            Text(item, "title", MaxTitleLength) is not { } title ||
            Text(item, "detail", MaxDetailLength) is not { } detail ||
            !item.TryGetProperty("evidenceId", out var id) || id.ValueKind != JsonValueKind.String ||
            !byId.TryGetValue(id.GetString()!, out var evidence) ||
            !item.TryGetProperty("startLine", out var startValue) || startValue.ValueKind != JsonValueKind.Number ||
            !startValue.TryGetInt32(out var start) ||
            !item.TryGetProperty("endLine", out var endValue) || endValue.ValueKind != JsonValueKind.Number ||
            !endValue.TryGetInt32(out var end) ||
            start > end || start < evidence.StartLine || end > evidence.EndLine)
            return null;
        string? severity = null;
        if (withSeverity)
        {
            if (!item.TryGetProperty("severity", out var severityValue) ||
                severityValue.ValueKind != JsonValueKind.String ||
                !Severities.Contains(severityValue.GetString())) return null;
            severity = severityValue.GetString();
        }
        return new AgentFinding(title, detail, severity, "model", [new AgentCitation(evidence.FilePath, start, end)]);
    }

    private static string? Text(JsonElement item, string name, int maximum) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()!.Trim() is { Length: > 0 } text && text.Length <= maximum ? text : null;

    // Calls the model with its own timeout. Null means "model unavailable"; the
    // calling agent then falls back to its deterministic tool output.
    public static async Task<ParsedFindings?> RunAsync(AgentRunState state, string agent, IAgentModel model,
        AgentModelRequest request, IReadOnlyList<AgentEvidence> evidence, bool withSeverity,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(state.Options.ModelTimeoutSeconds));
        try
        {
            var result = await model.CompleteJsonAsync(request, timeout.Token);
            state.Model = result.Model;
            var parsed = Parse(result.Output, evidence, withSeverity);
            state.Record(agent, "synthesize", "ok", clock,
                $"model {result.Model}; sufficient {parsed.Sufficient.ToString().ToLowerInvariant()}; " +
                $"{parsed.Findings.Count} valid findings, {parsed.Rejected} rejected; " +
                $"tokens in/out {result.PromptTokens?.ToString() ?? "n/a"}/{result.GeneratedTokens?.ToString() ?? "n/a"}");
            return parsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            state.Record(agent, "synthesize", "timeout", clock,
                $"model exceeded {state.Options.ModelTimeoutSeconds}s");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or CloudflareAiException)
        {
            // Parser messages describe structure and position only, never model or source text.
            state.Record(agent, "synthesize", "failed", clock, exception is JsonException
                ? "model output failed validation: " + Shorten(exception.Message)
                : "model provider unavailable");
        }
        return null;
    }

    private static string Shorten(string message) => message.Length <= 120 ? message : message[..120] + "...";
}
