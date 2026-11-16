using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RepoMind.Api.Agents;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

// Real PostgreSQL + pgvector, real tools, search, router and persistence.
// Only the model and embedding provider are deterministic fakes, so failure,
// timeout and invalid-output paths can be scripted.
public sealed class AgentOrchestrationIntegrationTests : IAsyncLifetime
{
    private const string ApiClient =
        "import { getToken } from './authService'\n" +
        "export const apiClient = {\n" +
        "  async get(url: string) {\n" +
        "    const headers = new Headers()\n" +
        "    headers.set('Authorization', `Bearer ${getToken()}`)\n" +
        "    localStorage.setItem('authToken', getToken())\n" +
        "    return fetch(url, { headers })\n" +
        "  }\n" +
        "}";
    private const string Guards =
        "import { router } from './router'\n" +
        "export function requireAuth() {\n" +
        "  router.beforeEach((to) => to.meta.public || isLoggedIn())\n" +
        "}";

    private readonly NpgsqlDataSource dataSource = TestDatabase.Create();
    private readonly List<long> repositories = [];
    private long repositoryId;
    private long otherRepositoryId;

    public async Task InitializeAsync()
    {
        repositoryId = await TestDatabase.AddRepositoryAsync(dataSource, "agents");
        otherRepositoryId = await TestDatabase.AddRepositoryAsync(dataSource, "agents-other");
        repositories.AddRange([repositoryId, otherRepositoryId]);
        await TestDatabase.AddChunkAsync(dataSource, repositoryId, "src/services/apiClient.ts", ApiClient, 1, 0.9f);
        await TestDatabase.AddChunkAsync(dataSource, repositoryId, "src/router/guards.ts", Guards, 1, 0.8f);
        await TestDatabase.AddChunkAsync(dataSource, repositoryId, "README.md", "# TaskTrail\nBearer tokens are sent.", 1, 0.95f);
        await TestDatabase.AddChunkAsync(dataSource, otherRepositoryId, "src/private.ts", "const privateBearer = 1", 1, 1f);
    }

    public async Task DisposeAsync()
    {
        await TestDatabase.CleanupAsync(dataSource, repositories);
        await dataSource.DisposeAsync();
    }

