using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FindFast.Core;

public sealed class FindFastService : IDisposable
{
    private static readonly HashSet<string> DefaultExcluded = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".findfast" };
    private const long MaxFileBytes = 64 * 1024 * 1024;
    private const int MaxQueryResults = 1000;
    private readonly SnapshotStore _store;
    private readonly RootCatalog _catalog;
    private readonly ConcurrentDictionary<string, RootSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _regexLru = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces = new(StringComparer.Ordinal);
    private readonly Timer _reconcileTimer;
    private long _indexOperations, _searchOperations, _bytesIndexed, _filesIndexed, _searchElapsedMilliseconds;
    private FindFastService(SnapshotStore store) { _store = store; _catalog = new RootCatalog(store.DataDirectory); _reconcileTimer = new Timer(_ => ReconcileAll(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)); }

    public static async Task<FindFastService> OpenAsync(string dataDirectory, CancellationToken cancellationToken = default)
    {
        var service = new FindFastService(new SnapshotStore(dataDirectory));
        var registered = await service._catalog.LoadAsync(cancellationToken);
        foreach (var root in registered)
        {
            var stale = root with { State = "stale", LastError = "Index is missing or unavailable." };
            service._snapshots[root.RootId] = new RootSnapshot { Root = stale }; service.StartWatcher(stale);
        }
        var migrated = false;
        foreach (var snapshot in await service._store.LoadAsync(cancellationToken))
        {
            service._snapshots[snapshot.Root.RootId] = snapshot; service.StartWatcher(snapshot.Root);
            if (!registered.Any(x => x.RootId == snapshot.Root.RootId)) { registered.Add(snapshot.Root); migrated = true; }
        }
        if (migrated) await service._catalog.SaveAsync(registered, cancellationToken);
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
            Include = options.Include?.ToList() ?? [], Exclude = options.Exclude?.ToList() ?? [], Extensions = NormalizeExtensions(options.Extensions), RespectGitignore = options.RespectGitignore };
        var placeholder = new RootSnapshot { Root = root };
        _snapshots[id] = placeholder;
        await SaveCatalogAsync(cancellationToken);
        try { var indexed = await IndexUpdateAsync(id, true, cancellationToken); StartWatcher(indexed.Root); return indexed.Root; }
        catch
        {
            var stale = root with { State = "stale", LastError = "Initial index build failed." };
            _snapshots[id] = new RootSnapshot { Root = stale }; await SaveCatalogAsync(CancellationToken.None); throw;
        }
    }

    public void RootRemove(string rootId)
    {
        if (!_snapshots.TryRemove(rootId, out _)) throw new KeyNotFoundException($"Unknown root: {rootId}");
        if (_watchers.TryRemove(rootId, out var watcher)) watcher.Dispose();
        if (_debounces.TryRemove(rootId, out var debounce)) debounce.Cancel();
        _store.Delete(rootId);
        SaveCatalogAsync(CancellationToken.None).GetAwaiter().GetResult();
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
            var oldByPath = previous.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var oldByHash = previous.Files.GroupBy(x => x.Hash).ToDictionary(x => x.Key, x => new Queue<IndexedFile>(x));
            var usedIds = new HashSet<int>();
            var nextId = previous.Files.Select(x => x.FileId).Concat(previous.Tombstones.Select(x => x.FileId)).DefaultIfEmpty().Max() + 1;
            foreach (var absolute in EnumerateFiles(previous.Root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(absolute);
                    if (info.Length > MaxFileBytes) continue;
                    string content = string.Empty; string hash; int[] lineStarts; IEnumerable<string> fileTrigrams; string? sourcePath = null;
                    if (info.Length > 1024 * 1024)
                    {
                        var probe = new byte[Math.Min(8192, (int)info.Length)];
                        await using (var input = File.OpenRead(absolute)) _ = await input.ReadAsync(probe, cancellationToken);
                        if (TextIndex.IsBinary(probe)) continue;
                        var analysis = await AnalyzeLargeFileAsync(absolute, cancellationToken);
                        hash = analysis.Hash; lineStarts = analysis.LineStarts; fileTrigrams = analysis.Trigrams; sourcePath = absolute;
                    }
                    else
                    {
                        var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
                        if (TextIndex.IsBinary(bytes)) continue;
                        content = Decode(bytes);
                        hash = TextIndex.Sha256(content); lineStarts = TextIndex.LineStarts(content); fileTrigrams = TextIndex.Trigrams(content);
                    }
                    var relative = Path.GetRelativePath(previous.Root.Path, absolute).Replace('\\', '/');
                    var fileId = oldByPath.TryGetValue(relative, out var old) ? old.FileId : 0;
                    if (fileId == 0 && oldByHash.TryGetValue(hash, out var sameContent))
                        while (sameContent.TryDequeue(out var renamed)) if (!usedIds.Contains(renamed.FileId)) { fileId = renamed.FileId; break; }
                    if (fileId == 0) fileId = nextId++;
                    usedIds.Add(fileId);
                    var indexed = new IndexedFile { FileId = fileId, Path = relative, Size = info.Length,
                        Modified = info.LastWriteTimeUtc, Hash = hash, Content = content, LineStarts = lineStarts, SourcePath = sourcePath ?? absolute };
                    files.Add(indexed);
                    foreach (var trigram in fileTrigrams)
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
            var retainedTombstones = full ? Enumerable.Empty<FileTombstone>() : previous.Tombstones;
            var tombstones = retainedTombstones.Concat(previous.Files.Where(x => !usedIds.Contains(x.FileId))
                .Select(x => new FileTombstone(x.FileId, x.Path, root.Version))).GroupBy(x => x.FileId).Select(x => x.Last()).ToList();
            var snapshot = new RootSnapshot { Root = root, Files = files, Trigrams = postings, Tombstones = tombstones };
            await _store.SaveAsync(snapshot, cancellationToken);
            var published = snapshot with { Files = files.Select(x => x with { Content = string.Empty }).ToList() };
            _snapshots[rootId] = published;
            await SaveCatalogAsync(cancellationToken);
            Interlocked.Increment(ref _indexOperations);
            Interlocked.Add(ref _bytesIndexed, files.Sum(x => x.Size));
            Interlocked.Add(ref _filesIndexed, files.Count);
            if (full) _store.Compact(rootId);
            return published;
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
                    var offsets = _store.FindLiteralAsync(snapshot.Root.RootId, snapshot.Root.Version, file.FileId, options.Query,
                        options.CaseSensitive, options.WholeWord, maxPerFile + 1, token).GetAwaiter().GetResult();
                    foreach (var occurrence in offsets)
                    {
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
                        matches.Add(CreateStreamingMatch(snapshot, file, occurrence.Offset, occurrence.Value, context, token));
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
        Interlocked.Increment(ref _searchOperations); Interlocked.Add(ref _searchElapsedMilliseconds, stopwatch.ElapsedMilliseconds);
        if (perFileSuppressed)
            return Response(snapshots, strategy, candidatesTotal, matches, true, "per_file_limit", null, stopwatch.ElapsedMilliseconds);
        var cursor = hasNextPage ? EncodeCursor(offset + matches.Count) : null;
        return Response(snapshots, strategy, candidatesTotal, matches, hasNextPage, hasNextPage ? "result_limit" : null, cursor, stopwatch.ElapsedMilliseconds);
    }

    public SearchResponse SearchRegex(RegexSearchOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.Pattern) || options.Pattern.Length > 4096) throw new ArgumentException("Pattern must contain 1 to 4096 characters.");
        var stopwatch = Stopwatch.StartNew();
        var snapshots = SelectSnapshots(options.RootIds);
        var maxResults = Math.Clamp(options.MaxResults, 1, MaxQueryResults);
        var maxPerFile = Math.Clamp(options.MaxResultsPerFile, 1, MaxQueryResults);
        var offset = DecodeCursor(options.Cursor);
        var literal = RequiredRegexLiteral(options.Pattern);
        var strategy = literal is { Length: >= 3 } && options.CaseSensitive ? "trigram_then_regex" : "filtered_regex_scan";
        var regex = GetRegex(options.Pattern, options.CaseSensitive, Math.Clamp(options.RegexTimeoutMs, 1, 5000));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(options.TimeoutMs, 1, 60_000));
        var matches = new List<SearchMatch>();
        var seen = 0; var candidatesTotal = 0; var hasMore = false; var suppressed = false; var windowLimited = false;
        try
        {
            foreach (var snapshot in snapshots)
            {
                IEnumerable<IndexedFile> candidates = snapshot.Files;
                if (strategy == "trigram_then_regex") candidates = TrigramCandidates(snapshot, literal!);
                var files = FilterPath(candidates, options.PathGlob).OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.FileId).ToArray();
                candidatesTotal += files.Length;
                foreach (var file in files)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (file.Size > 65536 && HasUnboundedRegex(options.Pattern)) windowLimited = true;
                    var perFile = 0;
                    var occurrences = _store.FindRegexAsync(snapshot.Root.RootId, snapshot.Root.Version, file.FileId, regex, maxPerFile + 1, 16384, timeout.Token).GetAwaiter().GetResult();
                    foreach (var match in occurrences)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        if (perFile == maxPerFile) { suppressed = true; break; }
                        perFile++;
                        if (seen++ < offset) continue;
                        if (matches.Count == maxResults) { hasMore = true; break; }
                        matches.Add(CreateStreamingMatch(snapshot, file, match.Offset, match.Value, Math.Clamp(options.ContextLines, 0, 20), timeout.Token));
                    }
                    if (hasMore) break;
                }
                if (hasMore) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Response(snapshots, strategy, candidatesTotal, matches, true, "timeout", null, stopwatch.ElapsedMilliseconds); }
        catch (RegexMatchTimeoutException)
        { return Response(snapshots, strategy, candidatesTotal, matches, true, "regex_timeout", null, stopwatch.ElapsedMilliseconds); }
        if (windowLimited) return Response(snapshots, "bounded_streaming_regex", candidatesTotal, matches, true, "regex_window_limit", null, stopwatch.ElapsedMilliseconds);
        if (suppressed) return Response(snapshots, strategy, candidatesTotal, matches, true, "per_file_limit", null, stopwatch.ElapsedMilliseconds);
        return Response(snapshots, strategy, candidatesTotal, matches, hasMore, hasMore ? "result_limit" : null,
            hasMore ? EncodeCursor(offset + matches.Count) : null, stopwatch.ElapsedMilliseconds);
    }

    public static string? RequiredRegexLiteral(string pattern)
    {
        // Conservative proof: a literal prefix is mandatory when the expression has no
        // top-level/inner alternation and begins with literals (anchors are ignored).
        if (pattern.Contains('|')) return null;
        var value = new StringBuilder();
        var i = pattern.StartsWith('^') ? 1 : 0;
        for (; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (++i >= pattern.Length) return null;
                var escaped = pattern[i];
                if ("\\.^$|?*+()[]{}".Contains(escaped)) value.Append(escaped); else break;
            }
            else if (".^$?*+()[]{}".Contains(c)) break;
            else value.Append(c);
        }
        return value.Length == 0 ? null : value.ToString();
    }

    private Regex GetRegex(string pattern, bool caseSensitive, int timeoutMs)
    {
        var key = $"{caseSensitive}:{timeoutMs}:{pattern}";
        if (_regexCache.TryGetValue(key, out var cached)) return cached;
        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { cached = new Regex(pattern, options | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (NotSupportedException) { cached = new Regex(pattern, options, TimeSpan.FromMilliseconds(timeoutMs)); }
        _regexCache[key] = cached; _regexLru.Enqueue(key);
        while (_regexCache.Count > 128 && _regexLru.TryDequeue(out var old)) _regexCache.TryRemove(old, out _);
        return cached;
    }
    private static bool HasUnboundedRegex(string pattern) => Regex.IsMatch(pattern, @"(?<!\\)(?:\*|\+|\{\d+,\})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

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
        var lines = _store.ReadLinesAsync(rootId, snapshot.Root.Version, file.FileId, startLine, cappedEnd - startLine + 1, cancellationToken).GetAwaiter().GetResult();
        return new(rootId, file.Path, startLine, startLine + Math.Max(0, lines.Length - 1), lines, cappedEnd < endLine, snapshot.Root.Version);
    }

    public MetricsSnapshot GetMetrics() => new(Interlocked.Read(ref _indexOperations), Interlocked.Read(ref _searchOperations),
        Interlocked.Read(ref _bytesIndexed), Interlocked.Read(ref _filesIndexed), Interlocked.Read(ref _searchElapsedMilliseconds));

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
                var relativeDir = Path.GetRelativePath(root.Path, dir).Replace('\\', '/') + "/";
                if (MatchesAny(root.Exclude, relativeDir) || (root.RespectGitignore && IsGitIgnored(root.Path, relativeDir))) continue;
                pending.Push(dir);
            }
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root.Path, file).Replace('\\', '/');
                if (MatchesAny(root.Exclude, relative) || (root.RespectGitignore && IsGitIgnored(root.Path, relative))) continue;
                if (root.Include.Count > 0 && !MatchesAny(root.Include, relative)) continue;
                if (root.Extensions.Count > 0 && !root.Extensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }
    private static bool IsGitIgnored(string root, string relative)
    {
        var ignored = false;
        var relativeDirectory = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var directories = new List<string> { string.Empty };
        var cursor = string.Empty;
        foreach (var part in relativeDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = cursor.Length == 0 ? part : cursor + "/" + part;
            directories.Add(cursor);
        }
        foreach (var baseDirectory in directories)
        {
            var ignoreFile = Path.Combine(root, baseDirectory.Replace('/', Path.DirectorySeparatorChar), ".gitignore");
            if (!File.Exists(ignoreFile)) continue;
            var local = baseDirectory.Length == 0 ? relative : relative.StartsWith(baseDirectory + "/", StringComparison.Ordinal) ? relative[(baseDirectory.Length + 1)..] : relative;
            foreach (var raw in File.ReadLines(ignoreFile))
            {
                var pattern = raw.TrimEnd();
                if (pattern.Length == 0 || pattern[0] == '#') continue;
                var negated = pattern[0] == '!'; if (negated) pattern = pattern[1..];
                if (pattern.Length == 0) continue;
                var directoryOnly = pattern.EndsWith('/'); pattern = pattern.TrimEnd('/');
                var anchored = pattern.StartsWith('/'); pattern = pattern.TrimStart('/');
                var targetPattern = anchored || pattern.Contains('/') ? pattern : "**/" + pattern;
                if (directoryOnly) targetPattern += "/**";
                if (Regex.IsMatch(local, TextIndex.GlobToRegex(targetPattern), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) ignored = !negated;
            }
        }
        return ignored;
    }
    private static bool MatchesAny(IEnumerable<string> globs, string path) => globs.Any(glob => Regex.IsMatch(path, TextIndex.GlobToRegex(glob), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));
    public static List<string> NormalizeExtensions(IEnumerable<string>? extensions)
    {
        if (extensions is null) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extensions)
        {
            var value = raw.Trim();
            if (value.Length == 0 || value is "." or ".." || value.Contains('/') || value.Contains('\\') || value.IndexOfAny(['*', '?', '[', ']']) >= 0)
                throw new ArgumentException($"Invalid extension: '{raw}'. Use values such as 'cs' or '.cs'.");
            value = value.TrimStart('.');
            if (value.Length == 0 || value.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-'))
                throw new ArgumentException($"Invalid extension: '{raw}'. Use values such as 'cs' or '.cs'.");
            result.Add("." + value.ToLowerInvariant());
        }
        return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    private static async Task<(string Hash, int[] LineStarts, string[] Trigrams)> AnalyzeLargeFileAsync(string path, CancellationToken token)
    {
        string hash;
        await using (var bytes = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true))
            hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(bytes, token));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 65536);
        var buffer = new char[65536]; var carry = string.Empty; var offset = 0;
        var starts = new List<int> { 0 }; var trigrams = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token); if (read == 0) break;
            for (var i = 0; i < read; i++) if (buffer[i] == '\n') starts.Add(offset + i + 1);
            var window = carry + new string(buffer, 0, read);
            foreach (var trigram in TextIndex.Trigrams(window)) trigrams.Add(trigram);
            carry = window.Length <= 2 ? window : window[^2..]; offset += read;
        }
        return (hash, [.. starts], [.. trigrams]);
    }
    private static SearchMatch CreateMatch(string rootId, IndexedFile file, string content, int offset, int length, int context)
    {
        var (line, column) = TextIndex.OffsetToPosition(file.LineStarts, offset);
        var lines = SplitLines(content);
        var before = lines.Skip(Math.Max(0, line - 1 - context)).Take(Math.Min(context, line - 1)).ToArray();
        var text = lines.ElementAtOrDefault(line - 1) ?? string.Empty;
        var after = lines.Skip(line).Take(context).ToArray();
        return new(rootId, file.Path, line, column, content.Substring(offset, length), before, text, after);
    }
    private SearchMatch CreateStreamingMatch(RootSnapshot snapshot, IndexedFile file, int offset, string match, int context, CancellationToken token)
    {
        var (line, column) = TextIndex.OffsetToPosition(file.LineStarts, offset); var first = Math.Max(1, line - context);
        var lines = _store.ReadLinesAsync(snapshot.Root.RootId, snapshot.Root.Version, file.FileId, first, context * 2 + 1, token).GetAwaiter().GetResult();
        var current = line - first; var before = lines.Take(current).ToArray(); var text = lines.ElementAtOrDefault(current) ?? string.Empty;
        var after = lines.Skip(current + 1).Take(context).ToArray();
        return new(snapshot.Root.RootId, file.Path, line, column, match, before, text, after);
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
    private Task SaveCatalogAsync(CancellationToken token) => _catalog.SaveAsync(_snapshots.Values.Select(x => x.Root), token);

    private void StartWatcher(RootDefinition root)
    {
        if (!Directory.Exists(root.Path) || _watchers.ContainsKey(root.RootId)) return;
        var watcher = new FileSystemWatcher(root.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler changed = (_, _) => Debounce(root.RootId);
        RenamedEventHandler renamed = (_, _) => Debounce(root.RootId);
        watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed; watcher.Renamed += renamed;
        watcher.Error += (_, _) => Debounce(root.RootId);
        watcher.EnableRaisingEvents = true;
        if (!_watchers.TryAdd(root.RootId, watcher)) watcher.Dispose();
    }

    private void Debounce(string rootId)
    {
        var next = new CancellationTokenSource();
        var previous = _debounces.AddOrUpdate(rootId, next, (_, old) => { old.Cancel(); old.Dispose(); return next; });
        _ = previous;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500, next.Token); await IndexUpdateAsync(rootId, false, next.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or KeyNotFoundException or IOException) { }
            finally { _debounces.TryRemove(new KeyValuePair<string, CancellationTokenSource>(rootId, next)); next.Dispose(); }
        });
    }

    private void ReconcileAll()
    {
        foreach (var rootId in _snapshots.Keys) Debounce(rootId);
    }

    public void Dispose()
    {
        _reconcileTimer.Dispose();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        foreach (var cancellation in _debounces.Values) { cancellation.Cancel(); cancellation.Dispose(); }
        _watchers.Clear(); _debounces.Clear();
    }
}
