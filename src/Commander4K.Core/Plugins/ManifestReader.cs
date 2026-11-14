using System.Text.Json;
using System.Text.RegularExpressions;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
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
        Converters = { new OptionListConverter() },
    };

    private static readonly string[] OptionTypes = ["string", "number", "boolean", "array", "object"];

    /// <summary>Lenient <c>options</c> list: a non-array is ignored, entries without an id are skipped, an unknown type is "string".</summary>
    private sealed class OptionListConverter : System.Text.Json.Serialization.JsonConverter<IReadOnlyList<OptionContribution>>
    {
        public override IReadOnlyList<OptionContribution>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<OptionContribution>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? Text(string name) => item.EnumerateObject()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    .Value.ToString() is { Length: > 0 } s ? s : null;

                if (Text("id") is not { } id || string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var type = Text("type")?.ToLowerInvariant();
                var values = item.EnumerateObject()
                    .FirstOrDefault(p => string.Equals(p.Name, "values", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                    .Value;
                var required = item.EnumerateObject()
                    .Any(p => string.Equals(p.Name, "required", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True);
                list.Add(new OptionContribution(
                    id,
                    type is not null && OptionTypes.Contains(type) ? type : "string",
                    Text("description"),
                    values.ValueKind == JsonValueKind.Array
                        ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                        : null,
                    required));
            }

            return list;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<OptionContribution> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.ToArray(), options);
    }

    public static IReadOnlyList<DiscoveredPlugin> Discover(IEnumerable<string> roots)
    {
        var found = new List<DiscoveredPlugin>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                // "<id>.new-xxxxxxxx" is an install in progress (or one Windows kept from finishing): never a plugin.
                if (PluginAuthoring.IsTemporaryFolder(folder))
                {
                    continue;
                }

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
            return new DiscoveredPlugin(folder, null, [Loc.T("Nieprawidłowy {0}: {1}", FileName, ex.Message)]);
        }

        return manifest is null
            ? new DiscoveredPlugin(folder, null, [Loc.T("Pusty {0}.", FileName)])
            : new DiscoveredPlugin(folder, manifest, Validate(manifest, folder));
    }

    public static IReadOnlyList<string> Validate(PluginManifest manifest, string folder)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdRegex().IsMatch(manifest.Id))
        {
            errors.Add(Loc.T("Id „{0}” musi składać się z małych liter, cyfr, kropek, myślników i podkreśleń (np. „acme.log-tools”).", manifest.Id));
        }

        if (manifest.Id?.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase) == true)
        {
            errors.Add(Loc.T("Prefiks „builtin.” jest zarezerwowany dla wtyczek wbudowanych."));
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add(Loc.T("Brak nazwy (name)."));
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            errors.Add(Loc.T("Wersja „{0}” nie jest wersją semantyczną (np. 1.0.0).", manifest.Version));
        }

        if (manifest.Protocol > ProtocolVersion)
        {
            errors.Add(Loc.T("Wtyczka wymaga protokołu {0}, aplikacja obsługuje {1}.", manifest.Protocol, ProtocolVersion));
        }

        if (manifest.Runtime is null || !RuntimeTypes.Contains(manifest.Runtime.Type, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(Loc.T("runtime.type musi być jednym z: {0}.", string.Join(", ", RuntimeTypes)));
        }
        else if (string.IsNullOrWhiteSpace(manifest.Runtime.Entry))
        {
            errors.Add(Loc.T("Brak runtime.entry (plik startowy)."));
        }
        else
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var entry = Path.GetFullPath(Path.Combine(folder, manifest.Runtime.Entry));
            if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Loc.T("runtime.entry musi wskazywać plik wewnątrz folderu wtyczki."));
            }
            else if (!File.Exists(entry))
            {
                errors.Add(Loc.T("Nie znaleziono pliku startowego {0}.", manifest.Runtime.Entry));
            }
        }

        foreach (var permission in manifest.Permissions ?? [])
        {
            if (!PluginPermissions.All.Contains(permission, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(Loc.T("Nieznane uprawnienie „{0}”. Dozwolone: {1}.", permission, string.Join(", ", PluginPermissions.All)));
            }
        }

        foreach (var preview in manifest.Contributes?.Previews ?? [])
        {
            if (preview.WebRoot is { } webRoot
                && (!PluginAuthoring.IsSafeRelativePath(webRoot) || !Directory.Exists(Path.Combine(folder, webRoot))))
            {
                errors.Add(Loc.T("Podgląd „{0}”: webRoot „{1}” musi być folderem wewnątrz wtyczki.", preview.Id, webRoot));
            }
            else if (preview.WebRoot is { } root && manifest.Runtime?.Entry is { Length: > 0 } startFile)
            {
                // Page code is not linted, so it must not share a folder with the code that runs as the plugin process.
                var web = PluginSourceCheck.NormalizeFolder(root);
                var entryFolder = PluginSourceCheck.NormalizeFolder(Path.GetDirectoryName(PluginSourceCheck.NormalizeFolder(startFile)) ?? string.Empty);
                if (entryFolder.Length > 0
                    && (string.Equals(web, entryFolder, StringComparison.OrdinalIgnoreCase)
                        || entryFolder.StartsWith(web + "/", StringComparison.OrdinalIgnoreCase)
                        || web.StartsWith(entryFolder + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add(Loc.T("Podgląd „{0}”: webRoot „{1}” nie może zawierać pliku startowego wtyczki ani leżeć w jego folderze.", preview.Id, root));
                }
            }
        }

        var ids = (manifest.Contributes?.Commands?.Select(c => "command:" + c.Id) ?? [])
            .Concat(manifest.Contributes?.Panels?.Select(p => "panel:" + p.Id) ?? [])
            .Concat(manifest.Contributes?.Previews?.Select(p => "preview:" + p.Id) ?? [])
            .ToList();
        foreach (var duplicate in ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add(Loc.T("Powtórzony identyfikator {0}.", duplicate.Key));
        }

        if (manifest.Contributes?.Background is { } background)
        {
            if (!manifest.Allows(PluginPermissions.Background))
            {
                errors.Add(Loc.T("contributes.background wymaga uprawnienia „{0}”.", PluginPermissions.Background));
            }

            if (!IsInterval(background.Interval))
            {
                errors.Add(Loc.T("background.interval „{0}” musi wyglądać tak: 90s, 5m lub 1h.", background.Interval));
            }
        }

        if (ids.Count == 0 && manifest.Contributes?.Background is null)
        {
            errors.Add(Loc.T("Wtyczka nic nie dodaje (contributes.commands / panels / previews są puste)."));
        }

        return errors;
    }

    private static bool IsInterval(string? text)
    {
        try
        {
            return C4kJson.ParseRefresh(text) is not null;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex IdRegex();
}
