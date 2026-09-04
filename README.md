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

Tools: `roots_list`, `root_add`, `root_update`, `root_remove`, `index_update`, `index_status`, `metrics_get`, `search_text`, `search_regex`, `files_find`, and `file_read`.

## Managing tracked roots

Every command below is an MCP `tools/call`. No `initialize` handshake is required: one newline-terminated request per line on stdin is enough, which makes the server drivable straight from a shell when no client is attached.

```json
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roots_list","arguments":{}}}
```

```powershell
$env:FINDFAST_DATA_DIR = "$env:LOCALAPPDATA\FindFast"
'{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roots_list","arguments":{}}}' |
  & "$env:LOCALAPPDATA\Programs\FindFast\FindFast.Server.exe"
```

The remaining examples show only the `arguments` object.

### Inspect what is registered

```json
{}
```

`roots_list` returns every root with state, version, file count and last update. `index_status` narrows to one:

```json
{ "root_id": "my_git_repos" }
```

Relevant states are `building`, `ready`, `updating`, `stale` and `error`. A registered root whose index is missing or unrecoverable reports `stale`.

### Add a root

`root_add` registers the directory, indexes it fully and starts its watcher in one call. Only `path` is required:

```json
{ "path": "C:\\novo\\repositorio" }
```

With every option spelled out:

```json
{
  "path": "C:\\novo\\repositorio",
  "name": "Novo Repositorio",
  "include": ["src/**", "docs/**"],
  "exclude": ["**/fixtures/**"],
  "extensions": ["cs", "sql", "md"],
  "respect_gitignore": true
}
```

`name` defaults to the directory name and seeds the `root_id` slug, deduplicated with a numeric suffix (`novo-repositorio-2`) when the slug is taken. `type` is detected: a directory containing `.git` becomes `git_repository`. Omitting `extensions` inherits the server's built-in set. A path that is already registered is refused, naming the existing `root_id`; a path that does not exist is refused before anything is written.

### Edit a root

`root_update` changes the filters of a registered root and reconciles the index incrementally, so `file_id` values and already-indexed content survive. Files that stopped qualifying become tombstones and lose their postings; newly qualifying files are read.

It accepts `include`, `exclude`, `extensions` and `respect_gitignore` only. **Omitted fields keep their current value**, so a call can touch one filter at a time:

```json
{ "root_id": "my_git_repos", "extensions": ["cs", "sql", "md"] }
```

Restore the server's default set for a root:

```json
{ "root_id": "my_git_repos", "extensions": [] }
```

Index every text file, including extensionless ones:

```json
{ "root_id": "my_git_repos", "extensions": ["*"] }
```

Add exclusions without disturbing the extension filter:

```json
{ "root_id": "my_git_repos", "exclude": ["artifacts/**", "**/*.generated.cs"] }
```

`path` and `name` are **not** updatable through this tool. Changing either means editing `roots.json` directly (see below) or removing and re-adding the root, which discards its index and every `file_id`.

### Remove a root

```json
{ "root_id": "my_git_repos" }
```

Drops the catalog entry, the watcher and the index storage. **Source files are never touched.**

### Force a reindex

Normal reconciliation, cheap when nothing moved:

```json
{ "root_id": "my_git_repos", "mode": "incremental", "wait": true }
```

Full rebuild, which rereads every file, discards tombstones and compacts old versions:

```json
{ "root_id": "my_git_repos", "mode": "full", "wait": true }
```

Use `full` after the server's default extension set changes, since an incremental pass on a `ready` root with no filesystem change publishes nothing.

### Editing `roots.json` by hand

Reserve this for what the tools cannot do — renaming a root or pointing it at a moved directory. Two properties of the catalog make it risky otherwise:

- **It is read once, at startup.** There is no watcher on `roots.json`, so an edit is invisible to a running process.
- **It is rewritten in full after every index publication.** The server serializes its in-memory roots over the file whenever a segment is published, so an edit made while the server runs is silently overwritten. Under stdio each client spawns its own process, so two clients sharing one `FINDFAST_DATA_DIR` means two writers: every client using the server has to be closed, not just one.

