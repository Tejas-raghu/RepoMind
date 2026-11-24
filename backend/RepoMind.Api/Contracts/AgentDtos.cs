namespace RepoMind.Api.Contracts;

public sealed record AgentRunRequest(string? Task)
{
    public const int MaxTaskLength = 500;

    public Dictionary<string, string[]> Validate(long repositoryId)
    {
        var errors = new Dictionary<string, string[]>();
        if (repositoryId <= 0)
            errors["repositoryId"] = ["Repository ID must be positive."];
        if (string.IsNullOrWhiteSpace(Task))
            errors["task"] = ["Task must not be empty."];
        else if (Task.Length > MaxTaskLength)
            errors["task"] = [$"Task must be at most {MaxTaskLength} characters."];
        return errors;
    }
}

// Paths and line ranges always come from stored chunks, never from model text.
public sealed record AgentCitation(string FilePath, int StartLine, int EndLine);

// Origin is "model" (validated LLM output) or "rule" (deterministic tool output).
public sealed record AgentFinding(string Title, string Detail, string? Severity, string Origin,
    IReadOnlyList<AgentCitation> Citations)
{
    public string? RuleId { get; init; }
}

public sealed record AgentEvidence(string Id, string FilePath, int StartLine, int EndLine,
    string Content, string Source);

public sealed record AgentPlanStep(int Step, string Agent, string Action, string Description);

// Trace details hold names, counts, ranges and statuses only; never source text.
public sealed record AgentTraceStep(int Step, string Agent, string Action, string Status,
    double DurationMs, string Detail);

public sealed record AgentRunResponse(long RepositoryId, string Task, string Route, string Status,
    string Summary, IReadOnlyList<AgentFinding> Findings, IReadOnlyList<AgentEvidence> Evidence,
    IReadOnlyList<AgentPlanStep> Plan, IReadOnlyList<AgentTraceStep> Trace)
{
    public long? RunId { get; init; }
    public string RouteReason { get; init; } = "";
    public double DurationMs { get; init; }
    public string? Model { get; init; }
    public RagAnswerResponse? Answer { get; init; }
    // Parts of the task the gathered code does not establish (shown as "Not established").
    public IReadOnlyList<string> Gaps { get; init; } = [];
    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed record AgentRunSummary(long RunId, string Task, string Route, string Status,
    string Summary, double DurationMs, DateTimeOffset CreatedAt);
