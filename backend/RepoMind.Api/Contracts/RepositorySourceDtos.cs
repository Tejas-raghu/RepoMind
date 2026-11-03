namespace RepoMind.Api.Contracts;

public sealed record IndexedFile(string Path);
public sealed record SourceLine(int Number, string? Content, string Status);
public sealed record SourceRange(int StartLine, int EndLine);
public sealed record IndexedFileContent(
    string Path, bool Complete, string Fidelity, IReadOnlyList<SourceLine> Lines,
    IReadOnlyList<SourceRange> CoveredRanges, IReadOnlyList<SourceRange> GapRanges,
    IReadOnlyList<SourceRange> ConflictRanges);
