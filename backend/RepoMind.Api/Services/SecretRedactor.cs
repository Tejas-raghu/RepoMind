using System.Text.RegularExpressions;

namespace RepoMind.Api.Services;

public sealed record RedactionResult(string Content, int Redactions);

// File-name filtering cannot see a credential inside an ordinary source file.
// Indexed text is embedded and may be sent to a model, so high-confidence secret
// values are replaced before chunking. Replacement stays on the same line, which
// keeps stored line numbers and citations exact.
public static class SecretRedactor
{
    public const string Marker = "[REDACTED:secret]";

    private static readonly Regex[] ValuePatterns =
    [
        new(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled),
        new(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled),
        new(@"\bgithub_pat_[A-Za-z0-9_]{22,}\b", RegexOptions.Compiled),
        new(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled),
        new(@"\bsk-(?:ant-)?[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled),
        new(@"\b(?:sk|rk)_(?:live|test)_[0-9A-Za-z]{16,}\b", RegexOptions.Compiled),
        new(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)
    ];

    // Only the quoted value is replaced, so the code shape stays readable.
    private static readonly Regex Assignment = new(
        @"(?i)\b(?<name>[A-Za-z0-9_]*(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)[A-Za-z0-9_]*)" +
        @"(?<sep>[""']?\s*[:=]\s*)(?<quote>[""'`])(?<value>[^""'`\s]{8,})\k<quote>",
        RegexOptions.Compiled);

    private static readonly Regex ConnectionPassword = new(
        @"(?i)\b(?<key>password|pwd)=(?<value>[^;""'\s]{4,})", RegexOptions.Compiled);

    private static readonly Regex PemBegin = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled);
    private static readonly Regex PemEnd = new(@"-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled);

    public static RedactionResult Redact(string content)
    {
        var lines = content.Split('\n');
        var count = 0;
        var insidePem = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (insidePem || PemBegin.IsMatch(line))
            {
                // Replace every key line, including the markers, one for one.
                insidePem = !PemEnd.IsMatch(line);
                lines[index] = Marker;
                count++;
                continue;
            }
            foreach (var pattern in ValuePatterns)
                line = pattern.Replace(line, _ => { count++; return Marker; });
            line = Assignment.Replace(line, match =>
            {
                if (Placeholder(match.Groups["value"].Value)) return match.Value;
                count++;
                return match.Groups["name"].Value + match.Groups["sep"].Value +
                    match.Groups["quote"].Value + Marker + match.Groups["quote"].Value;
            });
            line = ConnectionPassword.Replace(line, match =>
            {
                if (Placeholder(match.Groups["value"].Value)) return match.Value;
                count++;
                return match.Groups["key"].Value + "=" + Marker;
            });
            lines[index] = line;
        }
        return new RedactionResult(count == 0 ? content : string.Join('\n', lines), count);
    }

    private static bool Placeholder(string value) =>
        value.Contains(Marker, StringComparison.Ordinal) || value.Contains('{') || value.Contains('<') ||
        value.Contains("example", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("changeme", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("your", StringComparison.OrdinalIgnoreCase) ||
        value.All(character => character is 'x' or 'X' or '*' or '.');
}
