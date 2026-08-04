# FindFast

FindFast is a persistent, indexed local-file search server for AI agents. It implements MCP over stdio and targets .NET 8.

## Build and test

```powershell
dotnet build FindFast.sln
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj
```

## Run as an MCP server

```powershell
$env:FINDFAST_DATA_DIR = "D:\indexes\findfast"
dotnet run --project src/FindFast.Server
```

Configure the command and arguments in an MCP client as `dotnet` and `run --project <absolute-path>/src/FindFast.Server`. Messages are newline-delimited JSON-RPC 2.0 on stdin/stdout. Diagnostics go only to stderr.

Optional HTTP transport uses the identical JSON-RPC/tool contracts:

```powershell
dotnet run --project src/FindFast.Server -- --http http://127.0.0.1:7331/
```

Send one newline-terminated JSON-RPC request in each HTTP `POST`. Bind loopback unless network exposure is explicitly intended.

Tools: `roots_list`, `root_add`, `root_remove`, `index_update`, `index_status`, `search_text`, `search_regex`, `files_find`, and `file_read`.

Storage uses immutable version directories per root: a metadata manifest, a separately compressed postings segment, and one compressed content blob per file. Publication atomically replaces a small `.current` pointer only after every segment is durable. Full rebuilds compact old versions while retaining the two newest for concurrent readers. Legacy JSON/gzip snapshots are imported automatically; corrupt pointers or legacy snapshots are quarantined. `index_update` reconciles creations, modifications, and deletions by publishing a fresh consistent snapshot. A `FileSystemWatcher` coalesces changes with a 500 ms debounce, backed by periodic five-minute reconciliation. Default excluded directories are `.git`, `node_modules`, `bin`, `obj`, and `.findfast`; nested `.gitignore` rules, anchoring and negation are honored. Binary files and files above 64 MiB are skipped. Files above 1 MiB are analyzed as a stream with a two-character overlap for boundary trigram correctness and copied directly into compressed content blobs; their contents are not retained in the published in-memory snapshot.

Tracked roots are independently persisted in the human-readable, atomically replaced `<data-dir>/roots.json`. The catalog records canonical path, stable ID, name, root type, include/exclude rules, `respect_gitignore`, state, version and timestamps. It is loaded before index segments: a registered root whose index is missing or unrecoverable remains visible as `stale` and can be rebuilt with `index_update`. Existing segment-only installations are migrated into the catalog automatically. Removing a root updates the catalog and index storage but never deletes source files.

Regex search conservatively extracts only provably mandatory literal prefixes, uses trigram postings when possible, and otherwise performs a bounded filtered scan. The regex cache is capped at 128 entries; the non-backtracking engine is preferred with a timeout-enforced fallback.

Content verification is streaming. Literal search uses 64 KiB windows with overlap derived from the query. Regex uses 64 KiB windows with 16 KiB overlap; potentially unbounded regex on larger files returns `truncated: true` with `truncation_reason: "regex_window_limit"`, so bounded verification can never silently claim completeness. `file_read` streams only through the requested line range.

## Benchmark and coverage

The deterministic benchmark reports corpus generation/index time and warm-search p50/p95/p99 together with machine, OS, CPU and runtime:

```powershell
dotnet run -c Release --project benchmarks/FindFast.Benchmarks -- 10000 200
```

Coverage is collected by the standard VSTest/coverlet collector:

```powershell
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
```
