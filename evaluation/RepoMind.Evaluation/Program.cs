using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = Arguments.Parse(args);
var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(options.FixturePath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException("Fixture is empty.");
if (fixture.Questions.Length != 10 || fixture.Questions.Count(q => q.ExpectedPath is not null) != 5 ||
    fixture.Questions.Count(q => q.ExpectedPath is null) != 5 ||
    fixture.Questions.Select(q => q.Id).Distinct().Count() != 10)
    throw new InvalidDataException("Fixture must have five supported and five unsupported unique questions.");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
using var client = new HttpClient
{
    BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
};

var outcomes = new List<Outcome>();
string? preflightError = null;
try
{
    await ValidateFixtureAsync(client, fixture, cancellation.Token);
    Console.WriteLine($"Verified indexed fixture: repository {fixture.RepositoryId}, {fixture.IndexedPaths.Length} files.");
}
catch (Exception exception) when (exception is not OperationCanceledException || !cancellation.IsCancellationRequested)
{
    preflightError = exception.Message;
    Console.Error.WriteLine("Fixture check failed: " + preflightError);
}

if (preflightError is null)
{
    foreach (var question in fixture.Questions)
    {
        if (cancellation.IsCancellationRequested) break;
        var outcome = new Outcome(question);
        outcomes.Add(outcome);
        var searchBody = new { query = question.Query, topK = 5 };
        var answerBody = new { query = question.Query, topK = 3 };
        if (question.ExpectedPath is not null)
        {
            var search = await CallAsync(client,
                $"api/repositories/{fixture.RepositoryId}/hybrid/search", searchBody, cancellation.Token);
            outcome.SearchMs = search.ElapsedMs;
            outcome.SearchError = search.Error;
            if (search.Document is not null)
            {
                using var document = search.Document;
                try
                {
                    var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
                    outcome.RankedPaths = results.Select(x => x.GetProperty("filePath").GetString() ?? "").ToArray();
                    var match = Array.FindIndex(results, item => IsRelevant(item, question));
                    outcome.HitRank = match < 0 ? null : match + 1;
                }
                catch (Exception exception) { outcome.SearchError = "Invalid search response: " + exception.Message; }
            }
        }

        var answer = await CallAsync(client,
            $"api/repositories/{fixture.RepositoryId}/rag/answer", answerBody, cancellation.Token);
        outcome.AnswerMs = answer.ElapsedMs;
        outcome.AnswerError = answer.Error;
        if (answer.Document is not null)
        {
            using var document = answer.Document;
            try
            {
                var root = document.RootElement;
                outcome.AnswerStatus = root.GetProperty("status").GetString();
                var sources = root.GetProperty("sources").EnumerateArray().ToArray();
                outcome.AnswerPaths = sources.Select(x => x.GetProperty("filePath").GetString() ?? "").ToArray();
                if (question.ExpectedPath is not null)
                    outcome.AnswerSourceRelevant = sources.Any(item => IsRelevant(item, question));
                else
                {
                    outcome.CorrectAbstention = outcome.AnswerStatus == "insufficient_evidence" && sources.Length == 0;
                    outcome.FalseAcceptance = outcome.AnswerStatus == "answered";
                }
            }
            catch (Exception exception) { outcome.AnswerError = "Invalid answer response: " + exception.Message; }
        }
        Console.WriteLine($"{question.Id}: rank={outcome.HitRank?.ToString() ?? "miss"}, " +
            $"answer={outcome.AnswerStatus ?? outcome.AnswerError ?? "unavailable"}, " +
            $"answerMs={outcome.AnswerMs:F0}");
    }
}

var report = Report(fixture, options, outcomes, preflightError);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
await File.WriteAllTextAsync(options.ReportPath, report, Encoding.UTF8);
Console.WriteLine("Report: " + Path.GetFullPath(options.ReportPath));
if (preflightError is not null) Environment.ExitCode = 2;
else if (outcomes.Count != 10 || outcomes.Any(x => x.SearchError is not null || x.AnswerError is not null))
    Environment.ExitCode = 1;

static bool IsRelevant(JsonElement item, Question question) =>
    item.GetProperty("filePath").GetString() == question.ExpectedPath &&
    item.GetProperty("startLine").GetInt32() <= question.AnchorLine &&
    item.GetProperty("endLine").GetInt32() >= question.AnchorLine &&
    item.GetProperty("content").GetString()!.Contains(question.Anchor!, StringComparison.Ordinal);

static async Task ValidateFixtureAsync(HttpClient client, Fixture fixture, CancellationToken token)
{
    using var fileList = await GetAsync(client, $"api/repositories/{fixture.RepositoryId}/files", token);
    var actualPaths = fileList.RootElement.EnumerateArray()
        .Select(file => file.GetProperty("path").GetString() ?? "").Order(StringComparer.Ordinal).ToArray();
    if (!actualPaths.SequenceEqual(fixture.IndexedPaths.Order(StringComparer.Ordinal)))
        throw new InvalidDataException("Indexed paths differ from the labeled fixture. Check repository ID/version.");

    var indexedText = new StringBuilder();
    var byPath = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
    try
    {
        foreach (var path in actualPaths)
        {
            var document = await GetAsync(client,
                $"api/repositories/{fixture.RepositoryId}/files/content?path={Uri.EscapeDataString(path)}", token);
            byPath.Add(path, document);
            var root = document.RootElement;
            if (root.GetProperty("path").GetString() != path || root.GetProperty("complete").GetBoolean() ||
                root.GetProperty("gapRanges").GetArrayLength() != 0 ||
                root.GetProperty("conflictRanges").GetArrayLength() != 0)
                throw new InvalidDataException($"Indexed coverage changed or has gaps/conflicts: {path}.");
            indexedText.AppendLine(path);
            foreach (var line in root.GetProperty("lines").EnumerateArray())
            {
                if (line.GetProperty("status").GetString() != "covered")
                    throw new InvalidDataException($"Uncovered indexed line in {path}.");
                indexedText.AppendLine(line.GetProperty("content").GetString());
            }
        }

        foreach (var question in fixture.Questions)
        {
            if (question.ExpectedPath is not null)
            {
                if (question.AnchorLine is null || string.IsNullOrEmpty(question.Anchor) ||
                    !byPath.TryGetValue(question.ExpectedPath, out var source))
                    throw new InvalidDataException($"Incomplete source label: {question.Id}.");
                var lines = source.RootElement.GetProperty("lines");
                if (question.AnchorLine < 1 || question.AnchorLine > lines.GetArrayLength() ||
                    !lines[question.AnchorLine.Value - 1].GetProperty("content").GetString()!
                        .Contains(question.Anchor, StringComparison.Ordinal))
                    throw new InvalidDataException($"Source anchor changed: {question.Id}.");
            }
            else
            {
                if (question.AbsencePatterns is not { Length: > 0 })
                    throw new InvalidDataException($"Missing absence check: {question.Id}.");
                foreach (var pattern in question.AbsencePatterns)
                    if (Regex.IsMatch(indexedText.ToString(), pattern, RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(1)))
                        throw new InvalidDataException($"Unsupported feature term found for {question.Id}: {pattern}.");
            }
        }
    }
    finally { foreach (var document in byPath.Values) document.Dispose(); }
}

static async Task<JsonDocument> GetAsync(HttpClient client, string path, CancellationToken token)
{
    using var response = await client.GetAsync(path, token);
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"GET {path} returned HTTP {(int)response.StatusCode}.");
    await using var stream = await response.Content.ReadAsStreamAsync(token);
    return await JsonDocument.ParseAsync(stream, cancellationToken: token);
}

