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
        Task.Run(
            async () =>
            {
                Directory.CreateDirectory(ConfigDirectory);
                var path = Path.Combine(ConfigDirectory, SettingsFileName);
                var temporary = path + ".tmp";
                await using (var output = File.Create(temporary))
                {
                    await JsonSerializer.SerializeAsync(output, settings, ConfigJsonContext.Default.AppSettings, cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: true);
            },
            cancellationToken);

    public Task<SkinCatalog> LoadSkinsAsync(CancellationToken cancellationToken = default) =>
        SkinCatalog.LoadAsync(SkinsDirectory, cancellationToken);

    public Task<KeymapFile> LoadKeymapAsync(CancellationToken cancellationToken = default) =>
        LoadOrCreateAsync(
            KeymapFileName,
            () => new KeymapFile { Bindings = new(DefaultKeymap.Bindings, StringComparer.OrdinalIgnoreCase) },
            ConfigJsonContext.Default.KeymapFile,
            cancellationToken);

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
