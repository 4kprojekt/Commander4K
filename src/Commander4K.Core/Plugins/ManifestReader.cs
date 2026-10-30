using System.Text.Json;
using System.Text.RegularExpressions;
using Commander4K.Core.Updates;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>A <c>plugin.json</c> found on disk, with problems that prevent it from being enabled.</summary>
public sealed record DiscoveredPlugin(string Folder, PluginManifest? Manifest, IReadOnlyList<string> Errors)
{
    public bool IsValid => Manifest is not null && Errors.Count == 0;

    public string EntryPath => Path.GetFullPath(Path.Combine(Folder, Manifest?.Runtime.Entry ?? string.Empty));
}

/// <summary>Reads and validates external plugin manifests (<c>plugins/&lt;name&gt;/plugin.json</c>).</summary>
public static partial class ManifestReader
{
    public const string FileName = "plugin.json";
    public const int ProtocolVersion = 1;

    public static readonly IReadOnlyList<string> RuntimeTypes = ["node", "python", "powershell", "dotnet", "exe"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<DiscoveredPlugin> Discover(IEnumerable<string> roots)
    {
        var found = new List<DiscoveredPlugin>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(folder, FileName)))
                {
                    found.Add(Read(folder));
                }
            }
        }

        return found;
    }

    public static DiscoveredPlugin Read(string folder)
    {
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(folder, FileName)), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return new DiscoveredPlugin(folder, null, [$"Nieprawidłowy {FileName}: {ex.Message}"]);
        }

        return manifest is null
            ? new DiscoveredPlugin(folder, null, [$"Pusty {FileName}."])
            : new DiscoveredPlugin(folder, manifest, Validate(manifest, folder));
    }

    public static IReadOnlyList<string> Validate(PluginManifest manifest, string folder)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdRegex().IsMatch(manifest.Id))
        {
            errors.Add($"Id „{manifest.Id}” musi składać się z małych liter, cyfr, kropek, myślników i podkreśleń (np. „acme.log-tools”).");
        }

        if (manifest.Id?.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase) == true)
        {
            errors.Add("Prefiks „builtin.” jest zarezerwowany dla wtyczek wbudowanych.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add("Brak nazwy (name).");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"Wersja „{manifest.Version}” nie jest wersją semantyczną (np. 1.0.0).");
        }

        if (manifest.Protocol > ProtocolVersion)
        {
            errors.Add($"Wtyczka wymaga protokołu {manifest.Protocol}, aplikacja obsługuje {ProtocolVersion}.");
        }

        if (manifest.Runtime is null || !RuntimeTypes.Contains(manifest.Runtime.Type, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"runtime.type musi być jednym z: {string.Join(", ", RuntimeTypes)}.");
        }
        else if (string.IsNullOrWhiteSpace(manifest.Runtime.Entry))
        {
            errors.Add("Brak runtime.entry (plik startowy).");
        }
        else
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var entry = Path.GetFullPath(Path.Combine(folder, manifest.Runtime.Entry));
            if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("runtime.entry musi wskazywać plik wewnątrz folderu wtyczki.");
            }
            else if (!File.Exists(entry))
            {
                errors.Add($"Nie znaleziono pliku startowego {manifest.Runtime.Entry}.");
            }
        }

        foreach (var permission in manifest.Permissions ?? [])
        {
            if (!PluginPermissions.All.Contains(permission, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Nieznane uprawnienie „{permission}”. Dozwolone: {string.Join(", ", PluginPermissions.All)}.");
            }
        }

        var ids = (manifest.Contributes?.Commands?.Select(c => "command:" + c.Id) ?? [])
            .Concat(manifest.Contributes?.Panels?.Select(p => "panel:" + p.Id) ?? [])
            .Concat(manifest.Contributes?.Previews?.Select(p => "preview:" + p.Id) ?? [])
            .Concat(manifest.Contributes?.Palettes?.Select(p => "palette:" + p.Id) ?? [])
            .ToList();
        foreach (var duplicate in ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Powtórzony identyfikator {duplicate.Key}.");
        }

        if (ids.Count == 0)
        {
            errors.Add("Wtyczka nic nie dodaje (contributes.commands / panels / previews / palettes są puste).");
        }

        return errors;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex IdRegex();
}