With the clients closed, append an object to the array in `<data-dir>/roots.json`. Only three fields are required; the rest default:

```json
[
  {
    "root_id": "novo-repo",
    "name": "Novo Repositorio",
    "path": "C:\\novo\\repositorio"
  }
]
```

`root_id` must be unique in the array — a duplicate loses to the last entry when the catalog is loaded into a dictionary. Leave `state`, `version`, `file_count` and `last_updated` out: startup marks every registered root `stale` regardless, and the first reconciliation fills them in.

A root added this way **is monitored and indexed automatically**, with no reinstall and no explicit command. On the next startup it is registered as `stale` and immediately gets a `FileSystemWatcher`, even without an index. Indexing then arrives by whichever comes first: the periodic sweep, which queues every root every five minutes, or the watcher, which reacts to the first change under that directory within about a second. An incremental pass starting from an empty snapshot reads everything, because the "nothing moved" shortcut requires the previous state to be `ready`.

Storage uses immutable version directories per root: a metadata manifest, a separately compressed postings segment, and one compressed content blob per file. Publication atomically replaces a small `.current` pointer only after every segment is durable. Full rebuilds compact old versions while retaining the two newest for concurrent readers. Legacy JSON/gzip snapshots are imported automatically; corrupt pointers or legacy snapshots are quarantined. `index_update` reconciles creations, modifications, and deletions by publishing a fresh consistent snapshot. A `FileSystemWatcher` coalesces changes with a one-second debounce, backed by periodic five-minute reconciliation. Default excluded directories are `.git`, `node_modules`, `bin`, `obj`, and `.findfast`; nested `.gitignore` rules, anchoring and negation are honored. Binary files and files above 64 MiB are skipped. Files above 1 MiB are analyzed as a stream with a two-character overlap for boundary trigram correctness and copied directly into compressed content blobs; their contents are not retained in the published in-memory snapshot.

Tracked roots are independently persisted in the human-readable, atomically replaced `<data-dir>/roots.json`. The catalog records canonical path, stable ID, name, root type, include/exclude rules, allowed `extensions`, `respect_gitignore`, state, version and timestamps. An absent or empty `extensions` list selects the built-in default set of code and text extensions, which leaves extensionless files out; the `*` token disables extension filtering entirely. A populated list is normalized (`cs` becomes `.cs`) and filters the final extension case-insensitively in addition to include/exclude/gitignore. It is loaded before index segments: a registered root whose index is missing or unrecoverable remains visible as `stale` and can be rebuilt with `index_update`. Existing segment-only installations are migrated into the catalog automatically. Removing a root updates the catalog and index storage but never deletes source files.

Regex search conservatively extracts only provably mandatory literal prefixes, uses trigram postings when possible, and otherwise performs a bounded filtered scan. The regex cache is capped at 128 entries; the non-backtracking engine is preferred with a timeout-enforced fallback.

Content verification is streaming. Literal search uses 64 KiB windows with overlap derived from the query. Regex uses 64 KiB windows with 16 KiB overlap; potentially unbounded regex on larger files returns `truncated: true` with `truncation_reason: "regex_window_limit"`, so bounded verification can never silently claim completeness. `file_read` streams only through the requested line range.

## Benchmark and coverage

The deterministic benchmark reports corpus generation/index time and warm-search p50/p95/p99 together with machine, OS, CPU and runtime:

```powershell
dotnet run -c Release --project benchmarks/FindFast.Benchmarks -- 10000 200 --validate
```

The first positional argument is the indexed file count and the second is the number of warm samples per indexed scenario. Expensive filtered scans are capped at five samples. The benchmark covers selective/common/short literals, selective and non-prefilterable regex, large files, a monorepository-style directory layout, a large exclusion list, watcher visibility and cancellation. It reports cold latency, warm p50/p95/p99, process working set and whether the measured targets pass. Runs below one million files are explicitly reported as `scope=reduced`; use `1000000` for the full selective-query acceptance target.

Coverage is collected by the standard VSTest/coverlet collector:

```powershell
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
```