static async Task<CallResult> CallAsync(HttpClient client, string path, object body, CancellationToken token)
{
    var timer = Stopwatch.StartNew();
    try
    {
        using var response = await client.PostAsJsonAsync(path, body, token);
        if (!response.IsSuccessStatusCode)
            return new(null, timer.Elapsed.TotalMilliseconds, $"HTTP {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return new(await JsonDocument.ParseAsync(stream, cancellationToken: token),
            timer.Elapsed.TotalMilliseconds, null);
    }
    catch (OperationCanceledException) when (!token.IsCancellationRequested)
    { return new(null, timer.Elapsed.TotalMilliseconds, "timeout"); }
    catch (OperationCanceledException) { return new(null, timer.Elapsed.TotalMilliseconds, "cancelled"); }
    catch (Exception exception)
    { return new(null, timer.Elapsed.TotalMilliseconds, exception.Message); }
}

static string Report(Fixture fixture, Arguments options, IReadOnlyList<Outcome> results, string? preflightError)
{
    var supported = results.Where(x => x.Question.ExpectedPath is not null).ToArray();
    var unsupported = results.Where(x => x.Question.ExpectedPath is null).ToArray();
    var answerTimes = results.Where(x => x.AnswerMs is not null).Select(x => x.AnswerMs!.Value).ToArray();
    var searchTimes = supported.Where(x => x.SearchMs is not null).Select(x => x.SearchMs!.Value).ToArray();
    var text = new StringBuilder();
    text.AppendLine("# Day 14 — TaskTrail RAG evaluation").AppendLine();
    text.AppendLine($"- Run (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
    text.AppendLine($"- API: `{options.BaseUrl}`; repository: `{fixture.RepositoryId}` ({fixture.RepositoryName})");
    text.AppendLine("- Retrieval: hybrid search `topK: 5`; RAG answer: `topK: 3` (same as the Vue UI).");
    text.AppendLine("- Reranking: evaluator uses the API process setting; record that setting when comparing runs.");
    text.AppendLine("- Fixture: 12 expected indexed paths, five implementation anchors, and five absence-term checks.");
    text.AppendLine();
    if (preflightError is not null)
    {
        text.AppendLine($"**Fixture validation failed:** {Clean(preflightError)}").AppendLine();
        text.AppendLine("No questions were scored.");
        return text.ToString();
    }
    text.AppendLine($"- Total questions: {fixture.Questions.Length}; attempted: {results.Count}; skipped: {fixture.Questions.Length - results.Count}");
    text.AppendLine($"- Hit@1: {supported.Count(x => x.HitRank == 1)}/5 supported questions");
    text.AppendLine($"- Hit@5: {supported.Count(x => x.HitRank is >= 1 and <= 5)}/5 supported questions");
    text.AppendLine($"- Grounded supported answers: {supported.Count(x => x.AnswerStatus == "answered" && x.AnswerSourceRelevant)}/5");
    text.AppendLine($"- Correct abstentions: {unsupported.Count(x => x.CorrectAbstention)}/5 unsupported questions");
    text.AppendLine($"- False acceptances: {unsupported.Count(x => x.FalseAcceptance)}/5 unsupported questions");
    text.AppendLine($"- Average RAG answer request time: {Average(answerTimes)} ms across {answerTimes.Length} attempts");
    text.AppendLine($"- Average retrieval request time: {Average(searchTimes)} ms across {searchTimes.Length} attempts");
    text.AppendLine($"- HTTP, timeout, or response-parse failures: {results.Count(x => x.SearchError is not null || x.AnswerError is not null)} question(s)");
    text.AppendLine();
    text.AppendLine("A retrieval hit requires the exact labeled path, the anchor line within the returned chunk range, and the anchor text in that chunk. HTTP 200 alone is never a hit. A correct abstention requires `insufficient_evidence` with zero sources. `answered` on an unsupported question is a false acceptance; unavailable or uncertain outcomes are failures, not correct abstentions.");
    text.AppendLine();
    text.AppendLine("| Question | Retrieval rank | Answer status | Labeled source in answer | Search ms | Answer ms | Outcome |");
    text.AppendLine("|---|---:|---|---|---:|---:|---|");
    foreach (var question in fixture.Questions)
    {
        var result = results.FirstOrDefault(x => x.Question.Id == question.Id);
        if (result is null)
        { text.AppendLine($"| {Clean(question.Query)} | — | — | — | — | — | skipped |"); continue; }
        var verdict = question.ExpectedPath is not null
            ? result.AnswerError is not null ? "answer request failed" :
              result.AnswerStatus == "answered" && result.AnswerSourceRelevant ? "grounded answer" : "answer/source miss"
            : result.CorrectAbstention ? "correct abstention" :
              result.FalseAcceptance ? "false acceptance" : "abstention failure";
        if (result.SearchError is not null) verdict += "; search: " + result.SearchError;
        if (result.AnswerError is not null) verdict += "; answer: " + result.AnswerError;
        text.AppendLine($"| {Clean(question.Query)} | {result.HitRank?.ToString() ?? (question.ExpectedPath is null ? "n/a" : "miss")} | " +
            $"{Clean(result.AnswerStatus ?? "unavailable")} | {(question.ExpectedPath is null ? "n/a" : result.AnswerSourceRelevant ? "yes" : "no")} | " +
            $"{Milliseconds(result.SearchMs)} | {Milliseconds(result.AnswerMs)} | {Clean(verdict)} |");
        if (question.ExpectedPath is not null)
            text.AppendLine($"| ↳ `{question.Id}` | Expected `{question.ExpectedPath}` L{question.AnchorLine} | " +
                $"Retrieved: {Clean(string.Join(", ", result.RankedPaths))} | Answer sources: {Clean(string.Join(", ", result.AnswerPaths))} | | | |");
    }
    text.AppendLine();
    text.AppendLine("Hit@1 example: if the first of five returned chunks is the labeled implementation chunk, that question scores one hit. If it first appears at rank 3, it scores Hit@5 but not Hit@1. The denominator stays five supported questions; request failures are shown separately and score no hit. Average time uses Stopwatch wall time for every attempted request, including failed requests.");
    text.AppendLine();
    text.AppendLine("A grounded supported answer here means the answer cited the labeled implementation chunk; this run does not judge every sentence of generated prose. This is a ten-question smoke set for one indexed sample, not a general reliability estimate. The labels confirm absence only from this indexed fixture; ZIP ingestion may have excluded other files.");
    return text.ToString();
}

static string Clean(string value) => value.Replace("|", "\\|").Replace('\r', ' ').Replace('\n', ' ');
static string Milliseconds(double? value) => value is null ? "—" : value.Value.ToString("F0");
static string Average(double[] values) => values.Length == 0 ? "n/a" : values.Average().ToString("F0");

sealed record Fixture(int RepositoryId, string RepositoryName, string[] IndexedPaths, Question[] Questions);
sealed record Question(string Id, string Query, string? ExpectedPath, int? AnchorLine,
    string? Anchor, string[]? AbsencePatterns);
sealed record CallResult(JsonDocument? Document, double ElapsedMs, string? Error);
sealed class Outcome(Question question)
{
    public Question Question { get; } = question;
    public int? HitRank { get; set; }
    public string[] RankedPaths { get; set; } = [];
    public string[] AnswerPaths { get; set; } = [];
    public bool AnswerSourceRelevant { get; set; }
    public bool CorrectAbstention { get; set; }
    public bool FalseAcceptance { get; set; }
    public string? AnswerStatus { get; set; }
    public string? SearchError { get; set; }
    public string? AnswerError { get; set; }
    public double? SearchMs { get; set; }
    public double? AnswerMs { get; set; }
}

sealed record Arguments(string BaseUrl, string FixturePath, string ReportPath, int TimeoutSeconds)
{
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--"))
                throw new ArgumentException("Use --base-url, --fixture, --report, and --timeout-seconds with values.");
            values[args[index]] = args[index + 1];
        }
        var timeout = int.Parse(values.GetValueOrDefault("--timeout-seconds", "210"));
        if (timeout is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(timeout));
        return new Arguments(values.GetValueOrDefault("--base-url", "http://127.0.0.1:5000"),
            values.GetValueOrDefault("--fixture", "evaluation/day14-tasktrail.json"),
            values.GetValueOrDefault("--report", "evaluation/day14-report.md"), timeout);
    }
}
