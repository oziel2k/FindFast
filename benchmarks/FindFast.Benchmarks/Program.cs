using System.Diagnostics;
using FindFast.Core;

var positional = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
var fileCount = positional.Length > 0 ? int.Parse(positional[0]) : 10_000;
var queries = positional.Length > 1 ? int.Parse(positional[1]) : 200;
var validate = args.Contains("--validate", StringComparer.OrdinalIgnoreCase);
if (fileCount < 1 || queries < 1) throw new ArgumentOutOfRangeException(nameof(args), "fileCount and queries must be positive.");
var workspace = Path.Combine(Path.GetTempPath(), "findfast-benchmark-" + Guid.NewGuid().ToString("N"));
var corpus = Path.Combine(workspace, "corpus"); var data = Path.Combine(workspace, "index");
Directory.CreateDirectory(corpus);
try
{
    var generation = Stopwatch.StartNew();
    Parallel.For(0, fileCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, i =>
    {
        var module = Path.Combine(corpus, $"module-{i % 32:D2}", "src"); Directory.CreateDirectory(module);
        var marker = i % 997 == 0 ? $"selective-needle-{i:D8}" : "ordinary text";
        var shortLiteral = i % 211 == 0 ? " xy " : "";
        File.WriteAllText(Path.Combine(module, $"file-{i:D8}.txt"), $"deterministic corpus row {i}\n{marker}{shortLiteral}\ncomponent value-{i % 100}");
    });
    var largeDirectory = Path.Combine(corpus, "large"); Directory.CreateDirectory(largeDirectory);
    for (var i = 0; i < 4; i++) File.WriteAllText(Path.Combine(largeDirectory, $"large-{i}.txt"), new string('x', 1024 * 1024 + i) + " LARGE-BOUNDARY-NEEDLE " + new string('y', 128 * 1024));
    var ignoredDirectory = Path.Combine(corpus, "ignored"); Directory.CreateDirectory(ignoredDirectory);
    var ignoredCount = Math.Min(1000, Math.Max(10, fileCount / 10));
    for (var i = 0; i < ignoredCount; i++) File.WriteAllText(Path.Combine(ignoredDirectory, $"ignored-{i}.txt"), "selective-needle-ignored");
    generation.Stop();

    using var service = await FindFastService.OpenAsync(data);
    var indexing = Stopwatch.StartNew();
    var root = await service.RootAddAsync(new RootAddOptions { Path = corpus, Exclude = ["ignored/**"] });
    indexing.Stop();
    var scanQueries = Math.Min(queries, 5);
    var scenarios = new (string Name, int Iterations, Func<SearchResponse> Search)[]
    {
        ("literal_selective", queries, () => service.SearchText(new SearchOptions { Query = "selective-needle-00000000", RootIds = [root.RootId], MaxResults = 100 })),
        ("literal_common_first_page", queries, () => service.SearchText(new SearchOptions { Query = "ordinary text", RootIds = [root.RootId], MaxResults = 100 })),
        ("literal_short", scanQueries, () => service.SearchText(new SearchOptions { Query = "xy", RootIds = [root.RootId], MaxResults = 100 })),
        ("regex_selective", queries, () => service.SearchRegex(new RegexSearchOptions { Pattern = @"selective-needle-\d{8}", RootIds = [root.RootId], MaxResults = 100 })),
        ("regex_without_literal", scanQueries, () => service.SearchRegex(new RegexSearchOptions { Pattern = @"\w+\s+text", RootIds = [root.RootId], MaxResults = 100 })),
        ("large_file_literal", queries, () => service.SearchText(new SearchOptions { Query = "LARGE-BOUNDARY-NEEDLE", RootIds = [root.RootId], MaxResults = 100 }))
    };

    Console.WriteLine($"hardware={Environment.MachineName}; os={Environment.OSVersion}; cpu={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}; logical_cpus={Environment.ProcessorCount}; dotnet={Environment.Version}");
    Console.WriteLine($"files_requested={fileCount}; files_indexed={root.FileCount}; generate_ms={generation.ElapsedMilliseconds}; index_ms={indexing.ElapsedMilliseconds}; queries_per_scenario={queries}; working_set_mb={Environment.WorkingSet / 1024d / 1024d:F1}");
    var results = new Dictionary<string, (double Cold, double P50, double P95, double P99)>();
    foreach (var scenario in scenarios)
    {
        var coldWatch = Stopwatch.StartNew(); var coldResult = scenario.Search(); coldWatch.Stop();
        var samples = new long[scenario.Iterations];
        for (var i = 0; i < samples.Length; i++) { var sw = Stopwatch.StartNew(); scenario.Search(); samples[i] = sw.ElapsedTicks; }
        Array.Sort(samples); double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        var result = (Ms(coldWatch.ElapsedTicks), Ms(samples[(int)((samples.Length - 1) * .50)]), Ms(samples[(int)((samples.Length - 1) * .95)]), Ms(samples[(int)((samples.Length - 1) * .99)]));
        results.Add(scenario.Name, result);
        Console.WriteLine($"scenario={scenario.Name}; samples={samples.Length}; cold_ms={result.Item1:F3}; p50_ms={result.Item2:F3}; p95_ms={result.Item3:F3}; p99_ms={result.Item4:F3}; matches={coldResult.Matches.Count}; truncated={coldResult.Truncated}; strategy={coldResult.QueryPlan.Strategy}");
    }

    var updatePath = Path.Combine(corpus, "module-00", "src", "watcher-validation.txt");
    var updateWatch = Stopwatch.StartNew(); await File.WriteAllTextAsync(updatePath, "watcher-visible-marker");
    while (updateWatch.Elapsed < TimeSpan.FromSeconds(5) && service.SearchText(new SearchOptions { Query = "watcher-visible-marker", RootIds = [root.RootId] }).Matches.Count == 0) await Task.Delay(25);
    updateWatch.Stop();
    var updateVisible = service.SearchText(new SearchOptions { Query = "watcher-visible-marker", RootIds = [root.RootId] }).Matches.Count == 1;
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var cancellationWatch = Stopwatch.StartNew();
    try { service.SearchText(new SearchOptions { Query = "never-present" }, cancelled.Token); } catch (OperationCanceledException) { }
    cancellationWatch.Stop();
    Console.WriteLine($"validation=watcher; visible={updateVisible}; elapsed_ms={updateWatch.Elapsed.TotalMilliseconds:F3}; target_ms=2000");
    Console.WriteLine($"validation=cancellation; elapsed_ms={cancellationWatch.Elapsed.TotalMilliseconds:F3}; target_ms=100");
    Console.WriteLine($"validation=excluded_files; expected={ignoredCount}; indexed_matches={service.SearchText(new SearchOptions { Query = "selective-needle-ignored", RootIds = [root.RootId] }).Matches.Count}");
    var failures = new List<string>();
    if (!updateVisible || updateWatch.ElapsedMilliseconds > 2000) failures.Add("watcher visibility exceeded 2000 ms");
    if (cancellationWatch.ElapsedMilliseconds > 100) failures.Add("cancellation exceeded 100 ms");
    if (fileCount >= 1_000_000 && results["literal_selective"].P95 >= 100) failures.Add("million-file selective literal p95 exceeded 100 ms");
    if (results["literal_common_first_page"].P95 >= 250) failures.Add("first-page p95 exceeded 250 ms");
    Console.WriteLine($"validation_result={(failures.Count == 0 ? "PASS" : "FAIL")}; scope={(fileCount >= 1_000_000 ? "full" : "reduced")}; failures={string.Join(" | ", failures)}");
    if (validate && failures.Count > 0) Environment.ExitCode = 2;
}
finally { try { Directory.Delete(workspace, true); } catch { } }