    [Fact]
    public async Task Review_with_explicit_target_combines_validated_model_and_rule_findings_and_persists_trace()
    {
        var model = new ScriptedModel(synthesis: _ => Json("""
            {"sufficient":true,"summary":"The client stores the token in localStorage.","findings":[
              {"title":"Token readable by scripts","detail":"localStorage exposes the token to XSS.","severity":"medium","evidenceId":"E1","startLine":6,"endLine":6},
              {"title":"Invented line","detail":"Outside evidence.","severity":"high","evidenceId":"E1","startLine":40,"endLine":41}]}
            """));
        var (supervisor, store) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Review apiClient.ts for security issues", CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal(("review", "completed"), (run.Route, run.Status));
        var evidence = Assert.Single(run.Evidence);
        Assert.Equal(("E1", "src/services/apiClient.ts", 1, 9), (evidence.Id, evidence.FilePath, evidence.StartLine, evidence.EndLine));
        Assert.Contains(run.Findings, f => f.Origin == "model" && f.Title == "Token readable by scripts" &&
            f.Citations[0] == new AgentCitation("src/services/apiClient.ts", 6, 6));
        Assert.Contains(run.Findings, f => f.Origin == "rule" && f.RuleId == "token-in-local-storage");
        Assert.DoesNotContain(run.Findings, f => f.Title == "Invented line");
        Assert.Contains(run.Trace, t => t.Action == "synthesize" && t.Detail.Contains("1 rejected"));
        Assert.Equal(["route", "read_file", "static_checks", "synthesize"], run.Plan.Select(p => p.Action));
        Assert.Equal(["list_files", "route", "read_file", "handoff", "static_checks", "synthesize"],
            run.Trace.Select(t => t.Action));
        Assert.All(run.Trace, t => Assert.DoesNotContain("localStorage.setItem", t.Detail));
        Assert.Equal(0, model.PlannerCalls);

        Assert.NotNull(run.RunId);
        var stored = await store.GetAsync(repositoryId, run.RunId.Value, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(run.Findings.Count, stored.Findings.Count);
        Assert.Equal(run.Trace.Count, stored.Trace.Count);
        Assert.Null(await store.GetAsync(otherRepositoryId, run.RunId.Value, CancellationToken.None));
        Assert.Contains(await store.ListAsync(repositoryId, 5, CancellationToken.None), r => r.RunId == run.RunId);
        Assert.Empty(await store.ListAsync(otherRepositoryId, 5, CancellationToken.None));
    }

    [Fact]
    public async Task Analysis_without_target_uses_model_plan_limited_to_this_repository()
    {
        var model = new ScriptedModel(
            planner: request =>
            {
                var paths = ((JsonElement)JsonSerializer.SerializeToElement(request.Schema))
                    .GetProperty("properties").GetProperty("paths").GetProperty("items").GetProperty("enum")
                    .EnumerateArray().Select(x => x.GetString()).ToArray();
                Assert.DoesNotContain("src/private.ts", paths);
                return Json("""{"reason":"guards","relevant":true,"paths":["src/router/guards.ts"]}""");
            },
            synthesis: _ => Json("""
                {"sufficient":true,"summary":"requireAuth installs a router guard.","findings":[
                  {"title":"Global guard","detail":"beforeEach checks login.","evidenceId":"E1","startLine":3,"endLine":3}]}
                """));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Explain how routes are protected", CancellationToken.None);

        Assert.Equal(("analysis", "completed"), (run!.Route, run.Status));
        Assert.Equal(1, model.PlannerCalls);
        Assert.Equal("src/router/guards.ts", Assert.Single(run.Evidence).FilePath);
        Assert.Contains(run.Trace, t => t.Action == "plan_targets" && t.Status == "ok" && t.Detail.StartsWith("model:"));
        Assert.Contains(run.Trace, t => t.Action == "outline_file" && t.Status == "ok");
        Assert.Equal("Global guard", Assert.Single(run.Findings).Title);
    }

    [Fact]
    public async Task Invalid_planner_output_falls_back_to_rules_and_model_failure_returns_partial_outline()
    {
        var model = new ScriptedModel(
            planner: _ => Json("""{"reason":"x","relevant":true,"paths":["../../etc/passwd"]}"""),
            synthesis: _ => throw new HttpRequestException("model down"));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Explain how the Bearer header is attached", CancellationToken.None);

        Assert.Equal(("analysis", "partial"), (run!.Route, run.Status));
        Assert.Contains(run.Trace, t => t.Action == "plan_targets" && t.Status == "fallback");
        Assert.Contains(run.Trace, t => t.Action == "synthesize" && t.Status == "failed");
        Assert.Equal("src/services/apiClient.ts", run.Evidence[0].FilePath);
        Assert.All(run.Findings, f => Assert.Equal(("rule", "outline"), (f.Origin, f.RuleId)));
        Assert.Contains(run.Findings, f => f.Title == "export apiClient" && f.Citations[0].StartLine == 2);
    }

    [Fact]
    public async Task Review_model_timeout_returns_rule_findings_as_partial()
    {
        var model = new ScriptedModel(synthesis: _ => throw new TaskCanceledException("timeout"));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Review src/services/apiClient.ts", CancellationToken.None);

        Assert.Equal("partial", run!.Status);
        Assert.Contains(run.Trace, t => t.Action == "synthesize" && t.Status == "timeout");
        Assert.All(run.Findings, f => Assert.Equal("rule", f.Origin));
        Assert.Contains(run.Findings, f => f.RuleId == "token-in-local-storage");
    }

    [Fact]
    public async Task Unrelated_topic_abstains_before_synthesis()
    {
        var model = new ScriptedModel(planner: _ => Json("""{"reason":"none","relevant":false,"paths":[]}"""));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Explain the Stripe payment integration", CancellationToken.None);

        Assert.Equal(("analysis", "abstained"), (run!.Route, run.Status));
        Assert.Empty(run.Evidence);
        Assert.Empty(run.Findings);
        Assert.Equal(0, model.SynthesisCalls);
        Assert.Contains(run.Trace, t => t.Action == "handoff" && t.Status == "skipped");
    }

    [Fact]
    public async Task Model_cannot_veto_search_results_that_contain_task_terms()
    {
        var model = new ScriptedModel(
            planner: _ => Json("""{"reason":"none","relevant":false,"paths":[]}"""),
            synthesis: _ => Json("""
                {"summary":"The client sets the header.","findings":[
                  {"title":"Header","detail":"Line 5.","evidenceId":"E1","startLine":5,"endLine":5}],"sufficient":true}
                """));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Explain how the Bearer header is attached", CancellationToken.None);

        Assert.Equal("completed", run!.Status);
        Assert.Equal("src/services/apiClient.ts", run.Evidence[0].FilePath);
        Assert.Contains(run.Trace, t => t.Action == "plan_targets" && t.Detail.StartsWith("model judged the results unrelated"));
    }

    [Fact]
    public async Task Documentation_mentions_cannot_override_an_unrelated_verdict()
    {
        await TestDatabase.AddChunkAsync(dataSource, repositoryId, "docs/notes.md", "Payments are not supported in this app.");
        var model = new ScriptedModel(planner: _ => Json("""{"reason":"none","relevant":false,"paths":[]}"""));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Explain the payments integration", CancellationToken.None);

        Assert.Equal("abstained", run!.Status);
        Assert.Equal(0, model.SynthesisCalls);
    }

    [Fact]
    public async Task Analysis_insufficiency_abstains_even_when_the_model_describes_the_code()
    {
        var model = new ScriptedModel(
            planner: _ => Json("""{"reason":"guards","relevant":true,"paths":["src/router/guards.ts"]}"""),
            synthesis: _ => Json("""
                {"summary":"This is about routing, not payments.","findings":[
                  {"title":"Router guard","detail":"Line 3.","evidenceId":"E1","startLine":3,"endLine":3}],"sufficient":false}
                """));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Explain how routes are protected", CancellationToken.None);

        Assert.Equal("abstained", run!.Status);
        Assert.Empty(run.Findings);
    }

    [Fact]
    public async Task Validated_findings_survive_a_contradictory_insufficiency_flag()
    {
        var model = new ScriptedModel(
            planner: _ => Json("""{"reason":"auth","relevant":true,"paths":["src/services/apiClient.ts"]}"""),
            synthesis: _ => Json("""
                {"summary":"Token handling is risky.","findings":[
                  {"title":"Token in storage","detail":"Line 6.","severity":"medium","evidenceId":"E1","startLine":6,"endLine":6}],"sufficient":false}
                """));
        var (supervisor, _) = Create(model);

        var run = await supervisor.RunAsync(repositoryId, "Review the token handling for security risks", CancellationToken.None);

        Assert.Equal("completed", run!.Status);
        Assert.Equal("Token handling is risky.", run.Summary);
        Assert.Contains(run.Findings, f => f.Origin == "model" && f.Title == "Token in storage");
    }

    [Fact]
    public async Task Model_insufficiency_abstains_for_planned_files_but_not_for_named_files()
    {
        var model = new ScriptedModel(
            planner: _ => Json("""{"reason":"guards","relevant":true,"paths":["src/router/guards.ts"]}"""),
            synthesis: _ => Json("""{"sufficient":false,"summary":"Not related.","findings":[]}"""));
        var (supervisor, _) = Create(model);

        var planned = await supervisor.RunAsync(repositoryId, "Explain how routes are protected", CancellationToken.None);
        Assert.Equal("abstained", planned!.Status);
        Assert.Empty(planned.Findings);

        // A file the user named is in scope by definition; the outline fallback is used instead.
        var named = await supervisor.RunAsync(repositoryId, "Explain guards.ts", CancellationToken.None);
        Assert.Equal("partial", named!.Status);
        Assert.Contains(named.Findings, f => f.Title == "function requireAuth");

        var review = await supervisor.RunAsync(repositoryId, "Review guards.ts", CancellationToken.None);
        Assert.Equal("completed", review!.Status);
        Assert.StartsWith("The review model reported no issues", review.Summary);
    }

    [Fact]
    public async Task Unsupported_task_uses_no_model_or_search_and_is_persisted()
    {
        var model = new ScriptedModel();
        var embeddings = new StubEmbeddings();
        var (supervisor, store) = Create(model, embeddings);

        var run = await supervisor.RunAsync(repositoryId, "Delete apiClient.ts", CancellationToken.None);

        Assert.Equal(("unsupported", "unsupported"), (run!.Route, run.Status));
        Assert.Contains("read-only", run.Summary);
        Assert.Equal(0, model.PlannerCalls + model.SynthesisCalls);
        Assert.Equal(0, embeddings.Calls);
        Assert.Equal(["list_files", "route"], run.Trace.Select(t => t.Action));
        Assert.NotNull(await store.GetAsync(repositoryId, run.RunId!.Value, CancellationToken.None));
    }

    [Theory]
    [InlineData("answered", "completed")]
    [InlineData("insufficient_evidence", "abstained")]
    [InlineData("generation_unavailable", "failed")]
    public async Task Question_route_delegates_to_verified_rag_flow(string ragStatus, string expected)
    {
        var sources = ragStatus == "answered"
            ? new[] { new RagAnswerSource(5, "src/services/apiClient.ts", 1, 9, ApiClient) }
            : [];
        var rag = new FakeRag(new RagAnswerResponse(ragStatus, "Answer [source:5]", sources, "qwen3:4b", 10, 5)
            { Summary = "The client sets the header." });
        var (supervisor, _) = Create(new ScriptedModel(), rag: rag);

        var run = await supervisor.RunAsync(repositoryId, "Where is the Bearer token added?", CancellationToken.None);

        Assert.Equal(("question", expected), (run!.Route, run.Status));
        Assert.Equal(ragStatus, run.Answer?.Status);
        Assert.Equal(sources.Length, run.Evidence.Count);
        Assert.Equal(ragStatus == "answered" ? 1 : 0, run.Findings.Count);
        Assert.Equal(3, rag.TopK);
    }

    [Fact]
    public async Task Search_failure_is_recorded_and_reported_as_failed()
    {
        var embeddings = new StubEmbeddings { Failure = new HttpRequestException("embedding down") };
        var (supervisor, _) = Create(new ScriptedModel(), embeddings);

        var run = await supervisor.RunAsync(repositoryId, "Explain how the Bearer header is attached", CancellationToken.None);

        Assert.Equal("failed", run!.Status);
        Assert.Contains(run.Trace, t => t.Action == "search_code" && t.Status == "failed" &&
            t.Detail.Contains("AI provider unavailable"));
    }

    [Fact]
    public async Task Tool_budget_and_evidence_budget_are_enforced()
    {
        var model = new ScriptedModel(synthesis: _ => Json("""{"sufficient":true,"summary":"ok","findings":[]}"""));
        var (supervisor, _) = Create(model, settings: new() { ["Agents:MaxToolCalls"] = "2" });

        var run = await supervisor.RunAsync(repositoryId, "Review apiClient.ts and guards.ts", CancellationToken.None);

        Assert.Single(run!.Evidence);
        Assert.Contains(run.Trace, t => t.Action == "read_file" && t.Status == "skipped" && t.Detail.Contains("budget"));

        var state = new AgentRunState(repositoryId, "x", AgentOptions.From(Config(new() { ["Agents:MaxEvidenceCharacters"] = "1000" })))
        { IndexedPaths = ["src/long.ts"] };
        var longFile = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"const value{i} = '{new string('x', 40)}'"));
        await TestDatabase.AddChunkAsync(dataSource, repositoryId, "src/long.ts", longFile);
        var evidence = await CreateToolbox(new StubEmbeddings()).ReadFileAsync(state, "test", "src/long.ts", 1, 100, CancellationToken.None);
        var block = Assert.Single(evidence!);
        Assert.True(block.Content.Length <= 1000);
        Assert.True(block.EndLine < 40);
        Assert.Contains("evidence budget reached", state.Trace[^1].Detail);
    }

    [Fact]
    public async Task Tools_reject_paths_outside_the_repository_index()
    {
        var state = new AgentRunState(repositoryId, "x", AgentOptions.From(Config(new())))
        { IndexedPaths = ["src/services/apiClient.ts"] };
        var toolbox = CreateToolbox(new StubEmbeddings());

        Assert.Null(await toolbox.ReadFileAsync(state, "test", "src/private.ts", 1, 10, CancellationToken.None));
        Assert.Null(await toolbox.ReadFileAsync(state, "test", "../src/services/apiClient.ts", 1, 10, CancellationToken.None));
        Assert.Null(await toolbox.StaticChecksAsync(state, "test", "C:/Windows/win.ini", CancellationToken.None));
        Assert.Equal(3, state.Trace.Count(t => t.Status == "rejected"));
        Assert.Empty(state.Evidence);
    }

    [Fact]
    public async Task Client_cancellation_stops_the_run_and_persists_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new ScriptedModel(synthesis: _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var (supervisor, store) = Create(model);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            supervisor.RunAsync(repositoryId, "Review apiClient.ts", cancellation.Token));

        Assert.Empty(await store.ListAsync(repositoryId, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_repository_returns_null_and_storage_failure_keeps_the_result()
    {
        var (supervisor, _) = Create(new ScriptedModel());
        Assert.Null(await supervisor.RunAsync(long.MaxValue, "Explain apiClient.ts", CancellationToken.None));

        var builder = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2");
        await using var unreachable = builder.Build();
        var (offline, _) = Create(new ScriptedModel(), store: new AgentRunStore(unreachable));
        var run = await offline.RunAsync(repositoryId, "Deploy to production", CancellationToken.None);
        Assert.Null(run!.RunId);
        Assert.Contains(run.Trace, t => t.Action == "persist_run" && t.Status == "failed");
    }

    private (AgentSupervisor Supervisor, AgentRunStore Store) Create(ScriptedModel model,
        StubEmbeddings? embeddings = null, IRagAnswerService? rag = null, AgentRunStore? store = null,
        Dictionary<string, string?>? settings = null)
    {
        var config = Config(settings ?? new());
        var toolbox = CreateToolbox(embeddings ?? new StubEmbeddings(), config);
        var runStore = store ?? new AgentRunStore(dataSource);
        var supervisor = new AgentSupervisor(new RepositorySourceService(dataSource), toolbox,
            new RetrievalAgent(toolbox, rag ?? new FakeRag(null), model, NullLogger<RetrievalAgent>.Instance),
            new CodeAnalysisAgent(toolbox, model), new ReviewAgent(toolbox, model), runStore, config,
            NullLogger<AgentSupervisor>.Instance);
        return (supervisor, new AgentRunStore(dataSource));
    }

    private AgentToolbox CreateToolbox(StubEmbeddings embeddings, IConfiguration? config = null)
    {
        config ??= Config(new());
        var search = new HybridSearchService(new RepositorySearchStore(dataSource, config), embeddings,
            new NoOpReranker(), config, new TestHostEnvironment(), NullLogger<HybridSearchService>.Instance);
        return new AgentToolbox(search, new RepositorySourceService(dataSource), NullLogger<AgentToolbox>.Instance);
    }

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class ScriptedModel(Func<AgentModelRequest, JsonElement>? planner = null,
        Func<AgentModelRequest, JsonElement>? synthesis = null) : IAgentModel
    {
        public int PlannerCalls { get; private set; }
        public int SynthesisCalls { get; private set; }
        public string ModelName => "fake-model";

        public Task<AgentModelResult> CompleteJsonAsync(AgentModelRequest request, CancellationToken cancellationToken)
        {
            var isPlanner = request.SystemInstruction.Contains("planning step");
            if (isPlanner) PlannerCalls++; else SynthesisCalls++;
            var handler = isPlanner ? planner : synthesis;
            if (handler is null) throw new InvalidOperationException("Unexpected model call.");
            return Task.FromResult(new AgentModelResult(handler(request), ModelName, 100, 20));
        }
    }

    private sealed class FakeRag(RagAnswerResponse? response) : IRagAnswerService
    {
        public int TopK { get; private set; }

        public Task<RagAnswerResponse?> AnswerAsync(long repositoryId, string query, int topK,
            CancellationToken cancellationToken)
        {
            TopK = topK;
            return response is null
                ? Task.FromException<RagAnswerResponse?>(new InvalidOperationException("Unexpected RAG call."))
                : Task.FromResult<RagAnswerResponse?>(response);
        }
    }
}
