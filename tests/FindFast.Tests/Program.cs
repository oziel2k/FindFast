using FindFast.Core;
using Xunit;
using System.IO.Compression;
using System.Text.Json;
using System.Net.Sockets;
using System.Text;

public sealed class FindFastTests
{

[Fact] public Task TestTextIndex()
{
    Equal(new[] { "abc", "bcd" }, TextIndex.Trigrams("abcd").ToArray());
    Equal((2, 2), TextIndex.OffsetToPosition(TextIndex.LineStarts("one\ntwo"), 5));
    return Task.CompletedTask;
}

[Fact] public async Task TestLifecycle()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.cs"), "alpha\nneedle here\nomega");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "ignored.bin"), "x\0y");
    var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root, Name = "sample" });
    Equal("ready", root.State); Equal(1, root.FileCount);
    var response = service.SearchText(new SearchOptions { Query = "needle", RootIds = [root.RootId] });
    Equal(1, response.Matches.Count); Equal(2, response.Matches[0].Line); Equal("a.cs", response.Matches[0].Path);
    var read = service.FileRead(root.RootId, "a.cs", 2, 2); Equal("needle here", read.Lines.Single());
    var files = service.FilesFind([root.RootId], "**/*.cs", null, 10, null, out var cursor); Equal(1, files.Count); Equal(null, cursor);
    var reopened = await FindFastService.OpenAsync(fixture.Data);
    Equal(1, reopened.SearchText(new SearchOptions { Query = "needle" }).Matches.Count);
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.cs"), "changed");
    await reopened.IndexUpdateAsync(root.RootId, false);
    Equal(0, reopened.SearchText(new SearchOptions { Query = "needle" }).Matches.Count);
    reopened.RootRemove(root.RootId); Equal(0, reopened.RootsList().Count);
}

[Fact] public async Task TestPagination()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "many.txt"), "hit hit hit hit");
    var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var first = service.SearchText(new SearchOptions { Query = "hit", MaxResults = 2, MaxResultsPerFile = 10 });
    Equal(2, first.Matches.Count); True(first.Truncated); True(first.NextCursor is not null);
    var second = service.SearchText(new SearchOptions { Query = "hit", MaxResults = 2, MaxResultsPerFile = 10, Cursor = first.NextCursor });
    Equal(2, second.Matches.Count);
}

[Fact] public async Task TestTraversal()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "safe");
    var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    Throws<UnauthorizedAccessException>(() => service.FileRead(root.RootId, "../secret.txt", 1, 2));
}

[Fact] public async Task TestGitIgnore()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), "*.log\n");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "hidden.log"), "secret needle");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "visible.txt"), "public needle");
    var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var found = service.SearchText(new SearchOptions { Query = "needle", RootIds = [root.RootId] });
    Equal(1, found.Matches.Count); Equal("visible.txt", found.Matches[0].Path);
}

[Fact] public async Task TestGitIgnoreAdvanced()
{
    using var fixture = new Fixture();
    Directory.CreateDirectory(Path.Combine(fixture.Root, "nested"));
    Directory.CreateDirectory(Path.Combine(fixture.Root, "other"));
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), "*.log\n!keep.log\n/root.txt\n");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "nested", ".gitignore"), "*.tmp\n");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "drop.log"), "needle");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "keep.log"), "needle");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "root.txt"), "needle");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "other", "root.txt"), "needle");
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "nested", "drop.tmp"), "needle");
    using var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var paths = service.SearchText(new SearchOptions { Query = "needle" }).Matches.Select(x => x.Path).Order().ToArray();
    Equal(new[] { "keep.log", "other/root.txt" }, paths);
}

[Fact] public async Task TestCancellation()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), new string('x', 100_000));
    var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Throws<OperationCanceledException>(() => service.SearchText(new SearchOptions { Query = "not-found" }, cancellation.Token));
}

