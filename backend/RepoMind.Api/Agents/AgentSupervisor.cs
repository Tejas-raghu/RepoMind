using System.Diagnostics;
using Npgsql;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Agents;

// The Supervisor owns one run end to end:
//   1. route the task (rules) and write an explicit plan;
//   2. hand the shared state to the Retrieval Agent for evidence;
//   3. hand that evidence to the Code Analysis or Review Agent;
//   4. turn the outcome into a status, enforce the run deadline, persist the trace.
// Status values: completed, partial (deterministic fallback used, or only part of the task
// is established by the code; the rest is listed in Gaps), abstained
// (no relevant evidence), unsupported (refused by the router), failed.
public sealed class AgentSupervisor(RepositorySourceService sources, AgentToolbox tools,
    RetrievalAgent retrieval, CodeAnalysisAgent analysis, ReviewAgent review, AgentRunStore store,
    IConfiguration configuration, ILogger<AgentSupervisor> logger)
{
    public const string Name = "supervisor";
    private readonly AgentOptions options = AgentOptions.From(configuration);

    public async Task<AgentRunResponse?> RunAsync(long repositoryId, string task,
        CancellationToken cancellationToken)
    {
        if (!await sources.ExistsAsync(repositoryId, cancellationToken)) return null;
        var elapsed = Stopwatch.StartNew();
        var state = new AgentRunState(repositoryId, task, options);
        var decision = new RouteDecision(AgentRoute.Unsupported, "The run did not reach routing.", []);
        RagAnswerResponse? answer = null;
        SpecialistResult outcome;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            state.IndexedPaths = await tools.ListFilesAsync(state, Name, deadline.Token) ?? [];
            var clock = Stopwatch.StartNew();
            decision = AgentRouter.Route(task, state.IndexedPaths);
            state.Record(Name, "route", "ok", clock, $"{decision.Name}: {decision.Reason}");
            WritePlan(state, decision);
            outcome = decision.Route switch
            {
                AgentRoute.Unsupported => new SpecialistResult("unsupported", decision.Reason, []),
                AgentRoute.Question => await AnswerAsync(state, decision, a => answer = a, deadline.Token),
                _ => await RunSpecialistAsync(state, decision, deadline.Token)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            state.Record(Name, "deadline", "timeout", elapsed, $"run exceeded {options.TimeoutSeconds}s");
            outcome = state.Findings.Count > 0
                ? new SpecialistResult("partial", $"The run exceeded its {options.TimeoutSeconds}-second limit; partial results are shown.", state.Findings.ToArray())
                : new SpecialistResult("failed", $"The run exceeded its {options.TimeoutSeconds}-second limit before producing a result.", []);
        }

        var response = new AgentRunResponse(repositoryId, task, decision.Name, outcome.Status, outcome.Summary,
            outcome.Findings, state.Evidence.ToArray(), state.Plan.ToArray(), state.Trace.ToArray())
        {
            RouteReason = decision.Reason,
            Model = answer?.Model ?? state.Model,
            Answer = answer,
            Gaps = outcome.Gaps,
            DurationMs = Math.Round(elapsed.Elapsed.TotalMilliseconds, 1)
        };
        return await PersistAsync(response, state, cancellationToken);
    }

    private async Task<SpecialistResult> RunSpecialistAsync(AgentRunState state, RouteDecision decision,
        CancellationToken cancellationToken)
    {
        await retrieval.GatherAsync(state, decision.Route, decision.TargetPaths, cancellationToken);
        var clock = Stopwatch.StartNew();
        if (state.Evidence.Count == 0)
        {
            state.Record(Name, "handoff", "skipped", clock, "no evidence gathered; specialist not called");
            return state.AbstainReason is { } reason
                ? new SpecialistResult("abstained", reason, [])
                : new SpecialistResult("failed",
                    "No evidence could be gathered because a required tool failed. See the trace for details.", []);
        }
        var specialist = decision.Route == AgentRoute.Review ? ReviewAgent.Name : CodeAnalysisAgent.Name;
        state.Record(Name, "handoff", "ok", clock, $"{RetrievalAgent.Name} -> {specialist}: " +
            string.Join(", ", state.Evidence.Select(item => $"{item.Id} {item.FilePath}:{item.StartLine}-{item.EndLine}")));
        var result = decision.Route == AgentRoute.Review
            ? await review.RunAsync(state, cancellationToken)
            : await analysis.RunAsync(state, cancellationToken);
        state.Findings.AddRange(result.Findings);
        return result;
    }

    // The RAG flow answers only with direct evidence. When it finds none, the question may
    // still be partly answerable ("what happens when this request times out?": the request
    // and its error handling are in the code, the remote server is not). The Code Analysis
    // Agent then reads the code and reports full, partial (with gaps) or no coverage. Absent
    // topics still abstain there, so this never turns a refusal into a guess.
    private async Task<SpecialistResult> AnswerAsync(AgentRunState state, RouteDecision decision,
        Action<RagAnswerResponse?> keep, CancellationToken cancellationToken)
    {
        var answer = await retrieval.AnswerAsync(state, cancellationToken);
        keep(answer);
        var result = FromAnswer(answer);
        if (answer?.Status is not ("insufficient_evidence" or "uncertain")) return result;

        state.Record(Name, "handoff", "ok", Stopwatch.StartNew(),
            $"{RetrievalAgent.Name} -> {CodeAnalysisAgent.Name}: no direct evidence for a full answer ({answer.Status}); " +
            "checking the code for a partial answer");
        var analysis = await RunSpecialistAsync(state, decision with { Route = AgentRoute.Analysis }, cancellationToken);
        if (analysis.Status is not ("completed" or "partial") || analysis.Findings.Count == 0) return result;
        keep(null); // the shown result is the agent's, not the abstained RAG answer
        return analysis;
    }

    private static SpecialistResult FromAnswer(RagAnswerResponse? answer)
    {
        if (answer is null)
            return new SpecialistResult("failed",
                "The Retrieval Agent could not complete the RAG answer flow. See the trace for details.", []);
        // An answer with gaps is partial: the cited part is established, the rest is named.
        var status = answer.Status switch
        {
            "answered" => "completed",
            "insufficient_evidence" or "uncertain" or "insufficient_context" => "abstained",
            _ => "failed"
        };
        var findings = answer.Status == "answered"
            ? new[] { new AgentFinding("Grounded answer", answer.Summary ?? answer.Answer, null, "model",
                answer.Sources.Select(source => new AgentCitation(source.FilePath, source.StartLine, source.EndLine)).ToArray()) }
            : [];
        return new SpecialistResult(status, answer.Answer, findings);
    }

    private static void WritePlan(AgentRunState state, RouteDecision decision)
    {
        state.AddPlan(Name, "route", $"Classify the task as {decision.Name}.");
        switch (decision.Route)
        {
            case AgentRoute.Question:
                state.AddPlan(RetrievalAgent.Name, "rag_answer",
                    "Hybrid search, evidence verification, then a grounded answer with validated citations.");
                state.AddPlan(CodeAnalysisAgent.Name, "synthesize",
                    "Only if no direct evidence is found: read the related code and report what it does and does not establish.");
                return;
            case AgentRoute.Unsupported:
                return;
        }
        if (decision.TargetPaths.Count > 0)
            state.AddPlan(RetrievalAgent.Name, "read_file", $"Read {string.Join(", ", decision.TargetPaths)}.");
        else
        {
            state.AddPlan(RetrievalAgent.Name, "search_code", "Search the repository for code related to the task.");
            state.AddPlan(RetrievalAgent.Name, "plan_targets", state.Options.ModelPlanning
                ? "Ask the model which files to read; fall back to rules if it fails."
                : "Choose files to read with rules.");
            state.AddPlan(RetrievalAgent.Name, "read_file", "Read bounded windows of the chosen files.");
        }
        if (decision.Route == AgentRoute.Review)
        {
            state.AddPlan(ReviewAgent.Name, "static_checks", "Run deterministic review rules on each file.");
            state.AddPlan(ReviewAgent.Name, "synthesize", "Ask the model for line-cited review findings and validate them.");
        }
        else
        {
            state.AddPlan(CodeAnalysisAgent.Name, "outline_file", "Extract imports and declarations with line numbers.");
            state.AddPlan(CodeAnalysisAgent.Name, "synthesize", "Ask the model for a line-cited explanation and validate it.");
        }
    }

    // A storage failure must not discard a finished analysis; the response reports runId null.
    private async Task<AgentRunResponse> PersistAsync(AgentRunResponse response, AgentRunState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var (id, createdAt) = await store.SaveAsync(response, cancellationToken);
            return response with { RunId = id, CreatedAt = createdAt };
        }
        catch (NpgsqlException exception)
        {
            logger.LogWarning("Agent run could not be persisted: {Reason}", exception.Message);
            state.Record(Name, "persist_run", "failed", null,
                "trace not saved; check PostgreSQL and apply database/015_agent_runs.sql");
            return response with { Trace = state.Trace.ToArray() };
        }
    }
}
