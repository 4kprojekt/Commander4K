using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Commander4K.Core.Input;
using Commander4K.Core.Theming;
using Commander4K.Core.Tools;

namespace Commander4K.Core.Configuration;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(KeymapFile))]
[JsonSerializable(typeof(ExternalToolsConfig))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Skin))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>Loads JSON configuration from a directory, writing defaults for missing files. Runs off the caller's thread.</summary>
public sealed class ConfigStore(string configDirectory)
{
    public const string KeymapFileName = "keymap.json";
    public const string ToolsFileName = "tools.json";
    public const string SettingsFileName = "settings.json";
    public const string SkinsDirectoryName = "skins";

    public string ConfigDirectory { get; } = configDirectory;

    public string SkinsDirectory => Path.Combine(ConfigDirectory, SkinsDirectoryName);

    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        LoadOrCreateAsync(SettingsFileName, () => new AppSettings(), ConfigJsonContext.Default.AppSettings, cancellationToken);

    /// <summary>Writes via a temporary file and a rename, so a crash mid-write never leaves a truncated file.</summary>
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        SaveAtomicallyAsync(SettingsFileName, settings, ConfigJsonContext.Default.AppSettings, cancellationToken);

    private Task SaveAtomicallyAsync<T>(string fileName, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                Directory.CreateDirectory(ConfigDirectory);
                var path = Path.Combine(ConfigDirectory, fileName);
                var temporary = path + ".tmp";
                await using (var output = File.Create(temporary))
                {
                    await JsonSerializer.SerializeAsync(output, value, typeInfo, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: true);
            },
            cancellationToken);

    public string KeymapPath => Path.Combine(ConfigDirectory, KeymapFileName);

    public string ToolsPath => Path.Combine(ConfigDirectory, ToolsFileName);

    public Task SaveKeymapAsync(KeymapFile keymap, CancellationToken cancellationToken = default) =>
        SaveAtomicallyAsync(KeymapFileName, keymap, ConfigJsonContext.Default.KeymapFile, cancellationToken);

    public Task<SkinCatalog> LoadSkinsAsync(CancellationToken cancellationToken = default) =>
        SkinCatalog.LoadAsync(SkinsDirectory, cancellationToken);

    public async Task<KeymapFile> LoadKeymapAsync(CancellationToken cancellationToken = default)
    {
        var file = await LoadOrCreateAsync(
            KeymapFileName,
            () => new KeymapFile
            {
                Bindings = new(DefaultKeymap.Bindings, StringComparer.OrdinalIgnoreCase),
                KnownCommands = [.. DefaultKeymap.Bindings.Values.Distinct()],
                AppliedMigrations = [.. DefaultKeymap.ChangedDefaults.Select(m => $"{m.Sequence}->{m.NewCommand}")],
            },
            ConfigJsonContext.Default.KeymapFile,
            cancellationToken).ConfigureAwait(false);

        if (MergeNewDefaults(file))
        {
            await SaveKeymapAsync(file, cancellationToken).ConfigureAwait(false);
        }

        return file;
    }

    /// <summary>Adds default bindings for commands introduced since the file was written; returns true when changed.</summary>
    internal static bool MergeNewDefaults(KeymapFile file)
    {
        var known = new HashSet<string>((IEnumerable<string>?)file.KnownCommands ?? file.Bindings.Values, StringComparer.OrdinalIgnoreCase);
        var bound = new HashSet<string>(file.Bindings.Keys, StringComparer.OrdinalIgnoreCase);
        var changed = file.KnownCommands is null;
        var applied = new HashSet<string>(file.AppliedMigrations ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var (sequence, oldCommand, newCommand) in DefaultKeymap.ChangedDefaults)
        {
            // Each migration runs once: later choices of the user (even going back to the old command) are kept.
            if (!applied.Add($"{sequence}->{newCommand}"))
            {
                continue;
            }

            changed = true;
            if (file.Bindings.TryGetValue(sequence, out var current) && string.Equals(current, oldCommand, StringComparison.OrdinalIgnoreCase))
            {
                file.Bindings[sequence] = newCommand;
            }
        }

        file.AppliedMigrations = [.. applied.Order(StringComparer.Ordinal)];

        foreach (var (sequence, command) in DefaultKeymap.Bindings)
        {
            if (!known.Contains(command) && !bound.Contains(sequence))
            {
                file.Bindings[sequence] = command;
                changed = true;
            }
        }

        var allKnown = known.Concat(DefaultKeymap.Bindings.Values).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        changed |= file.KnownCommands is null || allKnown.Count != file.KnownCommands.Count;
        file.KnownCommands = allKnown;
        return changed;
    }

    public Task<ExternalToolsConfig> LoadToolsAsync(CancellationToken cancellationToken = default) =>
        LoadOrCreateAsync(
            ToolsFileName,
            ExternalToolsService.CreateDefaultConfig,
            ConfigJsonContext.Default.ExternalToolsConfig,
            cancellationToken);

    private Task<T> LoadOrCreateAsync<T>(string fileName, Func<T> createDefault, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                var path = Path.Combine(ConfigDirectory, fileName);
                if (File.Exists(path))
                {
                    await using var input = File.OpenRead(path);
                    return await JsonSerializer.DeserializeAsync(input, typeInfo, cancellationToken).ConfigureAwait(false)
                        ?? createDefault();
                }

                var defaults = createDefault();
                Directory.CreateDirectory(ConfigDirectory);
                await using (var output = File.Create(path))
                {
                    await JsonSerializer.SerializeAsync(output, defaults, typeInfo, cancellationToken).ConfigureAwait(false);
                }

                return defaults;
            },
            cancellationToken);
}