[Fact] public async Task TestCancelledUpdate()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "stable");
    var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var version = root.Version;
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await service.IndexUpdateAsync(root.RootId, false, cancellation.Token); }
    catch (OperationCanceledException) { }
    var status = service.IndexStatus(root.RootId);
    Equal("ready", status.State); Equal(version, status.Version);
    Equal(1, service.SearchText(new SearchOptions { Query = "stable" }).Matches.Count);
}

[Fact] public async Task TestDeterministicPagination()
{
    using var first = new Fixture();
    using var second = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(first.Root, "z.txt"), "hit");
    await File.WriteAllTextAsync(Path.Combine(second.Root, "a.txt"), "hit");
    var service = await FindFastService.OpenAsync(first.Data);
    await service.RootAddAsync(new RootAddOptions { Path = first.Root, Name = "z-root" });
    // Put the second snapshot in the same persistent catalog, then reopen it.
    await service.RootAddAsync(new RootAddOptions { Path = second.Root, Name = "a-root" });
    var page1 = service.SearchText(new SearchOptions { Query = "hit", MaxResults = 1, MaxResultsPerFile = 10 });
    Equal("a-root", page1.Matches.Single().RootId); True(page1.NextCursor is not null);
    var page2 = service.SearchText(new SearchOptions { Query = "hit", MaxResults = 1, MaxResultsPerFile = 10, Cursor = page1.NextCursor });
    Equal("z-root", page2.Matches.Single().RootId); Equal(null, page2.NextCursor);
    var repeat = service.SearchText(new SearchOptions { Query = "hit", MaxResults = 1, MaxResultsPerFile = 10 });
    Equal(page1.Matches[0], repeat.Matches[0]);
    var files = service.FilesFind(null, null, null, 10, null, out _);
    Equal("a-root", files[0].RootId); Equal("z-root", files[1].RootId);
}

[Fact] public async Task TestPerFileLimit()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "hit hit hit");
    var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var result = service.SearchText(new SearchOptions { Query = "hit", MaxResults = 10, MaxResultsPerFile = 2 });
    Equal(2, result.Matches.Count); True(result.Truncated); Equal("per_file_limit", result.TruncationReason); Equal(null, result.NextCursor);
}

[Fact] public async Task TestRegex()
{
    Equal("class", FindFastService.RequiredRegexLiteral(@"class\s+([A-Z]\w+)"));
    Equal(null, FindFastService.RequiredRegexLiteral("cat|dog"));
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.cs"), "class Widget\ninterface Nope");
    var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var indexed = service.SearchRegex(new RegexSearchOptions { Pattern = @"class\s+([A-Z]\w+)" });
    Equal("trigram_then_regex", indexed.QueryPlan.Strategy); Equal("class Widget", indexed.Matches.Single().Match);
    var fallback = service.SearchRegex(new RegexSearchOptions { Pattern = @"\w+\s+Nope" });
    Equal("filtered_regex_scan", fallback.QueryPlan.Strategy); Equal(1, fallback.Matches.Count);
}

[Fact] public async Task TestWatcher()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "before");
    using var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "automatic-after");
    var found = false;
    for (var i = 0; i < 30 && !found; i++)
    {
        await Task.Delay(100);
        found = service.SearchText(new SearchOptions { Query = "automatic-after" }).Matches.Count == 1;
    }
    True(found);
}

[Fact] public async Task TestStableIds()
{
    using var fixture = new Fixture();
    var oldPath = Path.Combine(fixture.Root, "old.txt");
    await File.WriteAllTextAsync(oldPath, "same-content");
    using var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var first = await service.IndexUpdateAsync(root.RootId, false);
    var id = first.Files.Single().FileId;
    File.Move(oldPath, Path.Combine(fixture.Root, "new.txt"));
    var renamed = await service.IndexUpdateAsync(root.RootId, false);
    Equal(id, renamed.Files.Single().FileId);
    File.Delete(Path.Combine(fixture.Root, "new.txt"));
    var deleted = await service.IndexUpdateAsync(root.RootId, false);
    Equal(id, deleted.Tombstones.Single(x => x.FileId == id).FileId);
}

