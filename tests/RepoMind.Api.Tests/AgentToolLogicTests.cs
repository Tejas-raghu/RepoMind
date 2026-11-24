using System.Text.Json;
using RepoMind.Api.Agents;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Tests;

public sealed class AgentToolLogicTests
{
    private static SourceLine[] Lines(string text) => text.Split('\n')
        .Select((line, index) => new SourceLine(index + 1, line, "covered")).ToArray();

    [Fact]
    public void Outline_finds_typescript_imports_and_declarations_with_line_numbers()
    {
        var symbols = CodeOutline.Extract("src/api.ts", Lines(
            "import { ref } from 'vue'\n" +
            "// function commented() {}\n" +
            "export interface Task { id: number }\n" +
            "export async function loadTasks() {\n}\n" +
            "export const isOverdue = (task: Task): boolean => true\n" +
            "export const apiClient = { get }"));
        Assert.Contains(new CodeSymbol("import", "vue", 1), symbols);
        Assert.Contains(new CodeSymbol("interface", "Task", 3), symbols);
        Assert.Contains(new CodeSymbol("function", "loadTasks", 4), symbols);
        Assert.Contains(new CodeSymbol("function", "isOverdue", 6), symbols);
        Assert.Contains(new CodeSymbol("export", "apiClient", 7), symbols);
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "commented");
    }

    [Fact]
    public void Outline_finds_csharp_usings_attributes_types_and_methods()
    {
        var symbols = CodeOutline.Extract("server/AuthController.cs", Lines(
            "using Microsoft.AspNetCore.Mvc;\n" +
            "public sealed class AuthController : ControllerBase\n{\n" +
            "    [HttpPost(\"login\")]\n" +
            "    public async Task<IActionResult> Login(LoginRequest request)\n    {\n" +
            "        return Ok(Validate(request));\n    }\n}"));
        Assert.Contains(new CodeSymbol("import", "Microsoft.AspNetCore.Mvc", 1), symbols);
        Assert.Contains(new CodeSymbol("class", "AuthController", 2), symbols);
        Assert.Contains(new CodeSymbol("attribute", "HttpPost(\"login\")", 4), symbols);
        Assert.Contains(new CodeSymbol("method", "Login", 5), symbols);
        Assert.Empty(CodeOutline.Extract("README.md", Lines("# function notCode()")));
    }

    [Fact]
    public void Static_rules_cite_real_lines_and_never_echo_secret_values()
    {
        var lines = Lines(
            "localStorage.setItem('authToken', token)\n" +
            "element.innerHTML = html\n" +
            "const apiKey = 'abcd1234secretvalue'\n" +
            "try { run() } catch (e) {}\n" +
            "const fine = 1\n" +
            $"const stripe = '{SecretRedactor.Marker}'");
        var findings = StaticReviewRules.Check("src/app.ts", lines);
        Assert.Contains(findings, f => f.RuleId == "token-in-local-storage" && f.Citations[0].StartLine == 1);
        Assert.Contains(findings, f => f.RuleId == "raw-html" && f.Citations[0].StartLine == 2);
        Assert.Contains(findings, f => f.RuleId == "hardcoded-secret" && f.Citations[0].StartLine == 3);
        Assert.Contains(findings, f => f.RuleId == "empty-catch" && f.Citations[0].StartLine == 4);
        Assert.Contains(findings, f => f.RuleId == "redacted-secret" && f.Citations[0].StartLine == 6);
        Assert.DoesNotContain(findings, f => f.Citations[0].StartLine == 5);
        Assert.All(findings, f => Assert.Equal("rule", f.Origin));
        Assert.All(findings, f => Assert.DoesNotContain("abcd1234secretvalue", f.Title + f.Detail));
    }

    [Fact]
    public void Static_rules_respect_language_and_per_rule_caps()
    {
        var csharp = StaticReviewRules.Check("Program.cs", Lines(
            "options.TokenValidationParameters = new() { ValidateLifetime = false };\n" +
            "builder.AllowAnyOrigin();\n" +
            "var sql = $\"SELECT * FROM users WHERE id = {id}\";"));
        Assert.Contains(csharp, f => f.RuleId == "jwt-validation-disabled");
        Assert.Contains(csharp, f => f.RuleId == "cors-any-origin");
        Assert.Contains(csharp, f => f.RuleId == "sql-concatenation");
        Assert.DoesNotContain(csharp, f => f.RuleId == "raw-html");

        var many = StaticReviewRules.Check("a.ts", Lines(string.Join('\n', Enumerable.Repeat("// TODO later", 10))));
        Assert.Equal(StaticReviewRules.MaxFindingsPerRule, many.Count);
    }

    [Fact]
    public void Planner_output_must_choose_allowed_paths()
    {
        string[] allowed = ["src/a.ts", "src/b.ts"];
        var plan = RetrievalAgent.ParsePlan(Parse("""{"reason":"r","relevant":true,"paths":["src/b.ts"]}"""), allowed);
        Assert.Equal(new[] { "src/b.ts" }, plan.Paths);
        Assert.Equal("model", plan.Method);
        Assert.False(RetrievalAgent.ParsePlan(Parse("""{"reason":"r","relevant":false,"paths":["src/a.ts"]}"""), allowed).Relevant);
        Assert.Throws<JsonException>(() => RetrievalAgent.ParsePlan(Parse("""{"reason":"r","relevant":true,"paths":["../secret.ts"]}"""), allowed));
        Assert.Throws<JsonException>(() => RetrievalAgent.ParsePlan(Parse("""{"reason":"r","relevant":true,"paths":[]}"""), allowed));
        // Duplicates and unknown extras are ignored; only allowed paths survive.
        Assert.Equal(new[] { "src/a.ts" }, RetrievalAgent.ParsePlan(
            Parse("""{"reason":"r","relevant":true,"paths":["src/a.ts","src/a.ts","../x.ts"]}"""), allowed).Paths);
        Assert.Throws<JsonException>(() => RetrievalAgent.ParsePlan(Parse("""{"relevant":"yes","paths":[]}"""), allowed));
    }

    [Fact]
    public void Planner_path_without_the_repository_folder_maps_to_one_allowed_path()
    {
        string[] allowed = ["App/src/services/apiClient.ts", "App/src/services/taskService.ts", "App/README.md"];
        var plan = RetrievalAgent.ParsePlan(Parse("""{"reason":"r","relevant":true,"paths":["src/services/apiClient.ts"]}"""), allowed);
        Assert.Equal(new[] { "App/src/services/apiClient.ts" }, plan.Paths);
        // A suffix must name exactly one allowed file; a bare folder or ambiguous name is not accepted.
        string[] twins = ["A/src/x.ts", "B/src/x.ts"];
        Assert.Throws<JsonException>(() => RetrievalAgent.ParsePlan(Parse("""{"reason":"r","relevant":true,"paths":["src/x.ts"]}"""), twins));
        Assert.Throws<JsonException>(() => RetrievalAgent.ParsePlan(Parse("""{"reason":"r","relevant":true,"paths":["/etc/passwd"]}"""), allowed));
    }

    [Fact]
    public void Fallback_plan_prefers_code_with_task_terms_and_abstains_without_them()
    {
        HybridSearchResult Hit(long id, string path, string content) => new(id, path, 1, 5, content, 0.5, 1, null, 0.01);
        var hits = new[]
        {
            Hit(1, "README.md", "Overdue tasks are shown in red."),
            Hit(2, "src/services/taskService.ts", "export function isOverdue(task) { return task.due < now }"),
            Hit(3, "src/map/markers.ts", "L.marker(position)")
        };
        var plan = RetrievalAgent.FallbackPlan("Explain how overdue tasks are identified", hits);
        Assert.True(plan.Relevant);
        Assert.Equal(new[] { "src/services/taskService.ts", "README.md" }, plan.Paths);
        Assert.False(RetrievalAgent.FallbackPlan("Explain the Stripe payment integration", hits).Relevant);

        // Singular stripping must not turn "redis" into a prefix of "redirect".
        var guard = new[] { Hit(4, "src/router/guards.ts", "return { name: 'login', query: { redirect: to.fullPath } }") };
        Assert.False(RetrievalAgent.FallbackPlan("Review the Redis caching layer for bugs", guard).Relevant);
        Assert.True(RetrievalAgent.FallbackPlan("Explain the routes", [Hit(5, "src/router.ts", "const route = 1")]).Relevant);

        // Only documentation matched: a README note is not evidence of an implementation.
        var docsOnly = RetrievalAgent.FallbackPlan("Where is payment processing implemented?",
            [Hit(6, "README.md", "Is there payment processing in this repository? (Expected: no supporting code.)")]);
        Assert.False(docsOnly.Relevant);
        Assert.Empty(docsOnly.Paths);
    }

    [Theory]
    [InlineData("Explain how overdue tasks are identified", "how overdue tasks are identified")]
    [InlineData("please review: the login flow", "the login flow")]
    [InlineData("Walk me through the router", "the router")]
    [InlineData("Reviewer comments in apiClient", "Reviewer comments in apiClient")]
    [InlineData("Explain", "Explain")]
    public void Search_query_drops_the_agent_directive(string task, string expected) =>
        Assert.Equal(expected, RetrievalAgent.SearchQuery(task));

    [Fact]
    public void Synthesis_keeps_only_findings_cited_inside_read_evidence()
    {
        var evidence = new[] { new AgentEvidence("E1", "src/a.ts", 10, 20, "code", "read_file") };
        var parsed = AgentSynthesis.Parse(Parse("""
            {"sufficient":true,"summary":"Explains a.","findings":[
              {"title":"Valid","detail":"Lines 12-14.","severity":"high","evidenceId":"E1","startLine":12,"endLine":14},
              {"title":"Outside","detail":"Line 25.","severity":"low","evidenceId":"E1","startLine":25,"endLine":25},
              {"title":"Unknown","detail":"E9.","severity":"low","evidenceId":"E9","startLine":12,"endLine":12},
              {"title":"Reversed","detail":"x","severity":"low","evidenceId":"E1","startLine":15,"endLine":12},
              {"title":"Bad severity","detail":"x","severity":"critical","evidenceId":"E1","startLine":12,"endLine":12},
              {"title":"","detail":"x","severity":"low","evidenceId":"E1","startLine":12,"endLine":12},
              {"title":"String line","detail":"x","severity":"low","evidenceId":"E1","startLine":"12","endLine":12}
            ]}
            """), evidence, withSeverity: true);
        var finding = Assert.Single(parsed.Findings);
        Assert.Equal("Valid", finding.Title);
        Assert.Equal(new AgentCitation("src/a.ts", 12, 14), finding.Citations[0]);
        Assert.Equal("model", finding.Origin);
        Assert.Equal(6, parsed.Rejected);
    }

    [Fact]
    public void Synthesis_rejects_structurally_invalid_output()
    {
        var evidence = new[] { new AgentEvidence("E1", "src/a.ts", 1, 2, "code", "read_file") };
        Assert.Throws<JsonException>(() => AgentSynthesis.Parse(Parse("""{"summary":"x","findings":[]}"""), evidence, false));
        Assert.Throws<JsonException>(() => AgentSynthesis.Parse(Parse("""{"sufficient":true,"summary":"  ","findings":[]}"""), evidence, false));
        Assert.Throws<JsonException>(() => AgentSynthesis.Parse(Parse("[]"), evidence, false));
        // An over-long summary is shortened, not rejected, so validated findings survive.
        var longSummary = new string('x', AgentSynthesis.MaxSummaryLength + 1);
        var shortened = AgentSynthesis.Parse(
            Parse($$"""{"sufficient":true,"summary":"{{longSummary}}","findings":[]}"""), evidence, false);
        Assert.Equal(AgentSynthesis.MaxSummaryLength, shortened.Summary.Length);
        Assert.EndsWith("…", shortened.Summary);
    }

    [Fact]
    public void Evidence_blocks_number_original_lines_for_citation()
    {
        var text = AgentSynthesis.EvidenceBlocks([new AgentEvidence("E1", "src/a.ts", 41, 42, "first\nsecond", "read_file")]);
        Assert.Contains("[E1] src/a.ts lines 41-42", text);
        Assert.Contains("41| first\n42| second", text);
    }

    [Fact]
    public void Cloudflare_agent_output_wrappers_are_removed()
    {
        Assert.Equal("{\"a\":1}", CloudflareAgentModel.StripWrapper("<think>plan</think>\n```json\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", CloudflareAgentModel.StripWrapper(" {\"a\":1} "));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
