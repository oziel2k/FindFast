using System.Text.Json.Serialization;

namespace FindFast.Core;

public sealed record RootDefinition
{
    public required string RootId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Type { get; init; } = "directory";
    public List<string> Include { get; init; } = [];
    public List<string> Exclude { get; init; } = [];
    public bool RespectGitignore { get; init; } = true;
    public string State { get; set; } = "building";
    public long Version { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public string? LastError { get; set; }
    public int FileCount { get; set; }
}

public sealed record IndexedFile
{
    public required int FileId { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset Modified { get; init; }
    public required string Hash { get; init; }
    public required string Content { get; init; }
    public required int[] LineStarts { get; init; }
    [JsonIgnore] public string? SourcePath { get; init; }
}

public sealed record RootSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required RootDefinition Root { get; init; }
    public List<IndexedFile> Files { get; init; } = [];
    public Dictionary<string, List<int>> Trigrams { get; init; } = new(StringComparer.Ordinal);
    public List<FileTombstone> Tombstones { get; init; } = [];
    [JsonIgnore] public Dictionary<int, IndexedFile> FilesById => _filesById ??= Files.ToDictionary(f => f.FileId);
    private Dictionary<int, IndexedFile>? _filesById;
}
public sealed record FileTombstone(int FileId, string Path, long RemovedInVersion);

public sealed record SearchMatch(string RootId, string Path, int Line, int Column, string Match,
    IReadOnlyList<string> Before, string Text, IReadOnlyList<string> After);
public sealed record QueryPlan(string Strategy, int CandidateFiles);
public sealed record SearchResponse(long IndexVersion, string IndexState, QueryPlan QueryPlan,
    IReadOnlyList<SearchMatch> Matches, bool Truncated, string? TruncationReason, string? NextCursor, long ElapsedMs);
public sealed record FileResult(string RootId, string Path, long Size, DateTimeOffset Modified);
public sealed record FileReadResponse(string RootId, string Path, int StartLine, int EndLine,
    IReadOnlyList<string> Lines, bool Truncated, long IndexVersion);
public sealed record MetricsSnapshot(long IndexOperations, long SearchOperations, long BytesIndexed, long FilesIndexed, long SearchElapsedMilliseconds);

public sealed class RootAddOptions
{
    public required string Path { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string>? Include { get; init; }
    public IReadOnlyList<string>? Exclude { get; init; }
    public bool RespectGitignore { get; init; } = true;
}

public sealed class SearchOptions
{
    public required string Query { get; init; }
    public IReadOnlyList<string>? RootIds { get; init; }
    public string? PathGlob { get; init; }
    public bool CaseSensitive { get; init; } = true;
    public bool WholeWord { get; init; }
    public int ContextLines { get; init; } = 1;
    public int MaxResults { get; init; } = 100;
    public int MaxResultsPerFile { get; init; } = 25;
    public string? Cursor { get; init; }
    public int TimeoutMs { get; init; } = 5000;
}

public sealed class RegexSearchOptions
{
    public required string Pattern { get; init; }
    public IReadOnlyList<string>? RootIds { get; init; }
    public string? PathGlob { get; init; }
    public bool CaseSensitive { get; init; } = true;
    public int ContextLines { get; init; } = 1;
    public int MaxResults { get; init; } = 100;
    public int MaxResultsPerFile { get; init; } = 25;
    public string? Cursor { get; init; }
    public int TimeoutMs { get; init; } = 5000;
    public int RegexTimeoutMs { get; init; } = 250;
}
