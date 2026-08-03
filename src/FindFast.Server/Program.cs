using System.Text.Json;
using System.Text.Json.Nodes;
using FindFast.Core;

var dataDirectory = Environment.GetEnvironmentVariable("FINDFAST_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindFast");
var service = await FindFastService.OpenAsync(dataDirectory);
var server = new McpServer(service, Console.In, Console.Out, Console.Error);
await server.RunAsync(CancellationToken.None);

internal sealed class McpServer(FindFastService service, TextReader input, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? id = null;
            try
            {
                var request = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("Request must be a JSON object.");
                id = request["id"]?.DeepClone();
                var method = request["method"]?.GetValue<string>() ?? throw new JsonException("Missing method.");
                if (id is null && method.StartsWith("notifications/", StringComparison.Ordinal)) continue;
                var result = await DispatchAsync(method, request["params"] as JsonObject, cancellationToken);
                await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, cancellationToken);
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync($"FindFast request failed: {ex.GetType().Name}: {ex.Message}");
                if (id is not null)
                    await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id,
                        ["error"] = new JsonObject { ["code"] = ErrorCode(ex), ["message"] = ex.Message } }, cancellationToken);
            }
        }
    }

    private async Task<JsonNode?> DispatchAsync(string method, JsonObject? parameters, CancellationToken cancellationToken) => method switch
    {
        "initialize" => JsonSerializer.SerializeToNode(new { protocolVersion = "2025-06-18", capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "FindFast", version = "0.1.0" } }, ProtocolJson),
        "ping" => new JsonObject(),
        "tools/list" => JsonSerializer.SerializeToNode(new { tools = ToolDefinitions.All }, ProtocolJson),
        "tools/call" => await CallToolAsync(parameters ?? throw new ArgumentException("Missing params."), cancellationToken),
        _ => throw new NotSupportedException($"Unknown MCP method: {method}")
    };

    private async Task<JsonNode> CallToolAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var name = parameters["name"]?.GetValue<string>() ?? throw new ArgumentException("Missing tool name.");
        var args = parameters["arguments"] as JsonObject ?? new JsonObject();
        object result = name switch
        {
            "roots_list" => new { roots = service.RootsList() },
            "root_add" => await service.RootAddAsync(new RootAddOptions { Path = RequiredString(args, "path"), Name = String(args, "name"),
                Include = Strings(args, "include"), Exclude = Strings(args, "exclude"), RespectGitignore = Bool(args, "respect_gitignore", true) }, cancellationToken),
            "root_remove" => Remove(RequiredString(args, "root_id")),
            "index_update" => await UpdateAsync(args, cancellationToken),
            "index_status" => service.IndexStatus(RequiredString(args, "root_id")),
            "search_text" => service.SearchText(new SearchOptions { Query = RequiredString(args, "query"), RootIds = Strings(args, "root_ids"),
                PathGlob = String(args, "path_glob"), CaseSensitive = Bool(args, "case_sensitive", true), WholeWord = Bool(args, "whole_word", false),
                ContextLines = Int(args, "context_lines", 1), MaxResults = Int(args, "max_results", 100),
                MaxResultsPerFile = Int(args, "max_results_per_file", 25), Cursor = String(args, "cursor"), TimeoutMs = Int(args, "timeout_ms", 5000) }, cancellationToken),
            "files_find" => FindFiles(args, cancellationToken),
            "file_read" => service.FileRead(RequiredString(args, "root_id"), RequiredString(args, "path"),
                Int(args, "start_line", 1), Int(args, "end_line", 200), cancellationToken),
            _ => throw new NotSupportedException($"Unknown tool: {name}")
        };
        var serialized = JsonSerializer.Serialize(result, Json);
        return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = serialized }),
            ["structuredContent"] = JsonNode.Parse(serialized) };
    }

    private object Remove(string rootId) { service.RootRemove(rootId); return new { removed = true, rootId }; }
    private async Task<object> UpdateAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var mode = String(args, "mode") ?? "incremental";
        if (mode is not ("incremental" or "full")) throw new ArgumentException("mode must be incremental or full.");
        var snapshot = await service.IndexUpdateAsync(RequiredString(args, "root_id"), mode == "full", cancellationToken);
        return snapshot.Root;
    }
    private object FindFiles(JsonObject args, CancellationToken cancellationToken)
    {
        var files = service.FilesFind(Strings(args, "root_ids"), String(args, "path_glob"), String(args, "query"),
            Int(args, "max_results", 100), String(args, "cursor"), out var nextCursor, cancellationToken);
        return new { files, truncated = nextCursor is not null, nextCursor };
    }
    private async Task WriteAsync(JsonObject value, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(value.ToJsonString(Json).AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
    private static int ErrorCode(Exception exception) => exception switch
    {
        JsonException or ArgumentException => -32602,
        NotSupportedException => -32601,
        KeyNotFoundException or FileNotFoundException => -32001,
        _ => -32603
    };
    private static string RequiredString(JsonObject args, string name) => String(args, name) ?? throw new ArgumentException($"Missing {name}.");
    private static string? String(JsonObject args, string name) => args[name]?.GetValue<string>();
    private static bool Bool(JsonObject args, string name, bool fallback) => args[name]?.GetValue<bool>() ?? fallback;
    private static int Int(JsonObject args, string name, int fallback) => args[name]?.GetValue<int>() ?? fallback;
    private static string[]? Strings(JsonObject args, string name) => args[name] is JsonArray array ? array.Select(x => x!.GetValue<string>()).ToArray() : null;
}

internal static class ToolDefinitions
{
    private static object Tool(string name, string description, object properties, string[]? required = null) => new
    {
        name, description, inputSchema = new { type = "object", properties, required = required ?? [], additionalProperties = false }
    };
    private static object S(string description) => new { type = "string", description };
    private static object B(string description) => new { type = "boolean", description };
    private static object I(string description, int minimum, int maximum) => new { type = "integer", description, minimum, maximum };
    private static object A(string description) => new { type = "array", description, items = new { type = "string" } };
    public static readonly object[] All =
    [
        Tool("roots_list", "List registered indexed roots.", new { }),
        Tool("root_add", "Register and index a local directory.", new { path = S("Absolute directory path"), name = S("Friendly name"),
            include = A("Include globs"), exclude = A("Exclude globs"), respect_gitignore = B("Respect root .gitignore rules") }, ["path"]),
        Tool("root_remove", "Remove a root and its index; source files are untouched.", new { root_id = S("Root identifier") }, ["root_id"]),
        Tool("index_update", "Reconcile or fully rebuild a root index.", new { root_id = S("Root identifier"), mode = S("incremental or full"), wait = B("Wait for completion; currently always true") }, ["root_id"]),
        Tool("index_status", "Return index state and version.", new { root_id = S("Root identifier") }, ["root_id"]),
        Tool("search_text", "Search indexed file content for literal text.", new { query = S("Literal expression"), root_ids = A("Roots to search"), path_glob = S("Path glob"),
            case_sensitive = B("Case-sensitive match"), whole_word = B("Require word boundaries"), context_lines = I("Context lines", 0, 20),
            max_results = I("Page size", 1, 1000), max_results_per_file = I("Per-file cap", 1, 1000), cursor = S("Opaque page cursor"), timeout_ms = I("Query timeout", 1, 60000) }, ["query"]),
        Tool("files_find", "Find indexed files by path.", new { root_ids = A("Roots to search"), path_glob = S("Path glob"), query = S("Path substring"),
            max_results = I("Page size", 1, 1000), cursor = S("Opaque page cursor") }),
        Tool("file_read", "Read a bounded line range from an indexed file.", new { root_id = S("Root identifier"), path = S("Relative indexed path"),
            start_line = I("First line, one based", 1, int.MaxValue), end_line = I("Last line, inclusive", 1, int.MaxValue) }, ["root_id", "path"])
    ];
}
