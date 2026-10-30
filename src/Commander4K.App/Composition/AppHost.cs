using Commander4K.Core.Configuration;
using Commander4K.Core.Execution;
using Commander4K.Core.Indexing;
using Commander4K.Core.Input;
using Commander4K.Core.Jobs;
using Commander4K.Core.Security;
using Commander4K.Core.Theming;
using Commander4K.Core.Tools;
using Commander4K.Core.Updates;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Network;
using Commander4K.Plugins.BuiltIn.Registry;
using Commander4K.Plugins.BuiltIn.Remote;
using Commander4K.Plugins.BuiltIn.Search;
using Commander4K.Plugins.BuiltIn.Viewers;

namespace Commander4K.App.Composition;

/// <summary>
/// Composition root. Everything is created by <see cref="CreateAsync"/> on background threads, so the window can
/// appear before configuration, SQLite and plugins are ready.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(string dataDirectory, ConfigStore config, AppSettings settings, SkinCatalog skins,
        KeymapEngine keymap, ExternalToolsService tools, SqliteIndexService? index)
    {
        DataDirectory = dataDirectory;
        Config = config;
        Settings = settings;
        Skins = skins;
        Keymap = keymap;
        Tools = tools;
        Index = index;
        KnownHosts = new KnownHostsStore(Path.Combine(config.ConfigDirectory, "known_hosts.json"));

        Network = new NetworkNeighborhoodVfsPlugin(index);
        Router = new VfsRouter(Executor, [Local, Network, Registry, Remote]);
        DirectorySizes = new DirectorySizeCalculator(Router);
        SearchProviders = [Registry, FileSearch];
        Updates = new UpdateService(Http);
        SettingsCatalog = Core.Configuration.SettingsCatalog.Create(
            () => [.. Skins.Skins.Select(s => new SettingChoice(s.Id, s.DisplayName))]);
        ApplySettings();
    }

    /// <summary>Shared client for GitHub release checks and downloads.</summary>
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(10) };

    public UpdateService Updates { get; }

    public IReadOnlyList<SettingDescriptor> SettingsCatalog { get; }

    public string DataDirectory { get; }

    public ConfigStore Config { get; }

    public AppSettings Settings { get; }

    public SkinCatalog Skins { get; private set; }

    public KeymapEngine Keymap { get; private set; }

    /// <summary>Key sequence → command id as stored in <c>keymap.json</c> (the editable form of <see cref="Keymap"/>).</summary>
    public Dictionary<string, string> KeyBindings { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public CommandRegistry Commands { get; } = new();

    public ExternalToolsService Tools { get; }

    /// <summary><c>null</c> when the database could not be opened; every feature then runs uncached.</summary>
    public SqliteIndexService? Index { get; }

    public KnownHostsStore KnownHosts { get; }

    public ResilientAsyncExecutor Executor { get; } = new();

    public VfsRouter Router { get; }

    public JobQueueManager Jobs { get; } = new();

    public DirectorySizeCalculator DirectorySizes { get; }

    public LocalFileSystemPlugin Local { get; } = new();

    public NetworkNeighborhoodVfsPlugin Network { get; }

    public WindowsRegistryVfsPlugin Registry { get; } = new();

    public RemoteTransferVfsPlugin Remote { get; } = new();

    public LocalFileSearchPlugin FileSearch { get; } = new();

    public IViewerPlugin Viewer { get; } = new TextHexViewerPlugin();

    public IReadOnlyList<ISearchProviderPlugin> SearchProviders { get; }

    public static Task<AppHost> CreateAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            async () =>
            {
                var dataDirectory = ResolveDataDirectory();
                var config = new ConfigStore(Path.Combine(dataDirectory, "config"));

                var settingsTask = config.LoadSettingsAsync(cancellationToken);
                var keymapTask = config.LoadKeymapAsync(cancellationToken);
                var toolsTask = config.LoadToolsAsync(cancellationToken);
                var skinsTask = config.LoadSkinsAsync(cancellationToken);
                var indexTask = OpenIndexAsync(Path.Combine(dataDirectory, "data", "index.db"), cancellationToken);
                await Task.WhenAll(settingsTask, keymapTask, toolsTask, skinsTask, indexTask).ConfigureAwait(false);

                // Kept in sync with the model on every start so editors and Claude always see the current schema.
                Directory.CreateDirectory(config.SkinsDirectory);
                await File.WriteAllTextAsync(Path.Combine(config.SkinsDirectory, SkinSchema.FileName), SkinSchema.CreateJson(), cancellationToken)
                    .ConfigureAwait(false);

                var host = new AppHost(
                    dataDirectory,
                    config,
                    settingsTask.Result,
                    skinsTask.Result,
                    new KeymapEngine(keymapTask.Result.Bindings),
                    new ExternalToolsService(toolsTask.Result),
                    indexTask.Result);
                host.KeyBindings = new Dictionary<string, string>(keymapTask.Result.Bindings, StringComparer.OrdinalIgnoreCase);
                return host;
            },
            cancellationToken);

    public async Task ReloadSkinsAsync(CancellationToken cancellationToken = default) =>
        Skins = await Config.LoadSkinsAsync(cancellationToken);

    public Task SaveSettingsAsync() => Config.SaveSettingsAsync(Settings);

    /// <summary>Pushes settings that live inside plugins (saved connections, LAN discovery).</summary>
    public void ApplySettings()
    {
        Network.SavedConnections = [.. Settings.Connections.Select(c => new SavedConnection(c.Name, c.Path))];
        Network.LanDiscoveryEnabled = Settings.LanDiscovery;
    }

    /// <summary>Validates, persists and activates new key bindings; throws <see cref="FormatException"/> on a bad sequence.</summary>
    public async Task SaveKeyBindingsAsync(Dictionary<string, string> bindings)
    {
        var engine = new KeymapEngine(bindings);
        await Config.SaveKeymapAsync(new KeymapFile { Bindings = bindings });
        KeyBindings = bindings;
        Keymap = engine;
    }

    public ISearchProviderPlugin? FindSearchProvider(string rootPath) =>
        SearchProviders.FirstOrDefault(p => p.CanSearch(rootPath));

    public async ValueTask DisposeAsync()
    {
        await Jobs.DisposeAsync();
        await Remote.DisposeAsync();
        Http.Dispose();
        if (Index is not null)
        {
            await Index.DisposeAsync();
        }
    }

    private static async Task<SqliteIndexService?> OpenIndexAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await SqliteIndexService.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceError($"Index unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Portable mode next to the executable when writable, otherwise %AppData%\Commander4K.</summary>
    private static string ResolveDataDirectory()
    {
        var portable = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return portable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Commander4K");
        }
    }
}
