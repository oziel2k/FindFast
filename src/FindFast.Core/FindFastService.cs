using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FindFast.Core;

public sealed class FindFastService : IDisposable
{
    private static readonly HashSet<string> DefaultExcluded = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".findfast" };

    /// <summary>Token that opts a root out of extension filtering entirely.</summary>
    public const string AllExtensions = "*";

    /// <summary>
    /// Extensions indexed when a root does not configure its own. An empty configuration means these, not
    /// "everything": an unfiltered root pulls in lockfiles, minified bundles, fixtures and generated output,
    /// which inflates the index and dilutes results. Use <see cref="AllExtensions"/> to index every text file.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExtensions =
    [
        ".adoc", ".astro", ".bash", ".bat", ".bicep", ".c", ".cc", ".cfg", ".clj", ".cljs", ".cmake", ".cmd",
        ".conf", ".cpp", ".cs", ".csproj", ".css", ".csv", ".cxx", ".dart", ".dockerfile", ".env", ".erl", ".ex",
        ".exs", ".f", ".f90", ".f95", ".fpw", ".fr2", ".fs", ".fsx", ".gitignore", ".go", ".gql", ".gradle",
        ".graphql", ".groovy", ".h", ".hpp", ".hrl", ".hs", ".htm", ".html", ".ini", ".java", ".jl", ".jrxml",
        ".js", ".json", ".jsonc", ".jsx", ".kt", ".kts", ".lb2", ".less", ".lua", ".m", ".markdown", ".md",
        ".mdx", ".mjs", ".mk", ".mm", ".mn2", ".php", ".pj2", ".pl", ".pm", ".prg", ".properties", ".proto",
        ".ps1", ".psd1", ".psm1", ".pxd", ".pxi", ".py", ".pyf", ".pyi", ".pyx", ".r", ".rb", ".rs", ".rst",
        ".sass", ".sc2", ".scala", ".scss", ".sh", ".sln", ".sql", ".svelte", ".swift", ".tex", ".tf", ".tfvars",
        ".toml", ".ts", ".tsv", ".tsx", ".txt", ".vb", ".vbproj", ".vc2", ".vue", ".xml", ".xsd", ".xsl",
        ".yaml", ".yml", ".zsh"
    ];
    private static readonly HashSet<string> DefaultExtensionSet = new(DefaultExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves the effective extension rule for a root: configured set, or the default when empty.</summary>
    public static bool ExtensionAllowed(RootDefinition root, string path)
    {
        if (root.Extensions.Count == 0) return DefaultExtensionSet.Contains(Path.GetExtension(path));
        if (root.Extensions.Contains(AllExtensions, StringComparer.Ordinal)) return true;
        return root.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
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
    private readonly ConcurrentDictionary<string, GitignoreFile> _gitignore = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private const int DebounceMilliseconds = 1000;
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

    /// <summary>
    /// Changes the filters of a registered root and reconciles the index. Omitted fields keep their current
    /// value. Removing a root and adding it back would work too, but would discard the index and every file id.
    /// The reconciliation is incremental: files that stopped qualifying are simply not enumerated any more,
    /// so they become tombstones and lose their postings, while newly qualifying files are read as dirty.
    /// </summary>
    public async Task<RootDefinition> RootUpdateAsync(string rootId, RootUpdateOptions options, CancellationToken cancellationToken = default)
    {
        var previous = GetSnapshot(rootId);
        var root = previous.Root with
        {
            Include = options.Include?.ToList() ?? previous.Root.Include,
            Exclude = options.Exclude?.ToList() ?? previous.Root.Exclude,
            Extensions = options.Extensions is null ? previous.Root.Extensions : NormalizeExtensions(options.Extensions),
            RespectGitignore = options.RespectGitignore ?? previous.Root.RespectGitignore
        };
        _snapshots[rootId] = previous with { Root = root };
        await SaveCatalogAsync(cancellationToken);
        return (await IndexUpdateAsync(rootId, false, cancellationToken)).Root;
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
        var gate = _locks.GetOrAdd(rootId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = GetSnapshot(rootId);
            var oldByPath = previous.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

            // Classification pass: stat only. An unchanged file keeps its record, its postings and its
            // already compressed blob, so the cost of an update tracks what changed, not the root size.
            var retained = new List<IndexedFile>();
            var retainedIds = new HashSet<int>();
            var dirty = new List<(string Absolute, string Relative, FileInfo Info)>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var absolute in EnumerateFiles(previous.Root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info;
                try { info = new FileInfo(absolute); if (!info.Exists) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                var relativePath = Path.GetRelativePath(previous.Root.Path, absolute).Replace('\\', '/');
                if (!seenPaths.Add(relativePath)) continue;
                if (!full && oldByPath.TryGetValue(relativePath, out var unchanged)
                    && unchanged.Size == info.Length && unchanged.Modified.UtcDateTime == info.LastWriteTimeUtc)
                {
                    retained.Add(unchanged with { Content = string.Empty, SourcePath = absolute });
                    retainedIds.Add(unchanged.FileId);
                }
                else dirty.Add((absolute, relativePath, info));
            }
            var removed = previous.Files.Where(x => !seenPaths.Contains(x.Path)).ToArray();

            // Nothing moved: no segment, no version bump, no catalog write. This is what makes the
            // periodic reconciliation sweep cheap enough to keep running every five minutes.
            if (!full && dirty.Count == 0 && removed.Length == 0 && previous.Root.State == "ready") return previous;

            var files = new List<IndexedFile>(retained);
            var dirtyTrigrams = new List<(int FileId, IEnumerable<string> Trigrams)>();
            var removedByHash = removed.GroupBy(x => x.Hash).ToDictionary(x => x.Key, x => new Queue<IndexedFile>(x), StringComparer.Ordinal);
            // Ids of retained files are reserved before any other resolution, so a new file that happens to
            // share content with an existing one can never take an id that is still in use.
            var usedIds = new HashSet<int>(retainedIds);
            var nextId = previous.Files.Select(x => x.FileId).Concat(previous.Tombstones.Select(x => x.FileId)).DefaultIfEmpty().Max() + 1;
            long dirtyBytes = 0;
            foreach (var (absolute, relative, info) in dirty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
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
                    var fileId = 0;
                    if (oldByPath.TryGetValue(relative, out var old) && usedIds.Add(old.FileId)) fileId = old.FileId;
                    // A rename can only inherit the id of a path that disappeared this round.
                    if (fileId == 0 && removedByHash.TryGetValue(hash, out var sameContent))
                        while (sameContent.TryDequeue(out var renamed)) if (usedIds.Add(renamed.FileId)) { fileId = renamed.FileId; break; }
                    if (fileId == 0) { fileId = nextId++; usedIds.Add(fileId); }
                    var indexed = new IndexedFile { FileId = fileId, Path = relative, Size = info.Length,
                        Modified = info.LastWriteTimeUtc, Hash = hash, Content = content, LineStarts = lineStarts, SourcePath = sourcePath ?? absolute };
                    files.Add(indexed);
                    dirtyTrigrams.Add((fileId, fileTrigrams));
                    dirtyBytes += info.Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (DecoderFallbackException) { }
            }

            // Postings are patched, not rebuilt. Every id that is not retained loses its entries: files that
            // disappeared, files being reindexed, and files that stopped qualifying (now binary or oversized).
            var deadIds = new HashSet<int>(previous.Files.Select(x => x.FileId).Where(id => !retainedIds.Contains(id)));
            var postings = new Dictionary<string, List<int>>(full ? 0 : previous.Trigrams.Count, StringComparer.Ordinal);
            // Lists reachable from the published snapshot are read lock-free by concurrent searches and must
            // never be mutated. An untouched posting is shared by reference; anything appended to is cloned first.
            var owned = new HashSet<string>(StringComparer.Ordinal);
            if (!full)
                foreach (var (trigram, ids) in previous.Trigrams)
                {
                    if (deadIds.Count == 0 || !ids.Any(deadIds.Contains)) { postings[trigram] = ids; continue; }
                    var kept = ids.Where(id => !deadIds.Contains(id)).ToList();
                    if (kept.Count > 0) { postings[trigram] = kept; owned.Add(trigram); }
                }
            foreach (var (fileId, trigrams) in dirtyTrigrams)
                foreach (var trigram in trigrams)
                {
                    if (!postings.TryGetValue(trigram, out var ids)) { postings[trigram] = [fileId]; owned.Add(trigram); continue; }
                    if (owned.Add(trigram)) postings[trigram] = ids = [.. ids];
                    ids.Add(fileId);
                }
            var root = previous.Root with { State = "ready", Version = previous.Root.Version + 1, LastUpdated = DateTimeOffset.UtcNow,
                LastError = null, FileCount = files.Count };
            var retainedTombstones = full ? Enumerable.Empty<FileTombstone>() : previous.Tombstones;
            var tombstones = retainedTombstones.Concat(previous.Files.Where(x => !usedIds.Contains(x.FileId))
                .Select(x => new FileTombstone(x.FileId, x.Path, root.Version))).GroupBy(x => x.FileId).Select(x => x.Last()).ToList();
            var snapshot = new RootSnapshot { Root = root, Files = files, Trigrams = postings, Tombstones = tombstones };
            var reuseFrom = previous.Files.Count > 0 ? previous.Root.Version : (long?)null;
            await _store.SaveAsync(snapshot, reuseFrom, retainedIds, cancellationToken);
            var published = snapshot with { Files = files.Select(x => x with { Content = string.Empty }).ToList() };
            _snapshots[rootId] = published;
            await SaveCatalogAsync(cancellationToken);
            Interlocked.Increment(ref _indexOperations);
            // Metrics report work actually performed, not the size of the root.
            Interlocked.Add(ref _bytesIndexed, dirtyBytes);
            Interlocked.Add(ref _filesIndexed, files.Count - retained.Count);
            // Every update publishes a segment, so every update has to compact. Retention is unchanged.
            _store.Compact(rootId);
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
    /// <summary>
    /// The predicate behind <see cref="EnumerateFiles"/>, reusable for a single path. Filesystem events are
    /// screened through it so that writes under <c>.git</c>, <c>obj</c> or a gitignored path — none of which
    /// can ever reach the index — do not schedule an update.
    /// </summary>
    private bool IsIndexable(RootDefinition root, string absolutePath, bool treatAsDirectory)
    {
        string relative;
        try { relative = Path.GetRelativePath(root.Path, absolutePath).Replace('\\', '/'); }
        catch (ArgumentException) { return false; }
        if (relative.Length == 0 || relative == "." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        var segments = relative.Split('/');
        var ancestors = treatAsDirectory ? segments.Length : segments.Length - 1;
        var cursor = string.Empty;
        for (var i = 0; i < ancestors; i++)
        {
            if (DefaultExcluded.Contains(segments[i])) return false;
            cursor = cursor.Length == 0 ? segments[i] : cursor + "/" + segments[i];
            var directory = cursor + "/";
            if (MatchesAny(root.Exclude, directory) || (root.RespectGitignore && IsGitIgnored(root.Path, directory))) return false;
        }
        if (treatAsDirectory) return true;
        if (MatchesAny(root.Exclude, relative) || (root.RespectGitignore && IsGitIgnored(root.Path, relative))) return false;
        if (root.Include.Count > 0 && !MatchesAny(root.Include, relative)) return false;
        if (!ExtensionAllowed(root, relative)) return false;
        return true;
    }

    private IEnumerable<string> EnumerateFiles(RootDefinition root)
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
                if (!ExtensionAllowed(root, relative)) continue;
                yield return file;
            }
        }
    }
    /// <summary>
    /// Parsed and compiled rules for one <c>.gitignore</c>, invalidated by its own mtime and length. Without
    /// this the rules were re-read and the globs recompiled once per candidate path; the static
    /// <see cref="Regex"/> cache holds fifteen entries, so any larger rule set recompiled on every call.
    /// </summary>
    private GitignoreRule[] GitignoreRules(string ignoreFile)
    {
        var info = new FileInfo(ignoreFile);
        var stamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        var length = info.Exists ? info.Length : -1;
        if (_gitignore.TryGetValue(ignoreFile, out var cached) && cached.Stamp == stamp && cached.Length == length) return cached.Rules;
        var rules = new List<GitignoreRule>();
        if (info.Exists)
            try
            {
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
                    rules.Add(new GitignoreRule(new Regex(TextIndex.GlobToRegex(targetPattern), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)), negated));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return cached?.Rules ?? []; }
        var entry = new GitignoreFile(stamp, length, [.. rules]);
        _gitignore[ignoreFile] = entry;
        return entry.Rules;
    }

    private bool IsGitIgnored(string root, string relative)
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
            var rules = GitignoreRules(Path.Combine(root, baseDirectory.Replace('/', Path.DirectorySeparatorChar), ".gitignore"));
            if (rules.Length == 0) continue;
            var local = baseDirectory.Length == 0 ? relative : relative.StartsWith(baseDirectory + "/", StringComparison.Ordinal) ? relative[(baseDirectory.Length + 1)..] : relative;
            foreach (var rule in rules) if (rule.Pattern.IsMatch(local)) ignored = !rule.Negated;
        }
        return ignored;
    }

    private sealed record GitignoreRule(Regex Pattern, bool Negated);
    private sealed record GitignoreFile(DateTime Stamp, long Length, GitignoreRule[] Rules);
    private static bool MatchesAny(IEnumerable<string> globs, string path) => globs.Any(glob => Regex.IsMatch(path, TextIndex.GlobToRegex(glob), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));
    public static List<string> NormalizeExtensions(IEnumerable<string>? extensions)
    {
        if (extensions is null) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extensions)
        {
            var value = raw.Trim();
            if (value == AllExtensions) { result.Add(AllExtensions); continue; }
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
        var rootId = root.RootId;
        var watcher = new FileSystemWatcher(root.Path) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler changed = (_, e) => { if (Interesting(rootId, e.FullPath)) Debounce(rootId); };
        RenamedEventHandler renamed = (_, e) => { if (Interesting(rootId, e.FullPath) || Interesting(rootId, e.OldFullPath)) Debounce(rootId); };
        watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed; watcher.Renamed += renamed;
        // An overflowed buffer means events were dropped; a reconciliation sweep is the only safe answer.
        watcher.Error += (_, _) => Debounce(rootId);
        watcher.EnableRaisingEvents = true;
        if (!_watchers.TryAdd(rootId, watcher)) watcher.Dispose();
    }

    private bool Interesting(string rootId, string absolutePath)
    {
        if (!_snapshots.TryGetValue(rootId, out var snapshot)) return false;
        // A deleted path cannot be stat-ed, so it is judged as a file. A deleted path that would not have
        // passed the filters was never indexed, so declining to schedule an update for it is correct.
        try { return IsIndexable(snapshot.Root, absolutePath, Directory.Exists(absolutePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private void Debounce(string rootId)
    {
        var next = new CancellationTokenSource();
        _debounces.AddOrUpdate(rootId, next, (_, old) =>
        {
            try { old.Cancel(); old.Dispose(); } catch (ObjectDisposedException) { }
            return next;
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMilliseconds, next.Token);
                // Only the waiting window is cancellable. Once indexing starts it runs to completion under the
                // shutdown token: a further event queues another cheap incremental pass instead of discarding
                // work already done, which is what previously kept a large root from ever converging.
                await IndexUpdateAsync(rootId, false, _shutdown.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or KeyNotFoundException or IOException or ObjectDisposedException) { }
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
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var cancellation in _debounces.Values)
            try { cancellation.Cancel(); cancellation.Dispose(); } catch (ObjectDisposedException) { }
        _watchers.Clear(); _debounces.Clear(); _gitignore.Clear();
        _shutdown.Dispose();
    }
}
