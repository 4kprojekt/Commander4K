using System.Text.Json;
using System.Text.Json.Nodes;
using Commander4K.Core.Folders;

namespace Commander4K.Core.Configuration;

/// <summary>
/// The hosts the user added to the LAN list. They live in an ordinary <c>.c4k</c> item, <c>config/lan/LAN.c4k</c>:
/// <c>{ "open": "net://?lan", "options": { "hosts": [ { "name", "host", "services": [ { "name", "scheme", "port" } ] } ] } }</c>,
/// so the folder opens the LAN list and the file can be edited by hand.
/// </summary>
public static class LanHostsFile
{
    public const string FolderName = "lan";

    public const string FileName = "LAN.c4k";

    /// <summary>The path the item opens; the same as the network plugin's LAN group.</summary>
    public const string LanPath = "net://?lan";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <param name="Error">Why the file could not be read (English, for the status line); <c>null</c> when it was, or when there is none.</param>
    public sealed record Content(IReadOnlyList<LanHostSetting> Hosts, string? Error);

    public static string PathIn(string configDirectory) => Path.Combine(configDirectory, FolderName, FileName);

    /// <summary>A missing file is an empty list; a broken one is an empty list with the reason.</summary>
    public static async Task<Content> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) ? Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : new Content([], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Content([], ex.Message);
        }
    }

    /// <summary>
    /// A missing <c>name</c> becomes the host. Entries without a usable <c>host</c> (a bare IPv4 address or name: no
    /// protocol, port or path) are left out and named in <see cref="Content.Error"/>, so nothing rewrites the file
    /// over them.
    /// </summary>
    public static Content Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, Lenient);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object
                || !options.TryGetProperty("hosts", out var hosts))
            {
                return new Content([], null);
            }

            var parsed = hosts.Deserialize(ConfigJsonContext.Default.ListLanHostSetting) ?? [];
            var bad = parsed.Select((host, index) => (host, index)).Where(entry => entry.host is null || !IsValidHost(entry.host.Host))
                .Select(entry => entry.index + 1).ToList();
            parsed.RemoveAll(host => host is null || !IsValidHost(host.Host));
            foreach (var host in parsed.Where(host => string.IsNullOrWhiteSpace(host.Name)))
            {
                host.Name = host.Host;
            }

            return new Content(parsed, bad.Count == 0 ? null
                : $"\"hosts\" entry {string.Join(", ", bad)} needs \"host\": an IPv4 address or a host name, without protocol, port or path.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new Content([], ex.Message);
        }
    }

    /// <summary>A bare IPv4 address or DNS name: what a LAN row's path (<c>net://host</c>) can carry.</summary>
    public static bool IsValidHost(string host)
    {
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', ':', '@', '?']) >= 0)
        {
            return false;
        }

        return System.Net.IPAddress.TryParse(host, out var address)
            ? address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            : Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    /// <summary>
    /// The file with <paramref name="hosts"/> as its list: the rest of <paramref name="existing"/> (other keys and
    /// options) is kept, comments are not. <c>null</c> or unreadable text gives a new file.
    /// </summary>
    public static string Write(string? existing, IEnumerable<LanHostSetting> hosts)
    {
        JsonObject? root = null;
        try
        {
            root = existing is null ? null : JsonNode.Parse(existing, documentOptions: Lenient) as JsonObject;
            _ = root?.Count; // duplicate keys throw here, on first use, not in Parse
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            root = null;
        }

        root ??= new JsonObject { ["open"] = LanPath };
        if (root["options"] is not JsonObject options)
        {
            root["options"] = options = [];
        }

        options["hosts"] = JsonSerializer.SerializeToNode([.. hosts], ConfigJsonContext.Default.ListLanHostSetting);
        return C4kWriter.Create(root);
    }
}
