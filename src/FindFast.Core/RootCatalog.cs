using System.Text.Json;

namespace FindFast.Core;

public sealed class RootCatalog
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public RootCatalog(string dataDirectory) => _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDirectory), "roots.json");
    public string Path => _path;
    public async Task<List<RootDefinition>> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<RootDefinition>>(stream, _json, token) ?? [];
    }
    public async Task SaveAsync(IEnumerable<RootDefinition> roots, CancellationToken token = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, roots.OrderBy(x => x.RootId, StringComparer.Ordinal).ToArray(), _json, token);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