[Fact] public async Task TestCompaction()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.Root, "gone.txt"); await File.WriteAllTextAsync(path, "gone");
    using var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    File.Delete(path); Equal(1, (await service.IndexUpdateAsync(root.RootId, false)).Tombstones.Count);
    Equal(0, (await service.IndexUpdateAsync(root.RootId, true)).Tombstones.Count);
}

[Fact] public async Task TestMetrics()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "metric needle");
    using var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    service.SearchText(new SearchOptions { Query = "needle" });
    var metrics = service.GetMetrics();
    Equal(1L, metrics.IndexOperations); Equal(1L, metrics.SearchOperations); True(metrics.BytesIndexed > 0); Equal(1L, metrics.FilesIndexed);
}

[Fact] public async Task TestLargeChunks()
{
    using var fixture = new Fixture();
    var content = new string('x', 1024 * 1024 - 3) + "BOUNDARY-NEEDLE" + new string('y', 32);
    var path = Path.Combine(fixture.Root, "large.txt"); await File.WriteAllTextAsync(path, content);
    using var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    True((await service.IndexUpdateAsync(root.RootId, false)).Files.All(x => x.Content.Length == 0));
    Equal(1, service.SearchText(new SearchOptions { Query = "BOUNDARY-NEEDLE" }).Matches.Count);
}

[Fact] public async Task TestMcpHandler()
{
    using var fixture = new Fixture();
    using var service = await FindFastService.OpenAsync(fixture.Data);
    using var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}\n");
    using var output = new StringWriter(); using var errors = new StringWriter();
    await new McpServer(service, input, output, errors).RunAsync(CancellationToken.None);
    var text = output.ToString(); True(text.Contains("\"protocolVersion\"")); True(text.Contains("\"search_regex\"")); True(text.Contains("\"inputSchema\""));
}

[Fact] public async Task TestSegmentedLayoutAndAtomicStagingRecovery()
{
    using var fixture = new Fixture(); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "segment needle");
    string rootId;
    using (var service = await FindFastService.OpenAsync(fixture.Data)) rootId = (await service.RootAddAsync(new RootAddOptions { Path = fixture.Root })).RootId;
    var segmentRoot = Path.Combine(fixture.Data, rootId + ".segments");
    var version = Directory.GetDirectories(segmentRoot).Single(x => !Path.GetFileName(x).StartsWith(".staging-"));
    True(File.Exists(Path.Combine(version, "manifest.json"))); True(File.Exists(Path.Combine(version, "postings.json.gz")));
    True(Directory.GetFiles(Path.Combine(version, "content"), "*.gz").Length == 1);
    Directory.CreateDirectory(Path.Combine(segmentRoot, ".staging-interrupted"));
    using var reopened = await FindFastService.OpenAsync(fixture.Data);
    Equal(1, reopened.SearchText(new SearchOptions { Query = "needle" }).Matches.Count);
}

