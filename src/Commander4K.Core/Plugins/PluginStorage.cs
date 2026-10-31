using System.Text.Json;
using System.Text.Json.Nodes;

namespace Commander4K.Core.Plugins;

/// <summary>
/// Per-plugin settings and data (<c>storage.get</c> / <c>storage.set</c>): one JSON object per plugin in
/// <c>data/plugin-data/&lt;id&gt;.json</c>. Needs no permission — a plugin only ever sees its own file — so plugins keep
/// their configuration through the SDK instead of reading files next to their code. At most 1 MB per plugin.
/// </summary>
public sealed class PluginStorage(string folder)
{
    public const int MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();

    public string FileFor(string pluginId) => Path.Combine(folder, pluginId + ".json");

    public JsonNode? Get(string pluginId, string key)
    {
        lock (_gate)
        {
            return Load(pluginId)[key]?.DeepClone();
        }
    }

    /// <summary>Stores a value; <c>null</c> removes the key.</summary>
    public void Set(string pluginId, string key, JsonNode? value)
    {
        lock (_gate)
        {
            var data = Load(pluginId);
            if (value is null)
            {
                data.Remove(key);
            }
            else
            {
                data[key] = value.DeepClone();
            }

            var json = data.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (json.Length > MaxBytes)
            {
                throw new InvalidOperationException($"Plugin data would exceed {MaxBytes / 1024} KB.");
            }

            Directory.CreateDirectory(folder);
            var path = FileFor(pluginId);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    public IReadOnlyList<string> Keys(string pluginId)
    {
        lock (_gate)
        {
            return [.. Load(pluginId).Select(p => p.Key)];
        }
    }

    private JsonObject Load(string pluginId)
    {
        var path = FileFor(pluginId);
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
