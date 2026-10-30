using System.Text.Json;
using Commander4K.Core.Configuration;

namespace Commander4K.Core.Security;

public enum HostKeyStatus : byte
{
    Unknown,
    Trusted,
    /// <summary>The host presented a different key than last time: possible man-in-the-middle.</summary>
    Mismatch,
}

/// <summary>
/// Trust-on-first-use store of SSH host key fingerprints (<c>config/known_hosts.json</c>). Thread-safe; called
/// from SSH.NET's connection thread, never from the UI thread.
/// </summary>
public sealed class KnownHostsStore(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _hosts;

    public HostKeyStatus Check(string host, int port, string fingerprint)
    {
        lock (_gate)
        {
            var hosts = Load();
            if (!hosts.TryGetValue(Key(host, port), out var known))
            {
                return HostKeyStatus.Unknown;
            }

            return string.Equals(known, fingerprint, StringComparison.Ordinal) ? HostKeyStatus.Trusted : HostKeyStatus.Mismatch;
        }
    }

    public void Trust(string host, int port, string fingerprint)
    {
        lock (_gate)
        {
            var hosts = Load();
            hosts[Key(host, port)] = fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(hosts, ConfigJsonContext.Default.DictionaryStringString));
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

    private Dictionary<string, string> Load()
    {
        if (_hosts is not null)
        {
            return _hosts;
        }

        _hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.DictionaryStringString);
            foreach (var (key, value) in stored ?? [])
            {
                _hosts[key] = value;
            }
        }

        return _hosts;
    }
}
