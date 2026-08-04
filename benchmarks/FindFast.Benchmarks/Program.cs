using System.Diagnostics;
using FindFast.Core;

var fileCount = args.Length > 0 ? int.Parse(args[0]) : 10_000;
var queries = args.Length > 1 ? int.Parse(args[1]) : 200;
var workspace = Path.Combine(Path.GetTempPath(), "findfast-benchmark-" + Guid.NewGuid().ToString("N"));
var corpus = Path.Combine(workspace, "corpus"); var data = Path.Combine(workspace, "index");
Directory.CreateDirectory(corpus);
try
{
    var generation = Stopwatch.StartNew();
    for (var i = 0; i < fileCount; i++)
        await File.WriteAllTextAsync(Path.Combine(corpus, $"file-{i:D8}.txt"), $"deterministic corpus row {i}\n{(i % 997 == 0 ? "selective-needle" : "ordinary text")}");
    generation.Stop();
    using var service = await FindFastService.OpenAsync(data);
    var indexing = Stopwatch.StartNew(); await service.RootAddAsync(new RootAddOptions { Path = corpus }); indexing.Stop();
    var samples = new long[queries];
    for (var i = 0; i < queries; i++) { var sw = Stopwatch.StartNew(); service.SearchText(new SearchOptions { Query = "selective-needle", MaxResults = 100 }); samples[i] = sw.ElapsedTicks; }
    Array.Sort(samples);
    double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    Console.WriteLine($"hardware={Environment.MachineName}; os={Environment.OSVersion}; cpu={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}; dotnet={Environment.Version}");
    Console.WriteLine($"files={fileCount}; generate_ms={generation.ElapsedMilliseconds}; index_ms={indexing.ElapsedMilliseconds}; queries={queries}");
    Console.WriteLine($"search_ms p50={Ms(samples[(int)(queries*.50)]):F3} p95={Ms(samples[(int)(queries*.95)]):F3} p99={Ms(samples[Math.Min(queries-1,(int)(queries*.99))]):F3}");
}
finally { try { Directory.Delete(workspace, true); } catch { } }