[Fact] public async Task TestLegacyMigration()
{
    using var fixture = new Fixture();
    var root = new RootDefinition { RootId = "legacy", Name = "legacy", Path = fixture.Root, State = "ready", Version = 1, FileCount = 1 };
    var content = "legacy needle"; var file = new IndexedFile { FileId = 1, Path = "a.txt", Size = content.Length, Modified = DateTimeOffset.UtcNow,
        Hash = TextIndex.Sha256(content), Content = content, LineStarts = TextIndex.LineStarts(content) };
    var snapshot = new RootSnapshot { Root = root, Files = [file], Trigrams = TextIndex.Trigrams(content).ToDictionary(x => x, _ => new List<int> { 1 }) };
    Directory.CreateDirectory(fixture.Data); var legacy = Path.Combine(fixture.Data, "legacy.snapshot.json.gz");
    await using (var output = File.Create(legacy)) await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        await JsonSerializer.SerializeAsync(gzip, snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var service = await FindFastService.OpenAsync(fixture.Data);
    Equal(1, service.SearchText(new SearchOptions { Query = "needle" }).Matches.Count);
    True(File.Exists(Path.Combine(fixture.Data, "legacy.current")));
}

[Fact] public async Task TestCorruptActiveSegmentFallsBack()
{
    using var fixture = new Fixture(); var source = Path.Combine(fixture.Root, "a.txt"); await File.WriteAllTextAsync(source, "version-one");
    string rootId;
    using (var service = await FindFastService.OpenAsync(fixture.Data))
    {
        rootId = (await service.RootAddAsync(new RootAddOptions { Path = fixture.Root })).RootId;
        await File.WriteAllTextAsync(source, "version-two"); await service.IndexUpdateAsync(rootId, false);
    }
    var pointer = Path.Combine(fixture.Data, rootId + ".current"); var active = (await File.ReadAllTextAsync(pointer)).Trim();
    await File.WriteAllTextAsync(Path.Combine(fixture.Data, rootId + ".segments", active, "postings.json.gz"), "corrupt");
    using var recovered = await FindFastService.OpenAsync(fixture.Data);
    Equal(1, recovered.SearchText(new SearchOptions { Query = "version-one" }).Matches.Count);
    True((await File.ReadAllTextAsync(pointer)).Trim() != active);
}

[Fact] public async Task TestStreamingRegexBoundaryAndWindowDiagnostic()
{
    using var fixture = new Fixture(); var content = new string('x', 65536 - 5) + "BOUNDARY-ABCDEF" + new string('y', 70000);
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "large.txt"), content);
    using var service = await FindFastService.OpenAsync(fixture.Data); await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var bounded = service.SearchRegex(new RegexSearchOptions { Pattern = "BOUNDARY-[A-Z]{6}" });
    Equal(1, bounded.Matches.Count); Equal(false, bounded.Truncated);
    var unbounded = service.SearchRegex(new RegexSearchOptions { Pattern = "BOUNDARY-[A-Z]+" });
    Equal(1, unbounded.Matches.Count); Equal(true, unbounded.Truncated); Equal("regex_window_limit", unbounded.TruncationReason); Equal(null, unbounded.NextCursor);
}

[Fact] public async Task TestHttpLoopbackLifecycle()
{
    using var fixture = new Fixture(); using var service = await FindFastService.OpenAsync(fixture.Data);
    var probe = new TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
    await using var host = new HttpMcpHost(service, $"http://127.0.0.1:{port}/"); host.Start(); using var client = new HttpClient();
    async Task<string> Post(string json) => await (await client.PostAsync($"http://127.0.0.1:{port}/", new StringContent(json + "\n", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync();
    True((await Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}" )).Contains("serverInfo"));
    True((await Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}" )).Contains("search_regex"));
    True((await Post("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"roots_list\",\"arguments\":{}}}" )).Contains("structuredContent"));
}

[Fact] public async Task TestAtomicPointerFaultInjection()
{
    using var fixture = new Fixture(); var store = new SnapshotStore(fixture.Data);
    RootSnapshot Snapshot(long version, string content) => new() { Root = new RootDefinition { RootId = "atomic", Name = "atomic", Path = fixture.Root, State = "ready", Version = version, FileCount = 1 },
        Files = [new IndexedFile { FileId = 1, Path = "a.txt", Size = content.Length, Modified = DateTimeOffset.UtcNow, Hash = TextIndex.Sha256(content), Content = content, LineStarts = TextIndex.LineStarts(content) }],
        Trigrams = TextIndex.Trigrams(content).ToDictionary(x => x, _ => new List<int> { 1 }) };
    await store.SaveAsync(Snapshot(1, "stable")); var pointer = Path.Combine(fixture.Data, "atomic.current"); var stablePointer = await File.ReadAllTextAsync(pointer);
    store.FaultInjector = phase => { if (phase == "before_pointer_publish") throw new IOException("injected"); };
    await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Snapshot(2, "partial")));
    Equal(stablePointer, await File.ReadAllTextAsync(pointer));
    store.FaultInjector = phase => { if (phase == "before_segment_publish") throw new IOException("injected-before-segment"); };
    await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Snapshot(3, "staging")));
    True(!Directory.EnumerateDirectories(Path.Combine(fixture.Data, "atomic.segments"), ".staging-*").Any());
    var loaded = await new SnapshotStore(fixture.Data).LoadAsync(); Equal(1L, loaded.Single().Root.Version);
}

[Fact] public async Task TestRootCatalogJsonCreationRestartAndRemoval()
{
    using var fixture = new Fixture(); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "catalog");
    string rootId;
    using (var service = await FindFastService.OpenAsync(fixture.Data))
    {
        var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root, Name = "Catalog Root", Include = ["**/*.txt"], Exclude = ["tmp/**"], RespectGitignore = false });
        rootId = root.RootId;
    }
    var catalogPath = Path.Combine(fixture.Data, "roots.json"); var json = await File.ReadAllTextAsync(catalogPath);
    True(json.Contains("\"root_id\"")); True(json.Contains(Path.GetFullPath(fixture.Root).Replace("\\", "\\\\"))); True(json.Contains("\"respect_gitignore\": false"));
    using (var reopened = await FindFastService.OpenAsync(fixture.Data)) { Equal(rootId, reopened.RootsList().Single().RootId); reopened.RootRemove(rootId); }
    var after = await File.ReadAllTextAsync(catalogPath); True(!after.Contains(rootId)); True(File.Exists(Path.Combine(fixture.Root, "a.txt")));
}

