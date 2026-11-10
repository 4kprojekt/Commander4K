using System.Text.Json;

namespace Commander4K.Core.Folders;

/// <summary>Which <c>.c4k</c> files the user allowed to run commands or send requests: file path to <see cref="C4kItem.TrustHash"/>.</summary>
public sealed class C4kTrustStore(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _trusted;

    public bool IsTrusted(C4kItem item)
    {
        lock (_gate)
        {
            return Load().TryGetValue(item.FilePath, out var hash) && hash == item.TrustHash;
        }
    }

    public void Trust(C4kItem item)
    {
        lock (_gate)
        {
            var trusted = Load();
            trusted[item.FilePath] = item.TrustHash;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(trusted, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_trusted is not null)
        {
            return _trusted;
        }

        try
        {
            _trusted = File.Exists(path)
                ? new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [], StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return _trusted;
    }
}
