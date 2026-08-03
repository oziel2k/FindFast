using FindFast.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("trigrams and offsets", TestTextIndex),
    ("add search read update persistence remove", TestLifecycle),
    ("pagination and limits", TestPagination),
    ("gitignore exclusion", TestGitIgnore),
    ("query cancellation", TestCancellation),
    ("cancelled update preserves snapshot", TestCancelledUpdate),
    ("deterministic multi-root pagination", TestDeterministicPagination),
    ("per-file truncation has no cursor", TestPerFileLimit),
    ("path traversal", TestTraversal)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
return failed;

static Task TestTextIndex()
{
    Equal(new[] { "abc", "bcd" }, TextIndex.Trigrams("abcd").ToArray());
    Equal((2, 2), TextIndex.OffsetToPosition(TextIndex.LineStarts("one\ntwo"), 5));
    return Task.CompletedTask;
}

static async Task TestLifecycle()
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

static async Task TestPagination()
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

static async Task TestTraversal()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "safe");
    var service = await FindFastService.OpenAsync(fixture.Data);
    var root = await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    Throws<UnauthorizedAccessException>(() => service.FileRead(root.RootId, "../secret.txt", 1, 2));
}

static async Task TestGitIgnore()
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

static async Task TestCancellation()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), new string('x', 100_000));
    var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Throws<OperationCanceledException>(() => service.SearchText(new SearchOptions { Query = "not-found" }, cancellation.Token));
}

static async Task TestCancelledUpdate()
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

static async Task TestDeterministicPagination()
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

static async Task TestPerFileLimit()
{
    using var fixture = new Fixture();
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "hit hit hit");
    var service = await FindFastService.OpenAsync(fixture.Data);
    await service.RootAddAsync(new RootAddOptions { Path = fixture.Root });
    var result = service.SearchText(new SearchOptions { Query = "hit", MaxResults = 10, MaxResultsPerFile = 2 });
    Equal(2, result.Matches.Count); True(result.Truncated); Equal("per_file_limit", result.TruncationReason); Equal(null, result.NextCursor);
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
