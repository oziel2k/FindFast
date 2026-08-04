using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FindFast.Core;

public sealed class SnapshotStore
{
    private readonly string _dataDirectory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public SnapshotStore(string dataDirectory) => _dataDirectory = Path.GetFullPath(dataDirectory);
    public string DataDirectory => _dataDirectory;
    public Action<string>? FaultInjector { get; set; }

    public async Task<IReadOnlyList<RootSnapshot>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);
        await ImportLegacyAsync(cancellationToken);
        var result = new List<RootSnapshot>();
        foreach (var pointer in Directory.EnumerateFiles(_dataDirectory, "*.current"))
        {
            var rootId = Path.GetFileNameWithoutExtension(pointer);
            var segmentsRoot = Path.Combine(_dataDirectory, rootId + ".segments");
            if (Directory.Exists(segmentsRoot))
                foreach (var abandoned in Directory.EnumerateDirectories(segmentsRoot, ".staging-*")) Directory.Delete(abandoned, true);
            try
            {
                var segment = (await File.ReadAllTextAsync(pointer, cancellationToken)).Trim();
                var directory = Path.Combine(_dataDirectory, rootId + ".segments", segment);
                var snapshot = await ReadSegmentAsync(directory, cancellationToken);
                if (snapshot is not null) result.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                var recovered = false;
                if (Directory.Exists(segmentsRoot))
                foreach (var candidate in Directory.EnumerateDirectories(segmentsRoot, "v*").Where(x => !x.Contains(".corrupt-", StringComparison.Ordinal)).OrderByDescending(x => x, StringComparer.Ordinal).ToArray())
                {
                    try
                    {
                        var snapshot = await ReadSegmentAsync(candidate, cancellationToken);
                        if (snapshot is null) continue;
                        var temporary = pointer + ".recover-" + Guid.NewGuid().ToString("N");
                        await File.WriteAllTextAsync(temporary, Path.GetFileName(candidate), cancellationToken); File.Move(temporary, pointer, true);
                        result.Add(snapshot); recovered = true; break;
                    }
                    catch (Exception inner) when (inner is IOException or InvalidDataException or JsonException)
                    { if (Directory.Exists(candidate)) Directory.Move(candidate, candidate + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
                }
                if (!recovered) File.Move(pointer, pointer + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
            }
        }
        return result;
    }

    public async Task SaveAsync(RootSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);
        var segmentsRoot = Path.Combine(_dataDirectory, snapshot.Root.RootId + ".segments");
        Directory.CreateDirectory(segmentsRoot);
        var segmentName = $"v{snapshot.Root.Version:D20}-{Guid.NewGuid():N}";
        var staging = Path.Combine(segmentsRoot, ".staging-" + segmentName);
        var published = Path.Combine(segmentsRoot, segmentName);
        Directory.CreateDirectory(Path.Combine(staging, "content"));
        try
        {
            foreach (var file in snapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, "content", file.FileId + ".txt.gz");
                if (file.SourcePath is not null) await WriteGzipFileAsync(target, file.SourcePath, cancellationToken);
                else
                {
                    var content = file.Content.Length > 0 ? file.Content : ReadContent(snapshot.Root.RootId, snapshot.Root.Version, file.FileId);
                    await WriteGzipTextAsync(target, content, cancellationToken);
                }
            }
            var manifest = snapshot with { Files = snapshot.Files.Select(x => x with { Content = string.Empty }).ToList(), Trigrams = new(StringComparer.Ordinal) };
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, _json), cancellationToken);
            await WriteGzipTextAsync(Path.Combine(staging, "postings.json.gz"), JsonSerializer.Serialize(snapshot.Trigrams, _json), cancellationToken);
            FaultInjector?.Invoke("before_segment_publish");
            Directory.Move(staging, published);
            var pointer = Path.Combine(_dataDirectory, snapshot.Root.RootId + ".current");
            var temporaryPointer = pointer + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporaryPointer, segmentName, cancellationToken);
            FaultInjector?.Invoke("before_pointer_publish");
            File.Move(temporaryPointer, pointer, true);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public string ReadContent(string rootId, long version, int fileId)
    {
        var directory = ResolveVersionDirectory(rootId, version);
        var path = Path.Combine(directory, "content", fileId + ".txt.gz");
        using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    public async Task<string[]> ReadLinesAsync(string rootId, long version, int fileId, int startLine, int count, CancellationToken token = default)
    {
        await using var file = File.OpenRead(ContentPath(rootId, version, fileId)); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip); var result = new List<string>(); var line = 1;
        while (result.Count < count && await reader.ReadLineAsync(token) is { } value) { if (line++ >= startLine) result.Add(value); }
        return [.. result];
    }

    public async Task<IReadOnlyList<(int Offset, string Value)>> FindLiteralAsync(string rootId, long version, int fileId, string query,
        bool caseSensitive, bool wholeWord, int maxMatches, CancellationToken token)
    {
        await using var file = File.OpenRead(ContentPath(rootId, version, fileId)); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip); var buffer = new char[65536]; var carry = string.Empty; long consumed = 0; var last = -1;
        var overlap = Math.Min(65535, query.Length + 1); var foundResults = new List<(int, string)>();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token); var eof = read == 0;
            var window = carry + (eof ? string.Empty : new string(buffer, 0, read)); var baseOffset = consumed - carry.Length;
            var safeEnd = eof ? window.Length : Math.Max(0, window.Length - overlap); var at = 0;
            while (at <= window.Length - query.Length)
            {
                var found = window.IndexOf(query, at, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase); if (found < 0 || (!eof && found >= safeEnd)) break;
                var absolute = checked((int)(baseOffset + found)); at = found + Math.Max(1, query.Length);
                if (absolute <= last) continue;
                if (wholeWord && ((found > 0 && IsWordChar(window[found - 1])) || (found + query.Length < window.Length && IsWordChar(window[found + query.Length])))) continue;
                foundResults.Add((absolute, window.Substring(found, query.Length))); last = absolute;
                if (foundResults.Count >= maxMatches) return foundResults;
            }
            if (eof) break; consumed += read; carry = window.Length <= overlap ? window : window[^overlap..];
        }
        return foundResults;
    }

    public async Task<IReadOnlyList<(int Offset, string Value)>> FindRegexAsync(string rootId, long version, int fileId, Regex regex, int maxMatches, int overlap, CancellationToken token)
    {
        await using var file = File.OpenRead(ContentPath(rootId, version, fileId)); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip); var buffer = new char[65536]; var carry = string.Empty; long consumed = 0; var last = -1;
        var results = new List<(int, string)>(); overlap = Math.Clamp(overlap, 256, 32768);
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token); var eof = read == 0;
            var window = carry + (eof ? string.Empty : new string(buffer, 0, read)); var baseOffset = consumed - carry.Length;
            var safeEnd = eof ? window.Length : Math.Max(0, window.Length - overlap);
            foreach (Match match in regex.Matches(window))
            {
                if (!eof && match.Index >= safeEnd) break;
                if (!eof && match.Index + match.Length > safeEnd) continue;
                var absolute = checked((int)(baseOffset + match.Index)); if (absolute <= last) continue;
                results.Add((absolute, match.Value)); last = absolute; if (results.Count >= maxMatches) return results;
            }
            if (eof) break; consumed += read; carry = window.Length <= overlap ? window : window[^overlap..];
        }
        return results;
    }

    private static bool IsWordChar(char value) => char.IsLetterOrDigit(value) || value == '_';
    private string ContentPath(string rootId, long version, int fileId) => Path.Combine(ResolveVersionDirectory(rootId, version), "content", fileId + ".txt.gz");

    public void Compact(string rootId, int retain = 2)
    {
        var root = Path.Combine(_dataDirectory, rootId + ".segments");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root).Where(x => !Path.GetFileName(x).StartsWith(".staging-", StringComparison.Ordinal))
                     .OrderByDescending(x => x, StringComparer.Ordinal).Skip(Math.Max(1, retain))) Directory.Delete(directory, true);
    }

    public void Delete(string rootId)
    {
        var pointer = Path.Combine(_dataDirectory, rootId + ".current"); if (File.Exists(pointer)) File.Delete(pointer);
        var segments = Path.Combine(_dataDirectory, rootId + ".segments"); if (Directory.Exists(segments)) Directory.Delete(segments, true);
        foreach (var legacy in Directory.EnumerateFiles(_dataDirectory, rootId + ".snapshot.json*")) File.Delete(legacy);
    }

    private string ResolveVersionDirectory(string rootId, long version)
    {
        var root = Path.Combine(_dataDirectory, rootId + ".segments");
        var prefix = $"v{version:D20}-";
        return Directory.EnumerateDirectories(root, prefix + "*").OrderByDescending(x => x, StringComparer.Ordinal).First();
    }

    private async Task<RootSnapshot?> ReadSegmentAsync(string directory, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<RootSnapshot>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), cancellationToken), _json);
        if (manifest is null || manifest.SchemaVersion != 1) return null;
        var postingsJson = await ReadGzipTextAsync(Path.Combine(directory, "postings.json.gz"), cancellationToken);
        var postings = JsonSerializer.Deserialize<Dictionary<string, List<int>>>(postingsJson, _json) ?? new(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            await using var blob = File.OpenRead(Path.Combine(directory, "content", file.FileId + ".txt.gz"));
            await using var gzip = new GZipStream(blob, CompressionMode.Decompress);
            await gzip.CopyToAsync(Stream.Null, cancellationToken);
        }
        return manifest with { Trigrams = postings };
    }

    private async Task ImportLegacyAsync(CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(_dataDirectory, "*.snapshot.json*").Where(x => !x.Contains(".migrated", StringComparison.Ordinal) && !x.Contains(".corrupt-", StringComparison.Ordinal)).ToArray())
        {
            try
            {
                await using var stream = File.OpenRead(path);
                await using Stream payload = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(stream, CompressionMode.Decompress) : stream;
                var snapshot = await JsonSerializer.DeserializeAsync<RootSnapshot>(payload, _json, cancellationToken);
                if (snapshot is null) continue;
                await SaveAsync(snapshot, cancellationToken); File.Move(path, path + ".migrated", true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            { File.Move(path, path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true); }
        }
    }

    private static async Task WriteGzipTextAsync(string path, string value, CancellationToken token)
    { await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true); await using var gzip = new GZipStream(file, CompressionLevel.Fastest); await using var writer = new StreamWriter(gzip); await writer.WriteAsync(value.AsMemory(), token); }
    private static async Task<string> ReadGzipTextAsync(string path, CancellationToken token)
    { await using var file = File.OpenRead(path); await using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new StreamReader(gzip); return await reader.ReadToEndAsync(token); }
    private static async Task WriteGzipFileAsync(string target, string source, CancellationToken token)
    { await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true); await using var gzip = new GZipStream(output, CompressionLevel.Fastest); await input.CopyToAsync(gzip, token); }
}
