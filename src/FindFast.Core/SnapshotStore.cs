using System.Text.Json;

namespace FindFast.Core;

public sealed class SnapshotStore
{
    private readonly string _dataDirectory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public SnapshotStore(string dataDirectory) => _dataDirectory = Path.GetFullPath(dataDirectory);
    public string DataDirectory => _dataDirectory;

    public async Task<IReadOnlyList<RootSnapshot>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);
        var result = new List<RootSnapshot>();
        foreach (var file in Directory.EnumerateFiles(_dataDirectory, "*.snapshot.json"))
        {
            await using var stream = File.OpenRead(file);
            var snapshot = await JsonSerializer.DeserializeAsync<RootSnapshot>(stream, _json, cancellationToken);
            if (snapshot is not null && snapshot.SchemaVersion == 1) result.Add(snapshot);
        }
        return result;
    }

    public async Task SaveAsync(RootSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);
        var target = GetPath(snapshot.Root.RootId);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, snapshot, _json, cancellationToken);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete(string rootId)
    {
        var path = GetPath(rootId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetPath(string rootId) => Path.Combine(_dataDirectory, rootId + ".snapshot.json");
}
