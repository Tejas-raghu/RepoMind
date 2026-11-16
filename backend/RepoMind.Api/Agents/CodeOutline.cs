using System.Text.RegularExpressions;
using RepoMind.Api.Contracts;

namespace RepoMind.Api.Agents;

public sealed record CodeSymbol(string Kind, string Name, int Line);

// A deterministic "static analysis" tool: regular expressions find imports and
// declarations with their original line numbers. It is approximate (no parser),
// but it is instant, never invents a symbol, and gives the model a whole-file map
// even when only part of a long file fits in the evidence budget.
public static class CodeOutline
{
    public const int MaxSymbols = 80;

    private static readonly Regex ScriptImport = new(
        @"^\s*import\s+(?:type\s+)?(?:[^'""]+?\s+from\s+)?['""](?<name>[^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex CSharpUsing = new(@"^\s*using\s+(?:static\s+)?(?<name>[A-Za-z_][\w.]*)\s*;", RegexOptions.Compiled);
    private static readonly Regex TypeDeclaration = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:(?:public|private|protected|internal|abstract|sealed|static|partial|readonly)\s+)*" +
        @"(?<kind>class|interface|record|enum|struct|type)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex ScriptFunction = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(?<name>[A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ArrowFunction = new(
        @"^\s*(?:export\s+)?(?:const|let)\s+(?<name>[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=>", RegexOptions.Compiled);
    private static readonly Regex ExportedValue = new(
        @"^\s*export\s+(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex CSharpMethod = new(
        @"^\s*(?:(?:public|private|protected|internal)\s+)(?:(?:static|async|virtual|override|sealed|abstract|new|extern)\s+)*" +
        @"[\w<>\[\],.?]+(?:\s*<[^>]*>)?\s+(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex CSharpAttribute = new(
        @"^\s*\[(?<name>(?:Http(?:Get|Post|Put|Delete|Patch)|Route|Authorize|AllowAnonymous|ApiController)\b[^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex VueApi = new(@"\b(?<name>defineProps|defineEmits|defineModel|onMounted|watch|computed)\s*[<(]", RegexOptions.Compiled);

    public static IReadOnlyList<CodeSymbol> Extract(string path, IReadOnlyList<SourceLine> lines)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var csharp = extension == ".cs";
        var script = extension is ".ts" or ".tsx" or ".js" or ".jsx" or ".vue";
        if (!csharp && !script) return [];
        var symbols = new List<CodeSymbol>();
        foreach (var line in lines)
        {
            if (line.Content is not { } text || symbols.Count >= MaxSymbols) continue;
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*")) continue;
            Match match;
            if (script && (match = ScriptImport.Match(text)).Success) Add("import", match);
            else if (csharp && (match = CSharpUsing.Match(text)).Success) Add("import", match);
            else if (csharp && (match = CSharpAttribute.Match(text)).Success) Add("attribute", match);
            else if ((match = TypeDeclaration.Match(text)).Success)
                Add(match.Groups["kind"].Value == "type" ? "type" : match.Groups["kind"].Value, match);
            else if (script && (match = ScriptFunction.Match(text)).Success) Add("function", match);
            else if (script && (match = ArrowFunction.Match(text)).Success) Add("function", match);
            else if (script && (match = ExportedValue.Match(text)).Success) Add("export", match);
            else if (csharp && (match = CSharpMethod.Match(text)).Success &&
                !Regex.IsMatch(text, @"\b(?:new|return|await|if|while|for|foreach|switch|catch|using)\b\s*\(?\s*$"))
                Add("method", match);
            else if (extension == ".vue" && (match = VueApi.Match(text)).Success) Add("vue-api", match);

            void Add(string kind, Match found) =>
                symbols.Add(new CodeSymbol(kind, found.Groups["name"].Value.Trim(), line.Number));
        }
        return symbols;
    }
}
