# FindFast

FindFast is a persistent, indexed local-file search server for AI agents. It implements MCP over stdio and targets .NET 8.

## Build and test

```powershell
dotnet build FindFast.sln
dotnet run --project tests/FindFast.Tests
```

## Run as an MCP server

```powershell
$env:FINDFAST_DATA_DIR = "D:\indexes\findfast"
dotnet run --project src/FindFast.Server
```

Configure the command and arguments in an MCP client as `dotnet` and `run --project <absolute-path>/src/FindFast.Server`. Messages are newline-delimited JSON-RPC 2.0 on stdin/stdout. Diagnostics go only to stderr.

Tools: `roots_list`, `root_add`, `root_remove`, `index_update`, `index_status`, `search_text`, `files_find`, and `file_read`.

The Phase 1 storage is one atomically replaced JSON snapshot per root. It persists file metadata, contents, line offsets, and the inverted trigram index. `index_update` reconciles creations, modifications, and deletions by publishing a fresh snapshot. Default excluded directories are `.git`, `node_modules`, `bin`, `obj`, and `.findfast`; basic root `.gitignore` exclusion rules are honored. Files above 10 MiB and binary files are skipped.
