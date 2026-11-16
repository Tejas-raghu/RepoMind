using RepoMind.Api.Agents;

namespace RepoMind.Api.Tests;

public sealed class AgentRouterTests
{
    private static readonly string[] Paths =
    [
        "TaskTrail/src/services/apiClient.ts", "TaskTrail/src/router/guards.ts",
        "TaskTrail/server/AuthController.cs", "a/index.ts", "b/index.ts"
    ];

    [Theory]
    [InlineData("Where is the Bearer token added to requests?", AgentRoute.Question)]
    [InlineData("How are protected routes secured?", AgentRoute.Question)]
    [InlineData("Explain how overdue tasks are identified", AgentRoute.Analysis)]
    [InlineData("What does apiClient.ts do?", AgentRoute.Analysis)]
    [InlineData("Summarize the dependencies of AuthController.cs", AgentRoute.Analysis)]
    [InlineData("Review src/router/guards.ts for security issues", AgentRoute.Review)]
    [InlineData("Are there potential bugs in the login flow?", AgentRoute.Review)]
    [InlineData("Audit the authentication code", AgentRoute.Review)]
    [InlineData("Delete the AuthController file", AgentRoute.Unsupported)]
    [InlineData("please deploy this repository to production", AgentRoute.Unsupported)]
    [InlineData("Can you refactor apiClient.ts?", AgentRoute.Unsupported)]
    [InlineData("Write unit tests for guards.ts", AgentRoute.Unsupported)]
    public void Tasks_route_to_the_expected_agent(string task, AgentRoute expected)
    {
        var decision = AgentRouter.Route(task, Paths);
        Assert.Equal(expected, decision.Route);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Unsupported_tasks_have_no_targets_and_explain_read_only_scope()
    {
        var decision = AgentRouter.Route("Run the AuthController.cs tests", Paths);
        Assert.Equal("unsupported", decision.Name);
        Assert.Empty(decision.TargetPaths);
        Assert.Contains("read-only", decision.Reason);
    }

    [Fact]
    public void File_mentions_resolve_only_to_unique_indexed_paths()
    {
        var (targets, notes) = AgentRouter.ResolveTargets(
            "Review guards.ts, TaskTrail/server/AuthController.cs, index.ts and missing.ts", Paths);
        Assert.Equal(["TaskTrail/src/router/guards.ts", "TaskTrail/server/AuthController.cs"], targets);
        Assert.Contains(notes, note => note.Contains("'index.ts' matches 2"));
        Assert.Contains(notes, note => note.Contains("'missing.ts' is not an indexed file"));
    }

    [Fact]
    public void Targets_are_capped_and_traversal_text_never_escapes_the_index()
    {
        var (targets, _) = AgentRouter.ResolveTargets(
            "Explain ../../etc/passwd.json apiClient.ts guards.ts AuthController.cs", Paths);
        Assert.Equal(AgentRouter.MaxTargets, targets.Count);
        Assert.All(targets, target => Assert.Contains(target, Paths));
    }
}
