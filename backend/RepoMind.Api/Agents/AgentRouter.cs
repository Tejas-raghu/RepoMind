using System.Text.RegularExpressions;

namespace RepoMind.Api.Agents;

public enum AgentRoute { Question, Analysis, Review, Unsupported }

public sealed record RouteDecision(AgentRoute Route, string Reason, IReadOnlyList<string> TargetPaths)
{
    public string Name => AgentRouter.Name(Route);
}

// The Supervisor's router is rule-based on purpose: a local 4B model adds tens of
// seconds per call, and routing must be instant, testable and explainable. Model
// calls are reserved for steps where judgment adds value (planning, synthesis).
public static class AgentRouter
{
    public const int MaxTargets = 2;

    // RepoMind agents are read-only. Requests to change, run or ship code are refused.
    private static readonly Regex SideEffect = new(
        @"^\s*(?:please\s+|can\s+you\s+|could\s+you\s+|go\s+and\s+)?" +
        @"(?<verb>delete|remove|drop|deploy|push|commit|merge|install|run|execute|modify|edit|rewrite|" +
        @"refactor|fix|implement|create|write|generate|upload|send|rename|move|update|change|replace)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Review = new(
        @"\b(?:review|audit|vulnerab\w*|insecure|code\s+smells?|bugs?|weakness(?:es)?|" +
        @"security\s+(?:issues?|risks?|problems?|concerns?|flaws?|review)|" +
        @"(?:potential|possible)\s+(?:issues?|problems?|bugs?|risks?)|what(?:'s|\s+is)\s+wrong)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Analysis = new(
        @"\b(?:explain|analy[sz]e|analysis|summari[sz]e|summary|overview|walk\s+(?:me\s+)?through|" +
        @"describe|responsibilit\w*|dependenc\w*|outline|structure\s+of|what\s+does\s+.+\s+do)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileMention = new(
        @"(?<![\w/.-])(?<path>[A-Za-z0-9_][A-Za-z0-9_./-]*\.(?:cs|tsx?|jsx?|vue|json|md|html))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Name(AgentRoute route) => route switch
    {
        AgentRoute.Question => "question",
        AgentRoute.Analysis => "analysis",
        AgentRoute.Review => "review",
        _ => "unsupported"
    };

    public static RouteDecision Route(string task, IReadOnlyList<string> indexedPaths)
    {
        var sideEffect = SideEffect.Match(task);
        if (sideEffect.Success)
            return new RouteDecision(AgentRoute.Unsupported,
                $"The task asks to '{sideEffect.Groups["verb"].Value.ToLowerInvariant()}'. RepoMind agents are read-only: they explain, locate and review code but never change, run or deploy it.",
                []);

        var (targets, notes) = ResolveTargets(task, indexedPaths);
        var targetNote = targets.Count > 0 ? $" Target: {string.Join(", ", targets)}." : "";
        if (notes.Count > 0) targetNote += " " + string.Join(" ", notes);

        if (Review.IsMatch(task))
            return new RouteDecision(AgentRoute.Review,
                "Review keywords matched; the Review Agent inspects code with deterministic checks and the model." + targetNote,
                targets);
        if (Analysis.IsMatch(task))
            return new RouteDecision(AgentRoute.Analysis,
                "Explanation keywords matched; the Code Analysis Agent outlines and explains the code." + targetNote,
                targets);
        return new RouteDecision(AgentRoute.Question,
            "Locate/implementation question; the Retrieval Agent runs the verified RAG answer flow." + targetNote,
            targets);
    }

    // Accepts a full indexed path or a file name that identifies exactly one indexed file.
    public static (IReadOnlyList<string> Targets, IReadOnlyList<string> Notes) ResolveTargets(
        string task, IReadOnlyList<string> indexedPaths)
    {
        var targets = new List<string>();
        var notes = new List<string>();
        foreach (Match match in FileMention.Matches(task))
        {
            var mention = match.Groups["path"].Value.TrimStart('.', '/');
            var exact = indexedPaths.FirstOrDefault(path => path.Equals(mention, StringComparison.OrdinalIgnoreCase));
            string[] candidates = exact is not null ? [exact] : indexedPaths
                .Where(path => path.EndsWith("/" + mention, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 1)
            {
                if (!targets.Contains(candidates[0]) && targets.Count < MaxTargets) targets.Add(candidates[0]);
            }
            else if (candidates.Length > 1)
                notes.Add($"'{mention}' matches {candidates.Length} indexed files, so search is used instead.");
            else
                notes.Add($"'{mention}' is not an indexed file.");
        }
        return (targets, notes);
    }
}
