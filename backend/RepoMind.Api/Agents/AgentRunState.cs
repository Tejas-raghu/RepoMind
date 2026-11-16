using System.Diagnostics;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Agents;

public sealed record AgentOptions(int TimeoutSeconds, int ModelTimeoutSeconds, int PlannerTimeoutSeconds,
    bool ModelPlanning, int MaxToolCalls, int MaxEvidenceCharacters, int MaxLinesPerFile,
    int ContextWindowTokens, int MaxOutputTokens)
{
    public static AgentOptions From(IConfiguration configuration)
    {
        var options = new AgentOptions(
            configuration.GetValue<int?>("Agents:TimeoutSeconds") ?? 420,
            configuration.GetValue<int?>("Agents:ModelTimeoutSeconds") ?? 240,
            configuration.GetValue<int?>("Agents:PlannerTimeoutSeconds") ?? 60,
            configuration.GetValue<bool?>("Agents:ModelPlanning") ?? true,
            configuration.GetValue<int?>("Agents:MaxToolCalls") ?? 10,
            configuration.GetValue<int?>("Agents:MaxEvidenceCharacters") ?? 6_000,
            configuration.GetValue<int?>("Agents:MaxLinesPerFile") ?? 150,
            configuration.GetValue<int?>("Agents:ContextWindowTokens") ?? 6_144,
            configuration.GetValue<int?>("Agents:MaxOutputTokens") ?? 1_000);
        if (options.TimeoutSeconds is < 10 or > 900 ||
            options.ModelTimeoutSeconds is < 5 or > 600 ||
            options.PlannerTimeoutSeconds is < 5 or > 300 ||
            options.MaxToolCalls is < 1 or > 30 ||
            options.MaxEvidenceCharacters is < 1_000 or > 40_000 ||
            options.MaxLinesPerFile is < 10 or > 1_000 ||
            options.ContextWindowTokens is < 2_048 or > 262_144 ||
            options.MaxOutputTokens is < 100 or > 4_096)
            throw new InvalidOperationException("Invalid Agents configuration.");
        return options;
    }
}

// The shared, typed blackboard that agents hand to each other. Agents never exchange
// free-form chat: the Retrieval Agent writes evidence, specialists read it and
// write findings, and every step appends to the trace.
public sealed class AgentRunState(long repositoryId, string task, AgentOptions options)
{
    public long RepositoryId { get; } = repositoryId;
    public string Task { get; } = task;
    public AgentOptions Options { get; } = options;
    public IReadOnlyList<string> IndexedPaths { get; set; } = [];
    public List<AgentPlanStep> Plan { get; } = [];
    public List<AgentTraceStep> Trace { get; } = [];
    public List<AgentEvidence> Evidence { get; } = [];
    public List<AgentFinding> Findings { get; } = [];
    public List<string> TargetPaths { get; } = [];
    public Dictionary<string, IndexedFileContent> Files { get; } = new(StringComparer.Ordinal);
    public int ToolCalls { get; set; }
    public int EvidenceCharacters => Evidence.Sum(item => item.Content.Length);
    public string? Model { get; set; }
    public string? AbstainReason { get; set; }
    // True when the user named the files. Relevance is then given, so a model
    // cannot dismiss the requested file as "unrelated".
    public bool ExplicitTargets { get; set; }

    public void AddPlan(string agent, string action, string description) =>
        Plan.Add(new AgentPlanStep(Plan.Count + 1, agent, action, description));

    public void Record(string agent, string action, string status, Stopwatch? clock, string detail) =>
        Trace.Add(new AgentTraceStep(Trace.Count + 1, agent, action, status,
            Math.Round(clock?.Elapsed.TotalMilliseconds ?? 0, 1), detail));

    public AgentEvidence AddEvidence(string path, int startLine, int endLine, string content, string source)
    {
        var evidence = new AgentEvidence($"E{Evidence.Count + 1}", path, startLine, endLine, content, source);
        Evidence.Add(evidence);
        if (!TargetPaths.Contains(path)) TargetPaths.Add(path);
        return evidence;
    }
}
