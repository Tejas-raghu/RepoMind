using System.Text.RegularExpressions;
using RepoMind.Api.Contracts;
using RepoMind.Api.Services;

namespace RepoMind.Api.Agents;

public sealed record ReviewRule(string Id, string Severity, string Title, string Detail, Regex Pattern,
    string[] Extensions);

// Deterministic review heuristics. They can report false positives (they match
// text, not semantics), but each finding points at a real stored line, so the
// Review Agent still has grounded output when the model is slow or unavailable.
// Messages never echo the matched value, which could be a secret.
public static class StaticReviewRules
{
    public const int MaxFindingsPerRule = 3;
    public const int MaxFindings = 12;
    private static readonly string[] Script = [".ts", ".tsx", ".js", ".jsx", ".vue"];
    private static readonly string[] CSharp = [".cs"];
    private static readonly string[] Code = [.. Script, .. CSharp];

    public static readonly IReadOnlyList<ReviewRule> Rules =
    [
        new("redacted-secret", "high", "Credential removed during indexing",
            "A credential-like value on this line was redacted when the repository was indexed. Move secrets to configuration or a secret store and rotate the exposed value.",
            new Regex(Regex.Escape(SecretRedactor.Marker)), [.. Code, ".json", ".md", ".html"]),
        new("hardcoded-secret", "high", "Possible hard-coded credential",
            "A credential-like name is assigned a literal value. Load it from configuration or a secret store instead.",
            new Regex(@"(?i)\b\w*(?:password|passwd|secret|api[_-]?key|private[_-]?key|signing[_-]?key)\w*[""']?\s*[:=]\s*[""'`][^""'`\s]{6,}[""'`]"), Code),
        new("jwt-validation-disabled", "high", "Token validation disabled",
            "A JWT validation flag is set to false, which can let forged or expired tokens through.",
            new Regex(@"\bValidate(?:Issuer|Audience|Lifetime|IssuerSigningKey)\s*=\s*false\b"), CSharp),
        new("dynamic-code", "high", "Dynamic code execution",
            "eval or the Function constructor executes strings as code and is unsafe with untrusted input.",
            new Regex(@"\beval\s*\(|\bnew\s+Function\s*\("), Script),
        new("sql-concatenation", "high", "SQL built from strings",
            "SQL text appears to be concatenated or interpolated. Use parameters to prevent SQL injection.",
            new Regex(@"(?i)(?:\$""[^""]*\b(?:select|insert|update|delete)\b[^""]*\{|[""'][^""']*\b(?:select|insert|update|delete)\b[^""']*[""']\s*\+)"), Code),
        new("token-in-local-storage", "medium", "Token stored in localStorage",
            "Tokens in localStorage are readable by any script on the page, so an XSS bug can steal them. Consider httpOnly cookies or in-memory storage.",
            new Regex(@"(?i)localStorage\.(?:setItem|getItem)\(\s*[""'`][^""'`]*(?:token|jwt|auth)"), Script),
        new("raw-html", "medium", "Raw HTML rendering",
            "Rendering raw HTML can enable XSS when the content is not trusted or sanitized.",
            new Regex(@"\.innerHTML\s*=|\bv-html\s*=|dangerouslySetInnerHTML"), Script),
        new("cors-any-origin", "medium", "CORS allows any origin",
            "AllowAnyOrigin lets every website call this API from a browser.",
            new Regex(@"\bAllowAnyOrigin\s*\("), CSharp),
        new("sensitive-logging", "medium", "Sensitive value logged",
            "A token, password or secret appears in a log statement.",
            new Regex(@"(?i)(?:console\.(?:log|debug|info)|Log(?:Information|Debug|Warning))\s*\([^)]*\b(?:token|password|secret)\b"), Code),
        new("empty-catch", "low", "Empty catch block",
            "Errors are swallowed silently, which hides failures.",
            new Regex(@"\bcatch\s*(?:\([^)]*\))?\s*\{\s*\}"), Code),
        new("plain-http", "low", "Plain HTTP URL",
            "A non-local http:// URL sends data unencrypted.",
            new Regex(@"[""'`]http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0)[^""'`\s]+"), Code),
        new("todo-marker", "low", "Unfinished work marker",
            "A TODO/FIXME/HACK comment marks known unfinished or fragile code.",
            new Regex(@"\b(?:TODO|FIXME|HACK)\b"), Code)
    ];

    public static IReadOnlyList<AgentFinding> Check(string path, IReadOnlyList<SourceLine> lines)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var findings = new List<AgentFinding>();
        foreach (var rule in Rules.Where(rule => rule.Extensions.Contains(extension)))
        {
            var hits = 0;
            foreach (var line in lines)
            {
                if (line.Content is null || !rule.Pattern.IsMatch(line.Content)) continue;
                // The redaction marker already explains a hard-coded secret on this line.
                if (rule.Id == "hardcoded-secret" && line.Content.Contains(SecretRedactor.Marker)) continue;
                findings.Add(new AgentFinding(rule.Title, rule.Detail, rule.Severity, "rule",
                    [new AgentCitation(path, line.Number, line.Number)]) { RuleId = rule.Id });
                if (++hits >= MaxFindingsPerRule || findings.Count >= MaxFindings) break;
            }
            if (findings.Count >= MaxFindings) break;
        }
        return findings;
    }
}
