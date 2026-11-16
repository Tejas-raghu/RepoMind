using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Npgsql;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Agents;

// Controlled tool execution. Agents and the model never touch the database or
// file system directly: they request one of five allowlisted, read-only tools.
// Every call is checked here before it runs:
// - the repository ID comes from run state (the URL), never from model output;
// - paths must be indexed files of that repository;
// - a per-run call budget and evidence character budget are enforced;
// - expected failures are recorded in the trace and return null, so the
//   orchestrator can continue with whatever evidence it already has.
public sealed class AgentToolbox(HybridSearchService search, RepositorySourceService sources,
    ILogger<AgentToolbox> logger)
{
    public const int MaxSearchResults = 8;
    public static readonly IReadOnlySet<string> AllowedTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_files", "search_code", "read_file", "outline_file", "static_checks"
    };

    public Task<IReadOnlyList<string>?> ListFilesAsync(AgentRunState state, string agent,
        CancellationToken cancellationToken) =>
        RunAsync(state, agent, "list_files", $"repository {state.RepositoryId}", async () =>
        {
            var files = await sources.ListAsync(state.RepositoryId, cancellationToken);
            IReadOnlyList<string> paths = files.Select(file => file.Path).ToArray();
            return (paths, $"{paths.Count} indexed files");
        }, cancellationToken);

    public Task<IReadOnlyList<HybridSearchResult>?> SearchCodeAsync(AgentRunState state, string agent,
        string query, int topK, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length is 0 or > RepositorySearchRequest.MaxQueryLength)
            return Rejected<IReadOnlyList<HybridSearchResult>>(state, agent, "search_code",
                "query must be 1-500 characters");
        topK = Math.Clamp(topK, 1, MaxSearchResults);
        var shown = query.Length > 80 ? query[..80] + "..." : query;
        return RunAsync(state, agent, "search_code", $"\"{shown}\" top {topK}", async () =>
        {
            var response = await search.SearchAsync(state.RepositoryId, query, topK, cancellationToken)
                ?? throw new InvalidDataException("Repository not found.");
            var detail = response.Results.Count == 0 ? "0 results" :
                $"{response.Results.Count} results: " + string.Join(", ", response.Results.Take(5)
                    .Select(result => $"{result.FilePath}:{result.StartLine}-{result.EndLine}"));
            return (response.Results, detail);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AgentEvidence>?> ReadFileAsync(AgentRunState state, string agent,
        string path, int startLine, int endLine, CancellationToken cancellationToken)
    {
        if (!Allowed(state, path))
            return Rejected<IReadOnlyList<AgentEvidence>>(state, agent, "read_file", "path is not an indexed file of this repository");
        var range = $"{startLine}-{(endLine == int.MaxValue ? "end" : endLine)}";
        return RunAsync(state, agent, "read_file", $"{path}:{range}", async () =>
        {
            var file = await LoadAsync(state, path, cancellationToken);
            var last = file.Lines.Count;
            var start = Math.Clamp(startLine, 1, last);
            var end = Math.Min(Math.Min(Math.Max(endLine, start), last), start + state.Options.MaxLinesPerFile - 1);
            var notes = new List<string>();
            if (end < Math.Min(endLine, last)) notes.Add($"window limited to {state.Options.MaxLinesPerFile} lines");
            var added = new List<AgentEvidence>();
            var block = new List<SourceLine>();
            // A trailing null flushes the final block of covered lines.
            foreach (var line in file.Lines.Skip(start - 1).Take(end - start + 1).Cast<SourceLine?>().Append(null))
            {
                if (line is { Status: "covered" }) { block.Add(line); continue; }
                if (block.Count > 0 && !AddBlock(block)) break;
                block.Clear();
                if (line is not null) notes.Add($"line {line.Number} {line.Status}");
            }
            IReadOnlyList<AgentEvidence> result = added;
            var detail = added.Count == 0 ? "no evidence added" : string.Join(", ", added.Select(item =>
                $"{item.Id} {item.FilePath}:{item.StartLine}-{item.EndLine}"));
            if (notes.Count > 0) detail += " (" + string.Join("; ", notes.Distinct().Take(4)) + ")";
            return (result, detail);

            bool AddBlock(List<SourceLine> lines)
            {
                var room = state.Options.MaxEvidenceCharacters - state.EvidenceCharacters;
                var taken = new List<SourceLine>();
                var size = 0;
                foreach (var line in lines)
                {
                    var cost = line.Content!.Length + 1;
                    if (size + cost > room) break;
                    taken.Add(line);
                    size += cost;
                }
                if (taken.Count > 0)
                    added.Add(state.AddEvidence(path, taken[0].Number, taken[^1].Number,
                        string.Join('\n', taken.Select(line => line.Content)), "read_file"));
                if (taken.Count == lines.Count) return true;
                notes.Add("evidence budget reached");
                return false;
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CodeSymbol>?> OutlineFileAsync(AgentRunState state, string agent,
        string path, CancellationToken cancellationToken)
    {
        if (!Allowed(state, path))
            return Rejected<IReadOnlyList<CodeSymbol>>(state, agent, "outline_file", "path is not an indexed file of this repository");
        return RunAsync(state, agent, "outline_file", path, async () =>
        {
            var file = await LoadAsync(state, path, cancellationToken);
            var symbols = CodeOutline.Extract(path, file.Lines);
            return (symbols, $"{symbols.Count} symbols");
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AgentFinding>?> StaticChecksAsync(AgentRunState state, string agent,
        string path, CancellationToken cancellationToken)
    {
        if (!Allowed(state, path))
            return Rejected<IReadOnlyList<AgentFinding>>(state, agent, "static_checks", "path is not an indexed file of this repository");
        return RunAsync(state, agent, "static_checks", path, async () =>
        {
            var file = await LoadAsync(state, path, cancellationToken);
            var findings = StaticReviewRules.Check(path, file.Lines);
            var detail = findings.Count == 0 ? "no rule matches" : $"{findings.Count} rule findings: " +
                string.Join(", ", findings.Select(item => $"{item.RuleId}@{item.Citations[0].StartLine}"));
            return (findings, detail);
        }, cancellationToken);
    }

    public static string FormatOutline(string path, IReadOnlyList<CodeSymbol> symbols)
    {
        var builder = new StringBuilder($"Outline of {path}:\n");
        foreach (var symbol in symbols) builder.Append($"  line {symbol.Line}: {symbol.Kind} {symbol.Name}\n");
        return builder.ToString();
    }

    private static bool Allowed(AgentRunState state, string path) =>
        RepositorySourceService.ValidPath(path) && state.IndexedPaths.Contains(path, StringComparer.Ordinal);

    private async Task<IndexedFileContent> LoadAsync(AgentRunState state, string path,
        CancellationToken cancellationToken)
    {
        if (state.Files.TryGetValue(path, out var cached)) return cached;
        var file = await sources.GetContentAsync(state.RepositoryId, path, cancellationToken)
            ?? throw new InvalidDataException("Indexed file not found.");
        state.Files[path] = file;
        return file;
    }

    private static Task<T?> Rejected<T>(AgentRunState state, string agent, string tool, string reason)
        where T : class
    {
        state.ToolCalls++;
        state.Record(agent, tool, "rejected", null, reason);
        return Task.FromResult<T?>(null);
    }

    private async Task<T?> RunAsync<T>(AgentRunState state, string agent, string tool, string input,
        Func<Task<(T Value, string Detail)>> action, CancellationToken cancellationToken) where T : class
    {
        if (!AllowedTools.Contains(tool)) throw new InvalidOperationException($"Tool {tool} is not allowlisted.");
        var clock = Stopwatch.StartNew();
        if (state.ToolCalls >= state.Options.MaxToolCalls)
        {
            state.Record(agent, tool, "skipped", clock, $"{input}; tool budget of {state.Options.MaxToolCalls} calls exhausted");
            return null;
        }
        state.ToolCalls++;
        try
        {
            var (value, detail) = await action();
            state.Record(agent, tool, "ok", clock, $"{input} -> {detail}");
            return value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            state.Record(agent, tool, "timeout", clock, $"{input} -> provider timed out");
            return null;
        }
        catch (Exception exception) when (exception is NpgsqlException or HttpRequestException or
            JsonException or EmbeddingModelMismatchException or SourceTooLargeException or
            InvalidDataException or CloudflareAiException)
        {
            logger.LogWarning("Agent tool {Tool} failed: {ErrorType}", tool, exception.GetType().Name);
            state.Record(agent, tool, "failed", clock, $"{input} -> {Describe(exception)}");
            return null;
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        EmbeddingModelMismatchException => "repository was indexed with a different embedding provider/model",
        SourceTooLargeException => "file exceeds the viewer size limit",
        NpgsqlException => "PostgreSQL unavailable",
        HttpRequestException or CloudflareAiException => "AI provider unavailable",
        JsonException => "AI provider returned an invalid response",
        _ => exception.Message
    };
}
