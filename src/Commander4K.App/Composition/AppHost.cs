using Commander4K.Core.Ai;
using Commander4K.Core.Configuration;
using Commander4K.Core.Execution;
using Commander4K.Core.Folders;
using Commander4K.Core.Indexing;
using Commander4K.Core.Input;
using Commander4K.Core.Jobs;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.Core.Security;
using Commander4K.Core.Theming;
using Commander4K.Core.Tools;
using Commander4K.Core.Updates;
using Commander4K.Core.Vfs;
using Commander4K.App.PluginPanels;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Archives;
using Commander4K.Plugins.BuiltIn.Columns;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Network;
using Commander4K.Plugins.BuiltIn.Registry;
using Commander4K.Plugins.BuiltIn.Remote;
using Commander4K.Plugins.BuiltIn.Search;
using Commander4K.Plugins.BuiltIn.Logs;
using Commander4K.Plugins.BuiltIn.PowerShell;
using Commander4K.Plugins.BuiltIn.Previews;
using Commander4K.Plugins.BuiltIn.SystemInfo;
using Commander4K.Plugins.BuiltIn.TaskManager;
using Commander4K.Plugins.BuiltIn.Viewers;

namespace Commander4K.App.Composition;

/// <summary>Forwards plugin UI calls to the window once it is attached; before that, calls are no-ops.</summary>
public sealed class PluginUiProxy : IPluginUi
{
    public IPluginUi? Target { get; set; }

    public string? ActivePath => Target?.ActivePath;

    public string? OtherPath => Target?.OtherPath;

    public IReadOnlyList<VfsEntry> SelectedEntries => Target?.SelectedEntries ?? [];

    public Task NavigateAsync(string path, string? focusName) => Target?.NavigateAsync(path, focusName) ?? Task.CompletedTask;

    public void Notify(string message, bool isError, string? target) => Target?.Notify(message, isError, target);

    public Task ShowTextAsync(string title, string text) => Target?.ShowTextAsync(title, text) ?? Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message) => Target?.ConfirmAsync(title, message) ?? Task.FromResult(false);

    public Task<string?> PromptAsync(string title, string message, string initial) => Target?.PromptAsync(title, message, initial) ?? Task.FromResult<string?>(null);

    public Task EditFileAsync(string path) => Target?.EditFileAsync(path) ?? Task.CompletedTask;

    public Task OpenPanelAsync(string panelPluginId, string? argument) => Target?.OpenPanelAsync(panelPluginId, argument) ?? Task.CompletedTask;

    public Task OpenUrlAsync(string url) => Target?.OpenUrlAsync(url) ?? Task.CompletedTask;

    public Task SearchWebAsync(string query, string? engine) => Target?.SearchWebAsync(query, engine) ?? Task.CompletedTask;

    public Task CopyTextAsync(string text) => Target?.CopyTextAsync(text) ?? Task.CompletedTask;
}