[Fact] public async Task TestCatalogWithoutIndexIsStaleAndRebuilds()
{
    using var fixture = new Fixture(); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "rebuild needle");
    string rootId;
    using (var service = await FindFastService.OpenAsync(fixture.Data)) rootId = (await service.RootAddAsync(new RootAddOptions { Path = fixture.Root })).RootId;
    File.Delete(Path.Combine(fixture.Data, rootId + ".current")); Directory.Delete(Path.Combine(fixture.Data, rootId + ".segments"), true);
    using var reopened = await FindFastService.OpenAsync(fixture.Data); Equal("stale", reopened.IndexStatus(rootId).State);
    await reopened.IndexUpdateAsync(rootId, true); Equal("ready", reopened.IndexStatus(rootId).State);
    Equal(1, reopened.SearchText(new SearchOptions { Query = "needle" }).Matches.Count);
}

[Fact] public async Task TestExistingSegmentsMigrateToRootCatalog()
{
    using var fixture = new Fixture(); var content = "migrated catalog";
    var snapshot = new RootSnapshot { Root = new RootDefinition { RootId = "old-install", Name = "Old", Path = fixture.Root, State = "ready", Version = 1, FileCount = 1 },
        Files = [new IndexedFile { FileId = 1, Path = "a.txt", Size = content.Length, Modified = DateTimeOffset.UtcNow, Hash = TextIndex.Sha256(content), Content = content, LineStarts = [0] }],
        Trigrams = TextIndex.Trigrams(content).ToDictionary(x => x, _ => new List<int> { 1 }) };
    await new SnapshotStore(fixture.Data).SaveAsync(snapshot); True(!File.Exists(Path.Combine(fixture.Data, "roots.json")));
    using var service = await FindFastService.OpenAsync(fixture.Data); Equal("old-install", service.RootsList().Single().RootId);
    True((await File.ReadAllTextAsync(Path.Combine(fixture.Data, "roots.json"))).Contains("old-install"));
}

static void Equal<T>(T expected, T actual)
{
    if (expected is Array ea && actual is Array aa)
    {
        if (!ea.Cast<object?>().SequenceEqual(aa.Cast<object?>())) throw new Exception($"Expected [{string.Join(',', ea.Cast<object?>())}], got [{string.Join(',', aa.Cast<object?>())}].");
        return;
    }
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void True(bool value) { if (!value) throw new Exception("Expected true."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class Fixture : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "findfast-tests-" + Guid.NewGuid().ToString("N"));
    public Fixture() { Root = Path.Combine(_base, "root"); Data = Path.Combine(_base, "data"); Directory.CreateDirectory(Root); }
    public string Root { get; }
    public string Data { get; }
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }
}
}
