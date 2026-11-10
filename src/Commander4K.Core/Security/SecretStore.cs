using System.Text.Json;
using System.Text.RegularExpressions;

namespace Commander4K.Core.Security;

/// <summary>A <c>{secret:name}</c> placeholder names a secret that is not stored yet.</summary>
public sealed class SecretMissingException(string name) : InvalidOperationException($"Secret '{name}' is not stored.")
{
    public string Name { get; } = name;
}

/// <summary>A secret was asked for by a request to another host than the one it was saved for.</summary>
public sealed class SecretHostMismatchException(string name, string boundHost)
    : InvalidOperationException($"Secret '{name}' belongs to {boundHost}.")
{
    public string Name { get; } = name;

    public string BoundHost { get; } = boundHost;
}

/// <summary>
/// Tokens and keys that <c>.c4k</c> files refer to as <c>{secret:name}</c> (<c>config/secrets.json</c>), encrypted
/// with DPAPI. Each secret is bound to the origin (scheme, host, port) it was first saved for, so an edited <c>.c4k</c> in a shared folder
/// cannot send it anywhere else.
/// </summary>
public sealed partial class SecretStore(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, Entry>? _entries;

    /// <summary>Stores <paramref name="value"/>; a secret already stored keeps the host it was bound to.</summary>
    public void Set(string name, string value, string host)
    {
        lock (_gate)
        {
            var entries = Load();
            var bound = entries.TryGetValue(name, out var existing) ? existing.Host : host;
            entries[name] = new Entry(SecretProtector.Protect(value), bound);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    /// <summary>The value, or <c>null</c> when it is missing, unreadable, or bound to another host.</summary>
    public string? Get(string name, string host)
    {
        lock (_gate)
        {
            return Load().TryGetValue(name, out var entry) && string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase)
                ? SecretProtector.Unprotect(entry.Value)
                : null;
        }
    }

    public bool Exists(string name)
    {
        lock (_gate)
        {
            return Load().ContainsKey(name);
        }
    }

    /// <summary>Replaces every <c>{secret:name}</c> in <paramref name="text"/> for a request to <paramref name="host"/>.</summary>
    public string Expand(string text, string host) => Placeholder().Replace(text, match =>
    {
        var name = match.Groups[1].Value;
        lock (_gate)
        {
            if (!Load().TryGetValue(name, out var entry) || SecretProtector.Unprotect(entry.Value) is not { } value)
            {
                throw new SecretMissingException(name);
            }

            return string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase) ? value : throw new SecretHostMismatchException(name, entry.Host);
        }
    });

    /// <summary>The names of the secrets <paramref name="text"/> refers to.</summary>
    public static IEnumerable<string> NamesIn(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value);

    [GeneratedRegex(@"\{secret:([A-Za-z0-9._-]+)\}")]
    private static partial Regex Placeholder();

    private Dictionary<string, Entry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            _entries = File.Exists(path)
                ? new Dictionary<string, Entry>(JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? [], StringComparer.Ordinal)
                : new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        }

        return _entries;
    }

    private sealed record Entry(string Value, string Host);
}