/// <param name="Category">Menu group for plugin commands (slash = submenu); <c>null</c> for built-ins.</param>
public sealed record CommandInfo(string Id, string Description, string? Category);

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
        // "This PC" (local://) lists the network root after drives and folders; hosts stream in as discovery finds them.
        Local.NetworkEntries = ct => Network.EnumerateAsync(NetworkNeighborhoodVfsPlugin.RootPath, ct);
        Router = new VfsRouter(Executor, [Local, Network, Registry, Remote, Archives]);
        Archives.Router = Router; // Archives open their container file through the router (any VFS).
        DirectorySizes = new DirectorySizeCalculator(Router);
        SearchProviders = [Registry, FileSearch];
        Updates = new UpdateService(Http);
        AiCatalog = new AiCatalogService(Http, Path.Combine(dataDirectory, "data", "ai-providers.json"), Path.Combine(config.ConfigDirectory, "ai", "providers.json"));
        PluginCatalog = new PluginCatalogService(Http, Path.Combine(dataDirectory, "data", "plugin-catalog.json"));
        FolderTrust = new FolderTrustStore(Path.Combine(config.ConfigDirectory, "trusted-folders.json"));
        Secrets = new Core.Security.SecretStore(Path.Combine(config.ConfigDirectory, "secrets.json"));
        AiCredentials = new AiCredentialStore(Path.Combine(config.ConfigDirectory, "ai", "credentials.json"));
        Ai = new AiRouter(AiCatalog, AiCredentials, new OpenAiCompatibleClient(Http), AiLogin.GitHubCliToken);
        _baseSettings = Core.Configuration.SettingsCatalog.Create(
            () => [.. Skins.Skins.Select(s => new SettingChoice(s.Id, s.DisplayName))],
            () => [new(Loc.Auto, Loc.T("Automatycznie")), .. Loc.AvailableLanguages(config.ConfigDirectory).Select(code => new SettingChoice(code, LanguageName(code)))]);
        PowerShell = new PowerShellCatalogPlugin(Path.Combine(config.ConfigDirectory, "powershell.json"));
        Audit = new AuditLog(Path.Combine(dataDirectory, "data", "logs"));
        Approvals = new PluginApprovalStore(Path.Combine(config.ConfigDirectory, "plugin-approvals.json"));
        Mail = new Core.Mail.MailService(
            new Core.Mail.MailCredentials(Secrets, (realm, user) => MailPasswordPrompt?.Invoke(realm, user) ?? Task.FromResult<string?>(null)),
            new Core.Mail.MailCertificateStore(Path.Combine(config.ConfigDirectory, "mail_certificates.json"),
                (hostPort, fingerprint) => MailCertificatePrompt?.Invoke(hostPort, fingerprint) ?? Task.FromResult(false)),
            warning: message => Audit.Record("mail", "mail.warning", message, AuditOutcome.Failed));
        BuiltInPanels = [new SystemInfoPanelPlugin(), new TaskManagerPanelPlugin(), new LogAnalyzerPanelPlugin(), PowerShell, new PluginManagerPanel(this)];
        PanelPlugins = BuiltInPanels;
        CommandPlugins = [PowerShell];
        BuiltInPreviews = [new MarkdownPreviewPlugin(), new CanvasPreviewPlugin(), new ImagePreviewPlugin(), new FolderPreviewPlugin(), new TextPreviewPlugin()];
        PreviewPlugins = BuiltInPreviews;
        ApplySettings();
    }

    /// <summary>Receives plugin UI calls; the window plugs itself in once it exists.</summary>
    public PluginUiProxy PluginUi { get; } = new();

    /// <summary>IMAP/SMTP behind the <c>mail</c> permission; passwords and certificate trust stay in the app.</summary>
    public Core.Mail.MailService Mail { get; }

    /// <summary>(realm text, user) → password or <c>null</c>; the window shows the dialog (called from background threads).</summary>
    public Func<string, string, Task<string?>>? MailPasswordPrompt { get; set; }

    /// <summary>(host:port, SHA-256 fingerprint) → trust; the window shows the dialog (called from background threads).</summary>
    public Func<string, string, Task<bool>>? MailCertificatePrompt { get; set; }

    public AuditLog Audit { get; }

    public PluginApprovalStore Approvals { get; }

    /// <summary>Installed plugin runtimes (Node, Python, PowerShell, .NET) detected at start-up.</summary>
    public IReadOnlyList<RuntimeInfo> Runtimes { get; private set; } = [];

    /// <summary>Every <c>plugin.json</c> found, enabled or not, valid or not (for the plugin manager).</summary>
    public IReadOnlyList<DiscoveredPlugin> DiscoveredPlugins { get; private set; } = [];

    /// <summary>Running external plugins (approved, valid, runtime available).</summary>
    public IReadOnlyList<ExternalPlugin> ExternalPlugins { get; private set; } = [];

    public IReadOnlyList<IPanelPlugin> BuiltInPanels { get; }

    public IReadOnlyList<IPreviewPlugin> BuiltInPreviews { get; }

    /// <summary>Quick-view renderers (built-in and external); the preview picks the highest priority that accepts an entry.</summary>
    public IReadOnlyList<IPreviewPlugin> PreviewPlugins { get; private set; }

    /// <summary>Folders searched for external plugins: next to the exe and in the data folder.</summary>
    public IReadOnlyList<string> PluginRoots =>
        [.. new[] { Path.Combine(AppContext.BaseDirectory, "plugins"), Path.Combine(DataDirectory, "plugins") }.Distinct(StringComparer.OrdinalIgnoreCase)];

    private readonly Dictionary<string, ScopedPluginHost> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The API instance for one plugin: permission checks against its manifest and audit entries under its id. The only
    /// way the window hands a host to plugin code.
    /// </summary>
    /// <summary>"Switch this folder to the gallery?": which preview suits the folder under the cursor.</summary>
    public FolderSuggestionService FolderSuggestions
    {
        get => field ??= CreateFolderSuggestions(new FolderSuggestionMemory(null));
        private set;
    }

    private FolderSuggestionService CreateFolderSuggestions(FolderSuggestionMemory memory) =>
        new(() => PreviewPlugins, preview => HostFor(preview.Manifest), (path, ct) => Router.OpenListing(path, ct).ReadAllAsync(ct), memory);

    /// <summary>Declines and the tour counter from <c>data/folder-suggestions.json</c> (start-up, off the UI thread).</summary>
    public async Task LoadFolderSuggestionsAsync(CancellationToken cancellationToken) =>
        FolderSuggestions = CreateFolderSuggestions(
            await FolderSuggestionMemory.LoadAsync(Path.Combine(DataDirectory, "data", "folder-suggestions.json"), cancellationToken).ConfigureAwait(false));

    public IPluginHost HostFor(PluginManifest manifest)
    {
        lock (_hosts)
        {
            if (!_hosts.TryGetValue(manifest.Id, out var host))
            {
                host = new ScopedPluginHost(manifest, PluginUi, Router, PluginHttp, Audit,
                    () => SystemSnapshots.Processes(Plugins.BuiltIn.Interop.ProcessLookups.ParentIds, Plugins.BuiltIn.Interop.ProcessLookups.ImagePath),
                    Mail);
                _hosts[manifest.Id] = host;
            }

            return host;
        }
    }

    /// <summary>Why a discovered plugin is not running, or <c>null</c> when it is.</summary>
    public string? PluginBlocker(DiscoveredPlugin plugin)
    {
        if (!plugin.IsValid)
        {
            return string.Join(" ", plugin.Errors);
        }

        if (RuntimeCatalog.CheckRequirement(plugin.Manifest!.Runtime, Runtimes) is { } missing)
        {
            return missing;
        }

        return Approvals.IsApproved(plugin.Manifest) ? null : Loc.T("Wyłączona — wymaga zgody na uprawnienia.");
    }

    /// <summary>Re-scans plugin folders and (re)starts approved plugins; call after enabling or installing one.</summary>
    public async Task ReloadExternalPluginsAsync()
    {
        await Task.Run(Background.Stop);
        foreach (var running in ExternalPlugins)
        {
            await running.DisposeAsync();
        }

        var discovered = await Task.Run(() => ManifestReader.Discover(PluginRoots));
        var external = new List<ExternalPlugin>();
        foreach (var plugin in discovered.Where(p => PluginBlocker(p) is null))
        {
            var runtime = Runtimes.First(r => string.Equals(r.Type, plugin.Manifest!.Runtime.Type, StringComparison.OrdinalIgnoreCase));
            external.Add(new ExternalPlugin(plugin, runtime, (ScopedPluginHost)HostFor(plugin.Manifest!), Audit, PluginData));
        }

        DiscoveredPlugins = discovered;
        ExternalPlugins = external;
        PanelPlugins = [.. BuiltInPanels, .. external.SelectMany(e => (e.Manifest.Contributes?.Panels ?? []).Select(p => (IPanelPlugin)new ExternalPanelPlugin(e, p)))];
        CommandPlugins = [PowerShell, .. external.Where(e => e.Manifest.Contributes?.Commands is { Count: > 0 }).Select(e => (ICommandPlugin)new ExternalCommandPlugin(e))];
        PreviewPlugins = [.. BuiltInPreviews, .. external.SelectMany(e => (e.Manifest.Contributes?.Previews ?? []).Select(p => (IPreviewPlugin)new ExternalPreviewPlugin(e, p)))];
        Background.Restart(external);
        PluginsChanged?.Invoke();
    }

    /// <summary>Periodic <c>background.tick</c> for approved plugins; follows <see cref="AppSettings.PluginBackground"/>.</summary>
    public PluginBackgroundService Background => _background ??= new PluginBackgroundService(() => Settings.PluginBackground, Audit);

    private PluginBackgroundService? _background;

    /// <summary>Plugins' own settings and data (<c>storage.get/set</c>), in <c>data/plugin-data</c>.</summary>
    public PluginStorage PluginData => _pluginData ??= new PluginStorage(Path.Combine(DataDirectory, "data", "plugin-data"));

    private PluginStorage? _pluginData;

    /// <summary>Raised (on a background thread) after <see cref="ReloadExternalPluginsAsync"/>.</summary>
    public event Action? PluginsChanged;

    public PowerShellCatalogPlugin PowerShell { get; }

    /// <summary>Panel modes (built-in and from the plugins folder).</summary>
    public IReadOnlyList<IPanelPlugin> PanelPlugins { get; private set; }

    /// <summary>Menu/palette operations (built-in and from the plugins folder).</summary>
    public IReadOnlyList<ICommandPlugin> CommandPlugins { get; private set; }

    public IPanelPlugin? FindPanel(string pluginId) =>
        PanelPlugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    public ICommandPlugin? FindCommandPlugin(string pluginId) =>
        CommandPlugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every executable command — built-ins, panel openers (<c>panel:id</c>) and plugin commands (<c>plugin:id:cmd</c>) —
    /// for the menu, command palette, help and the key-binding editor.
    /// </summary>
    public IReadOnlyList<CommandInfo> ListCommands()
    {
        var commands = Commands.Commands.Select(c => new CommandInfo(c.Id, c.Description, null)).ToList();
        commands.AddRange(PanelPlugins.Select(p => new CommandInfo(CommandIds.PanelPrefix + p.Id, "Panel: " + p.Title, Loc.T("Panele"))));
        foreach (var plugin in CommandPlugins)
        {
            commands.AddRange(plugin.Commands.Select(c =>
                new CommandInfo($"{CommandIds.PluginPrefix}{plugin.Id}:{c.Id}", $"{c.Category.Replace('/', ' ')}: {c.Title}", c.Category)));
        }

        return commands;
    }

    /// <summary>Shared client for GitHub release checks and downloads.</summary>
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Plugins' <c>net.*</c> calls. No client timeout: <see cref="ScopedPluginHost"/> applies its own 10-minute limit to
    /// every request except streamed file uploads, which may take longer and end with their job.
    /// </summary>
    public HttpClient PluginHttp { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    public UpdateService Updates { get; }

    /// <summary>AI providers: the live list, signed-in keys (<c>config/ai</c>) and the rotating router that asks them.</summary>
    public AiCatalogService AiCatalog { get; }

    public AiCredentialStore AiCredentials { get; }

    /// <summary>Official plugins from the repository (<c>plugins-catalog/catalog.json</c>), installable from the plugin manager and from <c>c4k.dir</c>.</summary>
    public PluginCatalogService PluginCatalog { get; }

    /// <summary>Downloads and installs a catalog plugin with the window's consent dialogs (set by the window).</summary>
    public Func<string, Task>? InstallFromCatalog { get; set; }

    /// <summary><c>c4k.dir</c> files whose scripts the user allowed.</summary>
    public FolderTrustStore FolderTrust { get; }

    /// <summary>The folder's <c>c4k.dir</c> when the setting is on; <c>null</c> otherwise or when there is none.</summary>
    public Task<FolderConfig?> ReadFolderConfigAsync(string folder, CancellationToken cancellationToken) =>
        Settings.FolderConfigs ? FolderConfig.ReadAsync(Router, folder, TimeSpan.FromSeconds(2), cancellationToken) : Task.FromResult<FolderConfig?>(null);

    /// <summary>Tokens that <c>c4k.dir</c> requests name as <c>{secret:name}</c> (<c>config/secrets.json</c>, DPAPI).</summary>
    public Core.Security.SecretStore Secrets { get; }

    /// <summary>API cards and the API view of <c>c4k.dir</c> dashboards.</summary>
    public Plugins.BuiltIn.Dashboard.ApiCards ApiCards => _apiCards ??= new(Secrets);

    private Plugins.BuiltIn.Dashboard.ApiCards? _apiCards;

    /// <summary>SSH monitor cards; keeps one SSH session per server while a dashboard shows it.</summary>
    public Plugins.BuiltIn.Remote.SshMonitor SshMonitor => _sshMonitor ??= new(Remote, Audit);

    private Plugins.BuiltIn.Remote.SshMonitor? _sshMonitor;

    /// <summary>Card sources for a dashboard, over the preview plugins loaded right now.</summary>
    public Plugins.BuiltIn.Dashboard.CardResolver CreateCardResolver() =>
        new(PreviewPlugins, HostFor, ReadFolderConfigAsync, id => PluginCatalog.Find(id) is not null, ApiCards, SshMonitor);

    public AiRouter Ai { get; }

    private readonly IReadOnlyList<SettingDescriptor> _baseSettings;

    /// <summary>Static settings plus those registered at runtime (palette positions).</summary>
    public IReadOnlyList<SettingDescriptor> SettingsCatalog => [.. _baseSettings, .. ExtraSettings];

    /// <summary>Settings contributed by the window (one on/off switch per plugin column).</summary>
    public IReadOnlyList<SettingDescriptor> ExtraSettings { get; set; } = [];

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

    public ArchiveVfsPlugin Archives { get; } = new();

    /// <summary>Extra file-list columns (WDX), switched on per column in the settings.</summary>
    public IReadOnlyList<IColumnMetadataPlugin> ColumnPlugins { get; } =
        [new GitStatusColumnPlugin(), new ImageInfoColumnPlugin(), new HashColumnPlugin()];

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

                // Normally done in Program.Main before the first window; tools and tests that only create a host get it here.
                if (!Loc.IsInitialized)
                {
                    Loc.Initialize(settingsTask.Result.Language, config.ConfigDirectory);
                }

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
                host.AiCatalog.Load();
                host.PluginCatalog.Load();
                _ = Task.Run(() => host.PluginCatalog.RefreshAsync(host.Settings.PluginCatalogUrl), CancellationToken.None);
                _ = host.Ai.States(); // Reads credentials and asks the GitHub CLI here, never later on the UI thread.
                _ = Task.Run(() => host.AiCatalog.RefreshAsync(host.Settings.AiCatalogUrl), CancellationToken.None); // Live list; the cached one serves meanwhile.
                await host.PowerShell.ReloadAsync(cancellationToken).ConfigureAwait(false);
                await host.LoadFolderSuggestionsAsync(cancellationToken).ConfigureAwait(false);
                host.Runtimes = await RuntimeCatalog.DetectAsync(cancellationToken).ConfigureAwait(false);
                await host.ReloadExternalPluginsAsync().ConfigureAwait(false);
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

        // Saved SFTP/FTP logins (password or key + passphrase); the secret is decrypted only in memory.
        foreach (var connection in Settings.Connections.Where(c => c.Auth is not null && VfsPath.GetScheme(c.Path) is "sftp" or "ftp"))
        {
            var secret = SecretProtector.Unprotect(connection.Secret);
            Remote.SetLogin(connection.Path, connection.Auth == "key"
                ? new RemoteLogin(connection.UserName ?? Environment.UserName, PrivateKeyFile: connection.KeyFile, Passphrase: secret)
                : new RemoteLogin(connection.UserName ?? Environment.UserName, Password: secret));
        }
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

    /// <summary>The MCP server, set by the window; disposed first, so no tool call runs while the rest shuts down.</summary>
    public IAsyncDisposable? Mcp { get; set; }

    public async ValueTask DisposeAsync()
    {
        _background?.Stop();
        if (Mcp is not null)
        {
            await Mcp.DisposeAsync();
        }

        foreach (var plugin in ExternalPlugins)
        {
            await plugin.DisposeAsync();
        }

        await Jobs.DisposeAsync();
        if (_sshMonitor is not null)
        {
            await _sshMonitor.DisposeAsync();
        }

        await Remote.DisposeAsync();
        await Mail.DisposeAsync();
        await Audit.DisposeAsync();
        Http.Dispose();
        PluginHttp.Dispose();
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

    /// <summary>Native name of a language code ("de" → "Deutsch"), or the code itself when .NET does not know it.</summary>
    public static string LanguageName(string code)
    {
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(code);
            var name = culture.NativeName;
            return culture.CultureTypes.HasFlag(System.Globalization.CultureTypes.UserCustomCulture) || name == code
                ? code
                : char.ToUpper(name[0], culture) + name[1..];
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Portable mode next to the executable when writable, otherwise %AppData%\Commander4K.</summary>
    public static string ResolveDataDirectory()
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
