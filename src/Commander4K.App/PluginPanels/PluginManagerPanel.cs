using System.Diagnostics;
using Commander4K.App.Composition;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.App.PluginPanels;

/// <summary>
/// Plugin manager: runtimes, built-in and external plugins with their permissions and status, consent (enable /
/// disable), the audit log, help for missing runtimes and new plugins from templates.
/// </summary>
public sealed class PluginManagerPanel(AppHost app) : IPanelPlugin
{
    public const string PluginId = "builtin.plugin-manager";

    /// <summary>Example plugins embedded in the app, keyed by template name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "folder-stats-python",
        ["node"] = "site-check-node",
        ["powershell"] = "git-powershell",
        ["preview"] = "project-modules-python",
    };

    public string Id => PluginId;

    public string DisplayName => "Plugin manager";

    public PluginManifest Manifest { get; } = new(PluginId, Loc.T("Menedżer wtyczek"), "1.0.0", "4Kprojekt",
        Loc.T("Zarządzanie wtyczkami, zgodami na uprawnienia i dziennikiem audytu."), [PluginPermissions.FileRead]);

    public string Title => Loc.T("Menedżer wtyczek");

    public string Description => Loc.T("Wtyczki, środowiska (Node, Python, PowerShell, .NET), uprawnienia, zgody i dziennik audytu.");

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session(app, host);

    /// <summary>
    /// What the user is asked before enabling a plugin: name, version, author, runtime, each permission with its
    /// description and whether the code bypasses the SDK. Shared by the plugin manager and the MCP <c>enable_plugin</c> tool.
    /// </summary>
    public static async Task<string> ApprovalMessageAsync(DiscoveredPlugin plugin, CancellationToken cancellationToken)
    {
        var manifest = plugin.Manifest ?? throw new InvalidOperationException("The plugin has no valid manifest.");
        var permissions = manifest.Permissions.Count == 0
            ? Loc.T("żadnych (tylko interfejs)")
            : string.Join("\n", manifest.Permissions.Select(p => $"  • {p} — {Loc.T(PluginPermissions.Describe(p))}"));
        var bypass = await Task.Run(() => SafeScan(plugin.Folder), cancellationToken);
        var sdkNote = bypass.Count == 0
            ? Loc.T("Wtyczka działa jako osobny proces i wszystko robi przez API aplikacji, która sprawdza te uprawnienia i zapisuje każde wywołanie w dzienniku.")
            : Loc.T("⚠ UWAGA: kod omija SDK w {0} miejscach (np. {1}). Tych operacji aplikacja nie sprawdzi ani nie zapisze w dzienniku — włącz tylko, jeśli ufasz autorowi.", bypass.Count, bypass[0]);
        return Loc.T("„{0}” {1} ({2})\nŚrodowisko: {3}\n\nUprawnienia:\n{4}\n\n", manifest.Name, manifest.Version, manifest.Author, manifest.Runtime.Type, permissions) + sdkNote;
    }

    /// <summary>Records the user's consent, audits it and restarts plugins; returns why it still cannot run, if anything.</summary>
    public static async Task<string?> ApproveAsync(AppHost app, DiscoveredPlugin plugin)
    {
        var manifest = plugin.Manifest ?? throw new InvalidOperationException("The plugin has no valid manifest.");
        app.Approvals.Approve(manifest);
        app.Audit.Record(manifest.Id, "approve", string.Join(",", manifest.Permissions), AuditOutcome.Info);
        await app.ReloadExternalPluginsAsync();
        return app.PluginBlocker(plugin);
    }

    /// <summary>SDK lint of an installed plugin; an unreadable folder counts as clean (the manifest check reports it).</summary>
    private static IReadOnlyList<SourceFinding> SafeScan(string folder)
    {
        try
        {
            return PluginSourceCheck.ScanFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class Session(AppHost app, IPluginHost host) : IPanelSession
    {
        private static readonly ColumnDefinition[] Columns =
        [
            new("name", Loc.T("Nazwa"), ColumnAlignment.Left, 300),
            new("version", Loc.T("Wersja"), ColumnAlignment.Left, 90),
            new("kind", Loc.T("Rodzaj"), ColumnAlignment.Left, 120),
            new("status", Loc.T("Stan"), ColumnAlignment.Left, 420),
        ];

        public TimeSpan? RefreshInterval => null;

        public IReadOnlyList<PanelAction> Actions { get; } =
        [
            new("enable", Loc.T("Włącz (zgoda)"), "Ctrl+E"),
            new("install", Loc.T("Zainstaluj z katalogu"), "Ctrl+I"),
            new("catalog", Loc.T("Odśwież katalog"), "Ctrl+R", AppliesToRows: false),
            new("disable", Loc.T("Wyłącz"), "Ctrl+D"),
            new("log", Loc.T("Dziennik audytu"), "F3", AppliesToRows: false),
            new("template", Loc.T("Nowa wtyczka z przykładu"), "Ctrl+N", AppliesToRows: false),
            new("folder", Loc.T("Folder wtyczek"), "F7", AppliesToRows: false),
        ];

        public Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var rows = new List<PanelRow>
            {
                new("runtimes", null, [Loc.T("Środowiska wtyczek"), "", "", "Node.js, Python, PowerShell, .NET"], RowSeverity.Highlight, ExpandedByDefault: true),
            };
            foreach (var runtime in app.Runtimes.Where(r => r.Type != "exe"))
            {
                rows.Add(new PanelRow("runtime:" + runtime.Type, "runtimes",
                    [runtime.DisplayName, runtime.Version?.ToString() ?? "-", runtime.Type,
                        runtime.IsInstalled ? runtime.Executable! : "brak — " + RuntimeCatalog.InstallHint(runtime.Type)],
                    runtime.IsInstalled ? RowSeverity.Normal : RowSeverity.Warning));
            }

            rows.Add(new PanelRow("external", null, [Loc.T("Wtyczki zewnętrzne"), "", "", string.Join(" · ", app.PluginRoots)], RowSeverity.Highlight, ExpandedByDefault: true));
            foreach (var plugin in app.DiscoveredPlugins)
            {
                var blocker = app.PluginBlocker(plugin);
                var manifest = plugin.Manifest;
                rows.Add(new PanelRow("ext:" + plugin.Folder, "external",
                    [manifest?.Name ?? Path.GetFileName(plugin.Folder), manifest?.Version ?? "-", manifest?.Runtime.Type ?? "?", blocker ?? "Aktywna · " + Permissions(manifest!)],
                    blocker is null ? RowSeverity.Normal : plugin.IsValid ? RowSeverity.Warning : RowSeverity.Error));
            }

            if (app.DiscoveredPlugins.Count == 0)
            {
                rows.Add(new PanelRow("external:none", "external", [Loc.T("(brak — Ctrl+N tworzy wtyczkę z przykładu)"), "", "", ""], RowSeverity.Muted));
            }

            // Official plugins from the repository: install with Ctrl+I, then enable like any other plugin.
            rows.Add(new PanelRow("catalog", null, [Loc.T("Katalog oficjalnych wtyczek"), "", "", app.Settings.PluginCatalogUrl], RowSeverity.Highlight, ExpandedByDefault: true));
            foreach (var entry in app.PluginCatalog.Plugins)
            {
                var installed = app.DiscoveredPlugins.FirstOrDefault(p => string.Equals(p.Manifest?.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                var status = installed is null
                    ? Loc.T("do pobrania (Ctrl+I) · {0}", entry.LocalizedDescription)
                    : installed.Manifest!.Version != entry.Version
                        ? Loc.T("zainstalowana {0} — dostępna {1} (Ctrl+I aktualizuje)", installed.Manifest.Version, entry.Version)
                        : Loc.T("zainstalowana · {0}", entry.LocalizedDescription);
                rows.Add(new PanelRow("catalog:" + entry.Id, "catalog", [entry.Name, entry.Version, entry.Runtime, status],
                    installed is null ? RowSeverity.Muted : installed.Manifest!.Version != entry.Version ? RowSeverity.Warning : RowSeverity.Normal));
            }

            rows.Add(new PanelRow("builtin", null, [Loc.T("Wtyczki wbudowane"), "", "", Loc.T("w procesie aplikacji")], RowSeverity.Highlight));
            foreach (var plugin in BuiltIns())
            {
                rows.Add(new PanelRow("builtin:" + plugin.Id, "builtin",
                    [plugin.Manifest.Name, plugin.Manifest.Version, Loc.T("wbudowana"), Permissions(plugin.Manifest)], RowSeverity.Normal));
            }

            var active = app.ExternalPlugins.Count;
            return Task.FromResult(new PanelSnapshot(Columns, rows,
                Loc.T("{0} aktywnych wtyczek zewnętrznych z {1} · dziennik: {2}", active, app.DiscoveredPlugins.Count, Path.Combine(app.DataDirectory, "data", "logs"))));
        }

        public async Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken)
        {
            if (Find(rowId) is { Manifest: { } manifest } plugin)
            {
                var findings = await Task.Run(() => SafeScan(plugin.Folder), cancellationToken);
                var sdk = findings.Count == 0
                    ? [new KeyValuePair<string, string>("✓", Loc.T("kod korzysta z systemu tylko przez SDK"))]
                    : findings.Take(30).Select(f => new KeyValuePair<string, string>($"⚠ {f.File}:{f.Line}", $"{f.Api} → {f.Instead}")).ToList();
                var info = new List<KeyValuePair<string, string>>
                {
                    new("Id", manifest.Id),
                    new(Loc.T("Autor"), manifest.Author),
                    new(Loc.T("Opis"), manifest.Description),
                    new(Loc.T("Środowisko"), $"{manifest.Runtime.Type} {manifest.Runtime.Version} ({manifest.Runtime.Entry})"),
                    new("Folder", plugin.Folder),
                    new(Loc.T("Stan"), app.PluginBlocker(plugin) ?? Loc.T("aktywna")),
                };
                var permissions = manifest.Permissions.Select(p => new KeyValuePair<string, string>(p, Loc.T(PluginPermissions.Describe(p)))).ToList();
                var contributions = (manifest.Contributes?.Commands ?? []).Select(c => new KeyValuePair<string, string>(Loc.T("komenda"), $"{c.Category}: {c.Title}"))
                    .Concat((manifest.Contributes?.Panels ?? []).Select(p => new KeyValuePair<string, string>(Loc.T("panel"), p.Title)))
                    .Concat((manifest.Contributes?.Previews ?? []).Select(p => new KeyValuePair<string, string>(Loc.T("podgląd"), $"{p.Title} ({string.Join(", ", p.Extensions)})"))).ToList();
                return new PanelDetails(
                [
                    new(Loc.T("Wtyczka"), info),
                    new(Loc.T("Uprawnienia"), permissions.Count > 0 ? permissions : [new("—", "brak (tylko interfejs)")]),
                    new(Loc.T("Zgodność z SDK"), sdk),
                    new(Loc.T("Dodaje"), contributions),
                    new(Loc.T("Ostatnie wpisy dziennika"), Audit(manifest.Id, 20)),
                ]);
            }

            if (rowId.StartsWith("catalog:", StringComparison.Ordinal) && app.PluginCatalog.Find(rowId[8..]) is { } entry)
            {
                var info = new List<KeyValuePair<string, string>>
                {
                    new("Id", entry.Id),
                    new(Loc.T("Autor"), entry.Author),
                    new(Loc.T("Opis"), entry.LocalizedDescription),
                    new(Loc.T("Środowisko"), entry.Runtime),
                    new(Loc.T("Pliki"), string.Join(", ", entry.Files.Select(f => f.Path))),
                    new(Loc.T("Źródło"), app.PluginCatalog.Catalog.BaseUrl + entry.Path),
                };
                var sections = new List<PanelDetailSection> { new(Loc.T("Wtyczka z katalogu"), info) };
                if (entry.Example is { } example)
                {
                    sections.Add(new(Loc.T("Przykładowy plik .c4k"), [new(FolderConfigName, example)]));
                }

                return new PanelDetails(sections);
            }

            if (Find(rowId) is { } invalid)
            {
                return new PanelDetails([new(Loc.T("Błędy manifestu"), [.. invalid.Errors.Select(e => new KeyValuePair<string, string>("•", e))])]);
            }

            if (rowId.StartsWith("builtin:", StringComparison.Ordinal) && BuiltIns().FirstOrDefault(p => "builtin:" + p.Id == rowId) is { } builtIn)
            {
                return new PanelDetails(
                [
                    new(builtIn.Manifest.Name, [new("Id", builtIn.Id), new(Loc.T("Opis"), builtIn.Manifest.Description)]),
                    new(Loc.T("Uprawnienia"), [.. builtIn.Manifest.Permissions.Select(p => new KeyValuePair<string, string>(p, Loc.T(PluginPermissions.Describe(p))))]),
                    new(Loc.T("Ostatnie wpisy dziennika"), Audit(builtIn.Id, 20)),
                ]);
            }

            return null;
        }

        public async Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            var plugin = rowIds.Count > 0 ? Find(rowIds[0]) : null;
            switch (actionId)
            {
                case "enable" when plugin is { IsValid: true, Manifest: { } }:
                    if (!await host.ConfirmAsync(Loc.T("Włącz wtyczkę"), await ApprovalMessageAsync(plugin, cancellationToken)))
                    {
                        return null;
                    }

                    return await ApproveAsync(app, plugin) is { } still ? Loc.T("Zgoda zapisana, ale wtyczka nie może się uruchomić: {0}", still) : null;

                case "install" when rowIds.FirstOrDefault() is { } row && row.StartsWith("catalog:", StringComparison.Ordinal) && app.InstallFromCatalog is { } install:
                    await install(row[8..]);
                    return null;

                case "install":
                    return Loc.T("Zaznacz wtyczkę w grupie „Katalog oficjalnych wtyczek”.");

                case "catalog":
                    return await app.PluginCatalog.RefreshAsync(app.Settings.PluginCatalogUrl, cancellationToken) is { } error
                        ? Loc.T("Nie udało się pobrać katalogu: {0}", error)
                        : null;

                case "disable" when plugin?.Manifest is { } manifest:
                    app.Approvals.Disable(manifest.Id);
                    app.Audit.Record(manifest.Id, "disable", "wyłączona przez użytkownika", AuditOutcome.Info);
                    await app.ReloadExternalPluginsAsync();
                    return null;

                case "log":
                    var id = plugin?.Manifest?.Id ?? (rowIds.FirstOrDefault()?.StartsWith("builtin:", StringComparison.Ordinal) == true ? rowIds[0][8..] : null);
                    var entries = app.Audit.Recent(id);
                    return entries.Count == 0
                        ? Loc.T("Dziennik jest pusty.")
                        : Loc.T("Dziennik audytu{0} ({1} wpisów, najnowsze na dole):\n\n", (id is null ? "" : $" — {id}"), entries.Count) + string.Join('\n', entries);

                case "template":
                    var choice = await host.PromptAsync(Loc.T("Nowa wtyczka z przykładu"), Loc.T("Przykład: python, node, powershell albo preview (dedykowany podgląd plików, Python)"), "python");
                    if (choice is null || !Templates.TryGetValue(choice.Trim(), out var template))
                    {
                        return choice is null ? null : Loc.T("Dostępne przykłady: python, node, powershell, preview.");
                    }

                    var target = Path.Combine(app.DataDirectory, "plugins", template);
                    var written = ExtractTemplate(template, target);
                    await app.ReloadExternalPluginsAsync();
                    OpenUrl(target);
                    return Loc.T("Utworzono {0} ({1} plików). Wtyczka jest wyłączona — zaznacz ją i włącz (Ctrl+E). ", target, written) +
                        Loc.T("Opis protokołu: docs/plugins.md w repozytorium.");

                case "folder":
                    var folder = Path.Combine(app.DataDirectory, "plugins");
                    Directory.CreateDirectory(folder);
                    OpenUrl(folder);
                    return null;

                default:
                    return null;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private const string FolderConfigName = "folder" + Core.Folders.C4kReader.Extension;

        private DiscoveredPlugin? Find(string rowId) =>
            rowId.StartsWith("ext:", StringComparison.Ordinal) ? app.DiscoveredPlugins.FirstOrDefault(p => p.Folder == rowId[4..]) : null;

        private IEnumerable<IPlugin> BuiltIns() =>
            app.Router.Providers.Cast<IPlugin>()
                .Concat(app.BuiltInPanels)
                .Concat([app.FileSearch, app.Viewer])
                .Concat(app.BuiltInPreviews)
                .Concat(app.ColumnPlugins)
                .Append(Browser.BrowserPlugin.Instance)
                .DistinctBy(p => p.Id)
                .OrderBy(p => p.Manifest.Name, StringComparer.CurrentCulture);

        private List<KeyValuePair<string, string>> Audit(string pluginId, int count) =>
            [.. app.Audit.Recent(pluginId).TakeLast(count).Select(e => new KeyValuePair<string, string>(
                $"{e.Time.ToLocalTime():HH:mm:ss} {e.Outcome}", $"{e.Operation} {e.Detail}"))];

        private static string Permissions(PluginManifest manifest) =>
            manifest.Permissions.Count == 0 ? Loc.T("bez uprawnień") : string.Join(", ", manifest.Permissions);

        private static void OpenUrl(string target)
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }

        /// <summary>Writes an embedded example plugin (resources named examples/&lt;template&gt;/...) to disk.</summary>
        private static int ExtractTemplate(string template, string target)
        {
            var assembly = typeof(PluginManagerPanel).Assembly;
            var prefix = $"examples/{template}/";
            var count = 0;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                var relative = name.Replace('\\', '/')[prefix.Length..];
                var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var resource = assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(path);
                resource.CopyTo(file);
                count++;
            }

            if (count == 0)
            {
                throw new InvalidOperationException(Loc.T("Brak osadzonego przykładu {0}.", template));
            }

            return count;
        }
    }
}
