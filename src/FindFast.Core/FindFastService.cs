using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FindFast.Core;

public sealed class FindFastService
{
    private static readonly HashSet<string> DefaultExcluded = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".findfast" };
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxQueryResults = 1000;
    private readonly SnapshotStore _store;
    private readonly ConcurrentDictionary<string, RootSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private FindFastService(SnapshotStore store) => _store = store;

    public static async Task<FindFastService> OpenAsync(string dataDirectory, CancellationToken cancellationToken = default)
    {
        var service = new FindFastService(new SnapshotStore(dataDirectory));
        foreach (var snapshot in await service._store.LoadAsync(cancellationToken)) service._snapshots[snapshot.Root.RootId] = snapshot;
        return service;
    }

    public IReadOnlyList<RootDefinition> RootsList() => _snapshots.Values.Select(x => x.Root).OrderBy(x => x.Name).ToArray();

    public async Task<RootDefinition> RootAddAsync(RootAddOptions options, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(options.Path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Root does not exist: {path}");
        var existing = _snapshots.Values.FirstOrDefault(x => PathComparer.Equals(x.Root.Path, path));
        if (existing is not null) throw new InvalidOperationException($"Root is already registered as '{existing.Root.RootId}'.");
        var baseId = Slug(options.Name ?? new DirectoryInfo(path).Name);
        var id = baseId;
        for (var suffix = 2; _snapshots.ContainsKey(id); suffix++) id = baseId + "-" + suffix;
        var root = new RootDefinition { RootId = id, Name = options.Name ?? new DirectoryInfo(path).Name, Path = path,
            Type = Directory.Exists(Path.Combine(path, ".git")) ? "git_repository" : "directory",
            Include = options.Include?.ToList() ?? [], Exclude = options.Exclude?.ToList() ?? [], RespectGitignore = options.RespectGitignore };
        var placeholder = new RootSnapshot { Root = root };
        _snapshots[id] = placeholder;
        try { return (await IndexUpdateAsync(id, true, cancellationToken)).Root; }
        catch { _snapshots.TryRemove(id, out _); throw; }
    }

    public void RootRemove(string rootId)
    {
        if (!_snapshots.TryRemove(rootId, out _)) throw new KeyNotFoundException($"Unknown root: {rootId}");
        _store.Delete(rootId);
    }

    public RootDefinition IndexStatus(string rootId) => GetSnapshot(rootId).Root;

    public async Task<RootSnapshot> IndexUpdateAsync(string rootId, bool full, CancellationToken cancellationToken = default)
    {
        _ = full; // Current snapshot format rebuilds postings atomically for both modes.
        var gate = _locks.GetOrAdd(rootId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = GetSnapshot(rootId);
            var files = new List<IndexedFile>();
            var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var nextId = 1;
            foreach (var absolute in EnumerateFiles(previous.Root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(absolute);
                    if (info.Length > MaxFileBytes) continue;
                    var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
                    if (TextIndex.IsBinary(bytes)) continue;
                    var content = Decode(bytes);
                    var relative = Path.GetRelativePath(previous.Root.Path, absolute).Replace('\\', '/');
                    var indexed = new IndexedFile { FileId = nextId++, Path = relative, Size = info.Length,
                        Modified = info.LastWriteTimeUtc, Hash = TextIndex.Sha256(content), Content = content, LineStarts = TextIndex.LineStarts(content) };
                    files.Add(indexed);
                    foreach (var trigram in TextIndex.Trigrams(content))
                    {
                        if (!postings.TryGetValue(trigram, out var ids)) postings[trigram] = ids = [];
                        ids.Add(indexed.FileId);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (DecoderFallbackException) { }
            }
            var root = previous.Root with { State = "ready", Version = previous.Root.Version + 1, LastUpdated = DateTimeOffset.UtcNow,
                LastError = null, FileCount = files.Count };
            var snapshot = new RootSnapshot { Root = root, Files = files, Trigrams = postings };
            await _store.SaveAsync(snapshot, cancellationToken);
            _snapshots[rootId] = snapshot;
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The published snapshot is immutable from the updater's perspective. A failed
            // build leaves the last consistent version queryable and unchanged.
            _ = ex;
            throw;
        }
        finally { gate.Release(); }
    }

    public SearchResponse SearchText(SearchOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.Query)) throw new ArgumentException("Query must not be empty.");
        if (options.Query.Length > 4096) throw new ArgumentException("Query exceeds 4096 characters.");
        var maxResults = Math.Clamp(options.MaxResults, 1, MaxQueryResults);
        var maxPerFile = Math.Clamp(options.MaxResultsPerFile, 1, MaxQueryResults);
        var context = Math.Clamp(options.ContextLines, 0, 20);
        var offset = DecodeCursor(options.Cursor);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(options.TimeoutMs, 1, 60_000));
        var token = timeout.Token;
        var matches = new List<SearchMatch>();
        var eligibleSeen = 0;
        var candidatesTotal = 0;
        var hasNextPage = false;
        var perFileSuppressed = false;
        var snapshots = SelectSnapshots(options.RootIds);
        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var strategy = options.CaseSensitive && options.Query.Length >= 3 ? "trigram_then_verify" : "filtered_scan";
        try
        {
            foreach (var snapshot in snapshots)
            {
                token.ThrowIfCancellationRequested();
                IEnumerable<IndexedFile> candidates = snapshot.Files;
                if (strategy == "trigram_then_verify") candidates = TrigramCandidates(snapshot, options.Query);
                candidates = FilterPath(candidates, options.PathGlob);
                var materialized = candidates.OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.FileId).ToArray();
                candidatesTotal += materialized.Length;
                foreach (var file in materialized)
                {
                    token.ThrowIfCancellationRequested();
                    var perFile = 0;
                    var searchAt = 0;
                    while (searchAt <= file.Content.Length - options.Query.Length)
                    {
                        var found = file.Content.IndexOf(options.Query, searchAt, comparison);
                        if (found < 0) break;
                        searchAt = found + Math.Max(1, options.Query.Length);
                        if (options.WholeWord && !IsWholeWord(file.Content, found, options.Query.Length)) continue;
                        if (perFile == maxPerFile)
                        {
                            perFileSuppressed = true;
                            break;
                        }
                        perFile++;
                        if (eligibleSeen++ < offset) continue;
                        if (matches.Count == maxResults)
                        {
                            hasNextPage = true;
                            break;
                        }
                        matches.Add(CreateMatch(snapshot.Root.RootId, file, found, options.Query.Length, context));
                    }
                    if (hasNextPage) break;
                }
                if (hasNextPage) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Response(snapshots, strategy, candidatesTotal, matches, true, "timeout", null, stopwatch.ElapsedMilliseconds);
        }
        // A per-file cap deliberately removes later occurrences from the pageable universe.
        // Report that loss explicitly and do not imply they are reachable through a cursor.
        if (perFileSuppressed)
            return Response(snapshots, strategy, candidatesTotal, matches, true, "per_file_limit", null, stopwatch.ElapsedMilliseconds);
        var cursor = hasNextPage ? EncodeCursor(offset + matches.Count) : null;
        return Response(snapshots, strategy, candidatesTotal, matches, hasNextPage, hasNextPage ? "result_limit" : null, cursor, stopwatch.ElapsedMilliseconds);
    }

    public IReadOnlyList<FileResult> FilesFind(IReadOnlyList<string>? rootIds, string? pathGlob, string? query, int maxResults,
        string? cursor, out string? nextCursor, CancellationToken cancellationToken = default)
    {
        var offset = DecodeCursor(cursor);
        var limit = Math.Clamp(maxResults, 1, MaxQueryResults);
        var all = new List<FileResult>();
        foreach (var snapshot in SelectSnapshots(rootIds))
            foreach (var file in FilterPath(snapshot.Files, pathGlob))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(query) && !file.Path.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                all.Add(new(snapshot.Root.RootId, file.Path, file.Size, file.Modified));
            }
        var page = all.OrderBy(x => x.RootId, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal).Skip(offset).Take(limit).ToArray();
        nextCursor = offset + page.Length < all.Count ? EncodeCursor(offset + page.Length) : null;
        return page;
    }

    public FileReadResponse FileRead(string rootId, string relativePath, int startLine, int endLine, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = GetSnapshot(rootId);
        ValidateRelativePath(snapshot.Root.Path, relativePath);
        var normalized = relativePath.Replace('\\', '/');
        var file = snapshot.Files.FirstOrDefault(x => x.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("The file is not present in the current index.", relativePath);
        startLine = Math.Max(1, startLine);
        endLine = Math.Max(startLine, endLine);
        var cappedEnd = Math.Min(endLine, startLine + 499);
        var lines = SplitLines(file.Content).Skip(startLine - 1).Take(cappedEnd - startLine + 1).ToArray();
        return new(rootId, file.Path, startLine, startLine + Math.Max(0, lines.Length - 1), lines, cappedEnd < endLine, snapshot.Root.Version);
    }

    private RootSnapshot GetSnapshot(string rootId) => _snapshots.TryGetValue(rootId, out var value) ? value : throw new KeyNotFoundException($"Unknown root: {rootId}");
    private RootSnapshot[] SelectSnapshots(IReadOnlyList<string>? ids) => (ids is null or { Count: 0 }
        ? _snapshots.Values : ids.Select(GetSnapshot)).OrderBy(x => x.Root.RootId, StringComparer.Ordinal).ToArray();
    private static IEnumerable<IndexedFile> TrigramCandidates(RootSnapshot snapshot, string query)
    {
        List<int>? ids = null;
        foreach (var trigram in TextIndex.Trigrams(query))
        {
            if (!snapshot.Trigrams.TryGetValue(trigram, out var posting)) return [];
            ids = ids is null ? [.. posting] : ids.Intersect(posting).ToList();
            if (ids.Count == 0) return [];
        }
        return ids is null ? snapshot.Files : ids.Select(id => snapshot.FilesById[id]);
    }
    private static IEnumerable<IndexedFile> FilterPath(IEnumerable<IndexedFile> files, string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) return files;
        var regex = new Regex(TextIndex.GlobToRegex(glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        return files.Where(x => regex.IsMatch(x.Path));
    }
    private static IEnumerable<string> EnumerateFiles(RootDefinition root)
    {
        var effectiveExcludes = root.Exclude.Concat(root.RespectGitignore ? LoadGitIgnore(root.Path) : []).ToArray();
        var pending = new Stack<string>(); pending.Push(root.Path);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> dirs; IEnumerable<string> files;
            try { dirs = Directory.EnumerateDirectories(directory).ToArray(); files = Directory.EnumerateFiles(directory).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs)
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || DefaultExcluded.Contains(info.Name)) continue;
                if (MatchesAny(effectiveExcludes, Path.GetRelativePath(root.Path, dir).Replace('\\', '/') + "/")) continue;
                pending.Push(dir);
            }
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root.Path, file).Replace('\\', '/');
                if (MatchesAny(effectiveExcludes, relative)) continue;
                if (root.Include.Count > 0 && !MatchesAny(root.Include, relative)) continue;
                yield return file;
            }
        }
    }
    private static IEnumerable<string> LoadGitIgnore(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        if (!File.Exists(path)) yield break;
        foreach (var raw in File.ReadLines(path))
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || pattern[0] == '#' || pattern[0] == '!') continue;
            pattern = pattern.TrimStart('/');
            if (pattern.EndsWith('/')) pattern += "**";
            if (!pattern.Contains('/')) pattern = "**/" + pattern;
            yield return pattern;
        }
    }
    private static bool MatchesAny(IEnumerable<string> globs, string path) => globs.Any(glob => Regex.IsMatch(path, TextIndex.GlobToRegex(glob), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    private static SearchMatch CreateMatch(string rootId, IndexedFile file, int offset, int length, int context)
    {
        var (line, column) = TextIndex.OffsetToPosition(file.LineStarts, offset);
        var lines = SplitLines(file.Content);
        var before = lines.Skip(Math.Max(0, line - 1 - context)).Take(Math.Min(context, line - 1)).ToArray();
        var text = lines.ElementAtOrDefault(line - 1) ?? string.Empty;
        var after = lines.Skip(line).Take(context).ToArray();
        return new(rootId, file.Path, line, column, file.Content.Substring(offset, length), before, text, after);
    }
    private static string[] SplitLines(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    private static bool IsWholeWord(string text, int offset, int length) =>
        (offset == 0 || !IsWord(text[offset - 1])) && (offset + length == text.Length || !IsWord(text[offset + length]));
    private static bool IsWord(char value) => char.IsLetterOrDigit(value) || value == '_';
    private static SearchResponse Response(RootSnapshot[] snapshots, string strategy, int candidates, List<SearchMatch> matches,
        bool truncated, string? reason, string? cursor, long elapsed) => new(snapshots.Select(x => x.Root.Version).DefaultIfEmpty().Max(),
            snapshots.All(x => x.Root.State == "ready") ? "ready" : "stale", new(strategy, candidates), matches, truncated, reason, cursor, elapsed);
    private static int DecodeCursor(string? cursor)
    {
        if (cursor is null) return 0;
        try { return int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
        catch { throw new ArgumentException("Invalid cursor."); }
    }
    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));
    private static string Slug(string value)
    {
        var result = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(result) ? "root" : result;
    }
    private static void ValidateRelativePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new UnauthorizedAccessException("Only relative paths are accepted.");
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(canonicalRoot, PathComparison)) throw new UnauthorizedAccessException("Path escapes the registered root.");
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
