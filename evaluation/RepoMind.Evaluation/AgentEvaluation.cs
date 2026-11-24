using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// Multi-agent evaluation. Each task is labeled with the route the Supervisor should
// choose and either the files a grounded result must cite or the expected
// non-answer status (abstained/unsupported). Citations are checked against the
// indexed line counts, and every run must be retrievable from the trace store.
static class AgentEvaluation
{
    public static async Task<int> RunAsync(Arguments options, CancellationToken token)
    {
        var fixture = JsonSerializer.Deserialize<AgentFixture>(await File.ReadAllTextAsync(options.FixturePath, token),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Agent fixture is empty.");
        var repositoryId = options.RepositoryId ?? throw new ArgumentException("Agent mode requires --repository-id.");
        if (fixture.Tasks.Length == 0 || fixture.Tasks.Select(t => t.Id).Distinct().Count() != fixture.Tasks.Length ||
            fixture.Tasks.Any(t => (t.ExpectedPaths is { Length: > 0 }) == (t.ExpectedStatus is not null)))
            throw new InvalidDataException("Each task needs unique ID and either expectedPaths or expectedStatus.");

        using var client = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        var lineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? preflight = null;
        try
        {
            var files = await client.GetFromJsonAsync<JsonElement>($"api/repositories/{repositoryId}/files", token);
            var paths = files.EnumerateArray().Select(f => f.GetProperty("path").GetString()!).Order(StringComparer.Ordinal).ToArray();
            if (!paths.SequenceEqual(fixture.IndexedPaths.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Indexed paths differ from the agent fixture.");
            foreach (var path in paths)
            {
                var content = await client.GetFromJsonAsync<JsonElement>(
                    $"api/repositories/{repositoryId}/files/content?path={Uri.EscapeDataString(path)}", token);
                lineCounts[path] = content.GetProperty("lines").GetArrayLength();
            }
            Console.WriteLine($"Verified agent fixture: repository {repositoryId}, {paths.Length} files.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            preflight = exception.Message;
            Console.Error.WriteLine("Fixture check failed: " + preflight);
        }

        var outcomes = new List<AgentOutcome>();
        if (preflight is null)
            foreach (var task in fixture.Tasks)
            {
                if (token.IsCancellationRequested) break;
                var outcome = new AgentOutcome(task);
                outcomes.Add(outcome);
                var timer = Stopwatch.StartNew();
                try
                {
                    using var response = await client.PostAsJsonAsync($"api/repositories/{repositoryId}/agents/run",
                        new { task = task.Task }, token);
                    outcome.Ms = timer.Elapsed.TotalMilliseconds;
                    if (!response.IsSuccessStatusCode) { outcome.Error = $"HTTP {(int)response.StatusCode}"; continue; }
                    var run = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                    Score(outcome, run, lineCounts);
                    if (run.GetProperty("runId").ValueKind == JsonValueKind.Number)
                    {
                        var stored = await client.GetFromJsonAsync<JsonElement>(
                            $"api/repositories/{repositoryId}/agents/runs/{run.GetProperty("runId").GetInt64()}", token);
                        outcome.Persisted = stored.GetProperty("status").GetString() == outcome.Status &&
                            stored.GetProperty("trace").GetArrayLength() == run.GetProperty("trace").GetArrayLength();
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { outcome.Ms = timer.Elapsed.TotalMilliseconds; outcome.Error = "timeout"; }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
                { outcome.Ms = timer.Elapsed.TotalMilliseconds; outcome.Error = exception.Message; }
                Console.WriteLine($"{task.Id}: route={outcome.Route ?? "-"} status={outcome.Status ?? outcome.Error} " +
                    $"pass={outcome.Pass} ms={outcome.Ms:F0}");
            }

        var report = Report(fixture, options, repositoryId, outcomes, preflight);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        await File.WriteAllTextAsync(options.ReportPath, report, Encoding.UTF8, token);
        Console.WriteLine("Report: " + Path.GetFullPath(options.ReportPath));
        return preflight is not null ? 2 : outcomes.Any(o => o.Error is not null) ? 1 : 0;
    }

    private static void Score(AgentOutcome outcome, JsonElement run, IReadOnlyDictionary<string, int> lineCounts)
    {
        outcome.Route = run.GetProperty("route").GetString();
        outcome.Status = run.GetProperty("status").GetString();
        var citations = run.GetProperty("findings").EnumerateArray()
            .SelectMany(f => f.GetProperty("citations").EnumerateArray())
            .Select(c => (Path: c.GetProperty("filePath").GetString()!, Start: c.GetProperty("startLine").GetInt32(),
                End: c.GetProperty("endLine").GetInt32())).ToArray();
        outcome.Citations = citations.Length;
        outcome.InvalidCitations = citations.Count(c => !lineCounts.TryGetValue(c.Path, out var count) ||
            c.Start < 1 || c.End < c.Start || c.End > count);
        outcome.CitedPaths = citations.Select(c => c.Path).Distinct().ToArray();
        outcome.ModelFindings = run.GetProperty("findings").EnumerateArray().Count(f => f.GetProperty("origin").GetString() == "model");
        outcome.Gaps = run.TryGetProperty("gaps", out var gaps) && gaps.ValueKind == JsonValueKind.Array ? gaps.GetArrayLength() : 0;
        outcome.Fallbacks = run.GetProperty("trace").EnumerateArray().Count(t =>
            t.GetProperty("status").GetString() is "fallback" or "timeout" or "failed");
        var task = outcome.Task;
        outcome.RouteCorrect = outcome.Route == task.ExpectedRoute;
        // Partial-evidence tasks must keep the cited part (status partial, expected file cited)
        // and name what the code does not establish; abstaining there is an over-refusal.
        outcome.Pass = outcome.RouteCorrect && outcome.InvalidCitations == 0 && (task.ExpectedStatus is not null
            ? outcome.Status == task.ExpectedStatus
            : task.ExpectGaps == true
                ? outcome.Status == "partial" && outcome.Gaps > 0 && outcome.CitedPaths.Any(task.ExpectedPaths!.Contains)
                : outcome.Status is "completed" or "partial" && outcome.CitedPaths.Any(task.ExpectedPaths!.Contains));
    }

    private static string Report(AgentFixture fixture, Arguments options, long repositoryId,
        IReadOnlyList<AgentOutcome> outcomes, string? preflight)
    {
        var text = new StringBuilder("# Multi-agent evaluation — TaskTrail\n\n");
        text.AppendLine($"- Run (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"- API: `{options.BaseUrl}`; repository: `{repositoryId}` ({fixture.RepositoryName})");
        text.AppendLine($"- Fixture: `{options.FixturePath}` with {fixture.Tasks.Length} labeled tasks.").AppendLine();
        if (preflight is not null)
            return text.AppendLine($"**Fixture validation failed:** {Clean(preflight)}\n\nNo tasks were scored.").ToString();

        var grounded = outcomes.Where(o => o.Task.ExpectedPaths is not null && o.Task.ExpectGaps != true).ToArray();
        var partialTasks = outcomes.Where(o => o.Task.ExpectGaps == true).ToArray();
        var abstain = outcomes.Where(o => o.Task.ExpectedStatus == "abstained").ToArray();
        var refuse = outcomes.Where(o => o.Task.ExpectedStatus == "unsupported").ToArray();
        var times = outcomes.Where(o => o.Ms > 0).Select(o => o.Ms).Order().ToArray();
        text.AppendLine($"- Route accuracy: {outcomes.Count(o => o.RouteCorrect)}/{outcomes.Count}");
        text.AppendLine($"- Grounded tasks citing an expected file: {grounded.Count(o => o.Pass)}/{grounded.Length} " +
            $"(completed with validated model findings: {grounded.Count(o => o.Pass && o.Status == "completed")}; " +
            $"partial (fallback or partial coverage): {grounded.Count(o => o.Pass && o.Status == "partial")})");
        text.AppendLine($"- Correct abstentions on absent topics: {abstain.Count(o => o.Pass)}/{abstain.Length}");
        var overRefusals = partialTasks.Count(o => o.Status == "abstained");
        if (partialTasks.Length > 0)
            text.AppendLine($"- Partial-evidence tasks (status partial, expected file cited, gaps named): " +
                $"{partialTasks.Count(o => o.Pass)}/{partialTasks.Length}; over-refusals: {overRefusals}");
        text.AppendLine($"- Correct refusals of side-effect tasks: {refuse.Count(o => o.Pass)}/{refuse.Length}");
        text.AppendLine($"- Citations outside indexed line ranges: {outcomes.Sum(o => o.InvalidCitations)} of {outcomes.Sum(o => o.Citations)}");
        text.AppendLine($"- Runs persisted and reloadable with the same status and trace: {outcomes.Count(o => o.Persisted)}/{outcomes.Count}");
        text.AppendLine($"- Runs with a fallback, timeout or failed step in the trace: {outcomes.Count(o => o.Fallbacks > 0)}");
        text.AppendLine($"- Request time: median {Percentile(times, 0.5)} ms, max {(times.Length == 0 ? "n/a" : times[^1].ToString("F0"))} ms");
        text.AppendLine($"- HTTP, timeout or parse failures: {outcomes.Count(o => o.Error is not null)}").AppendLine();
        text.AppendLine("| Task | Expected | Route | Status | Cited files | Model findings | Gaps | ms | Pass |");
        text.AppendLine("|---|---|---|---|---|---:|---:|---:|---|");
        foreach (var o in outcomes)
            text.AppendLine($"| {Clean(o.Task.Task)} | {o.Task.ExpectedRoute} / {o.Task.ExpectedStatus ?? (o.Task.ExpectGaps == true ? "partial + gaps" : "grounded")} | " +
                $"{o.Route ?? "—"} | {o.Status ?? Clean(o.Error ?? "—")} | {Clean(string.Join(", ", o.CitedPaths.Select(p => p.Split('/')[^1])))} | " +
                $"{o.ModelFindings} | {o.Gaps} | {o.Ms:F0} | {(o.Pass ? "yes" : "no")} |");
        text.AppendLine().AppendLine("A grounded pass requires the expected route, status `completed` or `partial`, at least one finding " +
            "citing a labeled file, and every citation inside that file's indexed line range. `partial` means either the model step " +
            "failed and deterministic tool output was returned, or the model reported partial coverage with gaps; the summary counts it separately. Abstention and refusal " +
            "passes require the exact status. A partial-evidence pass requires status `partial`, a cited labeled file and at " +
            "least one named gap. Citation validity proves provenance, not that every sentence of model prose is " +
            "correct. Ten tasks on one small sample are a smoke test, not a reliability estimate.");
        return text.ToString();
    }

    private static string Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? "n/a" : sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(p * sorted.Length))].ToString("F0");
    private static string Clean(string value) => value.Replace("|", "\\|").Replace('\r', ' ').Replace('\n', ' ');
}

sealed record AgentFixture(string RepositoryName, string[] IndexedPaths, AgentTask[] Tasks);
sealed record AgentTask(string Id, string Task, string ExpectedRoute, string[]? ExpectedPaths, string? ExpectedStatus,
    bool? ExpectGaps = null);
sealed class AgentOutcome(AgentTask task)
{
    public AgentTask Task { get; } = task;
    public string? Route { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public double Ms { get; set; }
    public bool RouteCorrect { get; set; }
    public int Gaps { get; set; }
    public bool Pass { get; set; }
    public bool Persisted { get; set; }
    public int Citations { get; set; }
    public int InvalidCitations { get; set; }
    public int ModelFindings { get; set; }
    public int Fallbacks { get; set; }
    public string[] CitedPaths { get; set; } = [];
}
