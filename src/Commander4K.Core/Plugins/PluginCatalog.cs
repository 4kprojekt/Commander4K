using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>One file of a catalog plugin; the hash is checked after download.</summary>
public sealed record PluginCatalogFile
{
    public required string Path { get; init; }

    /// <summary>SHA-256 (hex) of the file's UTF-8 text with CR removed — the same on a Windows checkout and on GitHub.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>One plugin of the official catalog (<c>plugins-catalog/catalog.json</c>).</summary>
public sealed record PluginCatalogEntry
{
    /// <summary>The manifest id; <c>.c4k</c> files refer to it with <c>"plugin"</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Version { get; init => field = value ?? "1.0.0"; } = "1.0.0";

    public string Author { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary><c>python</c>, <c>node</c>, <c>powershell</c> or <c>dotnet</c>.</summary>
    public string Runtime { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Description per language code.</summary>
    public IReadOnlyDictionary<string, string> Description { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();

    /// <summary>Search words and what folders it suits (<c>steam</c>, <c>gallery</c>…).</summary>
    public IReadOnlyList<string> Tags { get; init => field = value ?? []; } = [];

    /// <summary>Patterns declared by this plugin's <c>contributes.previews</c>.</summary>
    public IReadOnlyList<string> PreviewExtensions { get; init => field = value ?? []; } = [];

    /// <summary>An example <c>.c4k</c> item for a folder this plugin shows.</summary>
    public string? Example { get; init; }

    /// <summary>The plugin folder, relative to <see cref="PluginCatalogDocument.BaseUrl"/>.</summary>
    public required string Path { get; init; }

    public IReadOnlyList<PluginCatalogFile> Files { get; init => field = value ?? []; } = [];

    [JsonIgnore]
    public string LocalizedDescription =>
        Description.TryGetValue(Loc.Language, out var text) || Description.TryGetValue("en", out text) ? text : Description.Values.FirstOrDefault() ?? string.Empty;
}

public sealed record PluginCatalogDocument
{
    public int Version { get; init; } = 1;

    public string? Updated { get; init; }

    /// <summary>Raw URL of the repository root; plugin paths are relative to it.</summary>
    public string BaseUrl { get; init => field = value ?? PluginCatalogService.DefaultBaseUrl; } = PluginCatalogService.DefaultBaseUrl;

    public IReadOnlyList<PluginCatalogEntry> Plugins { get; init => field = value ?? []; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PluginCatalogDocument))]
internal sealed partial class PluginCatalogJsonContext : JsonSerializerContext;

/// <summary>
/// The official plugin catalog: built-in copy → last downloaded copy (<c>data/plugin-catalog.json</c>) → the live file
/// from the repository (<see cref="RefreshAsync"/>), so new plugins appear without updating the app.
/// <see cref="DownloadAsync"/> fetches a plugin's files and checks each against its hash; installing goes through the
/// same consent as any plugin (it lands disabled until the user approves its permissions).
/// </summary>
public sealed class PluginCatalogService(HttpClient http, string cachePath)
{
    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/";

    public const string DefaultUrl = DefaultBaseUrl + "plugins-catalog/catalog.json";

    private const int MaxFileBytes = 1024 * 1024;

    public PluginCatalogDocument Catalog { get; private set; } = LoadBuiltIn();

    public IReadOnlyList<PluginCatalogEntry> Plugins => Catalog.Plugins;

    /// <summary>Raised (on any thread) after the list changed.</summary>
    public event Action? Changed;

    public PluginCatalogEntry? Find(string id) => Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns catalog plugins whose declared preview patterns match this file or service entry.</summary>
    public IReadOnlyList<PluginCatalogEntry> FindPreviewCandidates(VfsEntry entry, int limit = 5)
    {
        if (limit <= 0)
        {
            return [];
        }

        return Plugins
            .Select(plugin => (Plugin: plugin, Score: PreviewMatchScore(plugin, entry)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Plugin.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(candidate => candidate.Plugin)
            .ToArray();
    }

    /// <summary>Reads the cached copy (call off the UI thread).</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(cachePath) && Parse(File.ReadAllText(cachePath)) is { Plugins.Count: > 0 } cached)
            {
                Catalog = cached;
                Changed?.Invoke();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
    }

    /// <summary>Downloads the live catalog; returns an error message, or <c>null</c> on success.</summary>
    public async Task<string?> RefreshAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var json = await http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            var catalog = Parse(json);
            if (catalog.Plugins.Count == 0)
            {
                return "The downloaded catalog lists no plugins.";
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cachePath))!);
            await File.WriteAllTextAsync(cachePath + ".tmp", json, cancellationToken).ConfigureAwait(false);
            File.Move(cachePath + ".tmp", cachePath, overwrite: true);
            Catalog = catalog;
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    /// <summary>Downloads every file of <paramref name="entry"/>; throws <see cref="InvalidOperationException"/> when one is missing, too large or altered.</summary>
    public async Task<IReadOnlyList<GeneratedFile>> DownloadAsync(PluginCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Files.Count == 0 || !entry.Files.Any(f => string.Equals(f.Path, ManifestReader.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The catalog entry \"{entry.Id}\" lists no plugin.json.");
        }

        var files = new List<GeneratedFile>();
        foreach (var file in entry.Files)
        {
            if (!PluginAuthoring.IsSafeRelativePath(file.Path) || !PluginAuthoring.IsSafeRelativePath(entry.Path))
            {
                throw new InvalidOperationException($"Unsafe path in the catalog: \"{entry.Path}/{file.Path}\".");
            }

            var url = FileUrl(entry, file);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{url}: HTTP {(int)response.StatusCode}.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxFileBytes)
            {
                throw new InvalidOperationException($"{file.Path} is larger than 1 MB.");
            }

            var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿'); // A BOM is not part of the hashed text.
            if (!string.Equals(ContentHash(text), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{file.Path} does not match the catalog's checksum (the repository may be mid-update; try again later).");
            }

            files.Add(new GeneratedFile(file.Path, text.Replace("\r\n", "\n", StringComparison.Ordinal)));
        }

        return files;
    }

    public string FileUrl(PluginCatalogEntry entry, PluginCatalogFile file) =>
        Catalog.BaseUrl.TrimEnd('/') + "/" + string.Join('/', $"{entry.Path}/{file.Path}".Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    /// <summary>SHA-256 of the text without CR characters (git may check text files out with CRLF).</summary>
    public static string ContentHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r", string.Empty, StringComparison.Ordinal))));

    public static PluginCatalogDocument Parse(string json) =>
        EnsureUniquePluginIds(JsonSerializer.Deserialize(json, PluginCatalogJsonContext.Default.PluginCatalogDocument) ?? new PluginCatalogDocument());

    private static int PreviewMatchScore(PluginCatalogEntry plugin, VfsEntry entry)
    {
        var best = 0;
        foreach (var pattern in plugin.PreviewExtensions)
        {
            if (pattern == "/" && entry.IsContainer
                && plugin.Tags.Any(tag => tag.Length >= 3 && entry.Name.Contains(tag, StringComparison.OrdinalIgnoreCase)))
            {
                best = Math.Max(best, 60);
            }
            else if (ExternalPreviewPlugin.Matches(pattern, entry.Name))
            {
                best = Math.Max(best, 100);
            }
            else if (entry.Kind == VfsEntryKind.Service && pattern.Length > 0 && !pattern.Contains('*') && !pattern.Contains('?')
                && (entry.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                    || entry.Description?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true))
            {
                best = Math.Max(best, 80);
            }
        }

        return best;
    }

    public static PluginCatalogDocument LoadBuiltIn()
    {
        using var stream = typeof(PluginCatalogService).Assembly.GetManifestResourceStream("plugin-catalog.json")
            ?? throw new InvalidOperationException("The built-in plugin catalog is missing.");
        return EnsureUniquePluginIds(JsonSerializer.Deserialize(stream, PluginCatalogJsonContext.Default.PluginCatalogDocument) ?? new PluginCatalogDocument());
    }

    private static PluginCatalogDocument EnsureUniquePluginIds(PluginCatalogDocument catalog)
    {
        var duplicate = catalog.Plugins
            .GroupBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new JsonException($"The plugin catalog contains duplicate plugin id \"{duplicate.Key}\".");
        }

        return catalog;
    }
}
