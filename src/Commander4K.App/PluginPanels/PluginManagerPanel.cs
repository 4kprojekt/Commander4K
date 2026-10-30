using System.Diagnostics;
using System.Text;
using Commander4K.App.Composition;
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
    };

    public string Id => PluginId;

    public string DisplayName => "Plugin manager";

    public PluginManifest Manifest { get; } = new(PluginId, "Menedżer wtyczek", "1.0.0", "4Kprojekt",
        "Zarządzanie wtyczkami, zgodami na uprawnienia i dziennikiem audytu.", [PluginPermissions.FileRead]);

    public string Title => "Menedżer wtyczek";

    public string Description => "Wtyczki, środowiska (Node, Python, PowerShell, .NET), uprawnienia, zgody i dziennik audytu.";

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session(app, host);

    private sealed class Session(AppHost app, IPluginHost host) : IPanelSession
    {
        private static readonly ColumnDefinition[] Columns =
        [
            new("name", "Nazwa", ColumnAlignment.Left, 300),
            new("version", "Wersja", ColumnAlignment.Left, 90),
            new("kind", "Rodzaj", ColumnAlignment.Left, 120),
            new("status", "Stan", ColumnAlignment.Left, 420),
        ];

        public TimeSpan? RefreshInterval => null;

        public IReadOnlyList<PanelAction> Actions { get; } =
        [
            new("enable", "Włącz (zgoda)", "Ctrl+E"),
            new("disable", "Wyłącz", "Ctrl+D"),
            new("log", "Dziennik audytu", "F3", AppliesToRows: false),
            new("search", "Szukaj rozwiązania", "Ctrl+F"),
            new("template", "Nowa wtyczka z przykładu", "Ctrl+N", AppliesToRows: false),
            new("folder", "Folder wtyczek", "F7", AppliesToRows: false),
        ];

        public Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var rows = new List<PanelRow>
            {
                new("runtimes", null, ["Środowiska wtyczek", "", "", "Node.js, Python, PowerShell, .NET"], RowSeverity.Highlight, ExpandedByDefault: true),
            };
            foreach (var runtime in app.Runtimes.Where(r => r.Type != "exe"))
            {
                rows.Add(new PanelRow("runtime:" + runtime.Type, "runtimes",
                    [runtime.DisplayName, runtime.Version?.ToString() ?? "-", runtime.Type,
                        runtime.IsInstalled ? runtime.Executable! : "brak — " + RuntimeCatalog.InstallHint(runtime.Type)],
                    runtime.IsInstalled ? RowSeverity.Normal : RowSeverity.Warning));
            }

            rows.Add(new PanelRow("external", null, ["Wtyczki zewnętrzne", "", "", string.Join(" · ", app.PluginRoots)], RowSeverity.Highlight, ExpandedByDefault: true));
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
                rows.Add(new PanelRow("external:none", "external", ["(brak — Ctrl+N tworzy wtyczkę z przykładu)", "", "", ""], RowSeverity.Muted));
            }

            rows.Add(new PanelRow("builtin", null, ["Wtyczki wbudowane", "", "", "w procesie aplikacji"], RowSeverity.Highlight));
            foreach (var plugin in BuiltIns())
            {
                rows.Add(new PanelRow("builtin:" + plugin.Id, "builtin",
                    [plugin.Manifest.Name, plugin.Manifest.Version, "wbudowana", Permissions(plugin.Manifest)], RowSeverity.Normal));
            }

            var active = app.ExternalPlugins.Count;
            return Task.FromResult(new PanelSnapshot(Columns, rows,
                $"{active} aktywnych wtyczek zewnętrznych z {app.DiscoveredPlugins.Count} · dziennik: {Path.Combine(app.DataDirectory, "data", "logs")}"));
        }

        public Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken)
        {
            if (Find(rowId) is { Manifest: { } manifest } plugin)
            {
                var info = new List<KeyValuePair<string, string>>
                {
                    new("Id", manifest.Id),
                    new("Autor", manifest.Author),
                    new("Opis", manifest.Description),
                    new("Środowisko", $"{manifest.Runtime.Type} {manifest.Runtime.Version} ({manifest.Runtime.Entry})"),
                    new("Folder", plugin.Folder),
                    new("Stan", app.PluginBlocker(plugin) ?? "aktywna"),
                };
                var permissions = manifest.Permissions.Select(p => new KeyValuePair<string, string>(p, PluginPermissions.Describe(p))).ToList();
                var contributions = (manifest.Contributes?.Commands ?? []).Select(c => new KeyValuePair<string, string>("komenda", $"{c.Category}: {c.Title}"))
                    .Concat((manifest.Contributes?.Panels ?? []).Select(p => new KeyValuePair<string, string>("panel", p.Title))).ToList();
                return Task.FromResult<PanelDetails?>(new PanelDetails(
                [
                    new("Wtyczka", info),
                    new("Uprawnienia", permissions.Count > 0 ? permissions : [new("—", "brak (tylko interfejs)")]),
                    new("Dodaje", contributions),
                    new("Ostatnie wpisy dziennika", Audit(manifest.Id, 20)),
                ]));
            }

            if (Find(rowId) is { } invalid)
            {
                return Task.FromResult<PanelDetails?>(new PanelDetails([new("Błędy manifestu", [.. invalid.Errors.Select(e => new KeyValuePair<string, string>("•", e))])]));
            }

            if (rowId.StartsWith("builtin:", StringComparison.Ordinal) && BuiltIns().FirstOrDefault(p => "builtin:" + p.Id == rowId) is { } builtIn)
            {
                return Task.FromResult<PanelDetails?>(new PanelDetails(
                [
                    new(builtIn.Manifest.Name, [new("Id", builtIn.Id), new("Opis", builtIn.Manifest.Description)]),
                    new("Uprawnienia", [.. builtIn.Manifest.Permissions.Select(p => new KeyValuePair<string, string>(p, PluginPermissions.Describe(p)))]),
                    new("Ostatnie wpisy dziennika", Audit(builtIn.Id, 20)),
                ]));
            }

            return Task.FromResult<PanelDetails?>(null);
        }

        public async Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            var plugin = rowIds.Count > 0 ? Find(rowIds[0]) : null;
            switch (actionId)
            {
                case "enable" when plugin is { IsValid: true, Manifest: { } manifest }:
                    var permissions = manifest.Permissions.Count == 0
                        ? "żadnych (tylko interfejs)"
                        : string.Join("\n", manifest.Permissions.Select(p => $"  • {p} — {PluginPermissions.Describe(p)}"));
                    if (!await host.ConfirmAsync("Włącz wtyczkę",
                            $"„{manifest.Name}” {manifest.Version} ({manifest.Author})\nŚrodowisko: {manifest.Runtime.Type}\n\nUprawnienia:\n{permissions}\n\n" +
                            "Wtyczka działa jako osobny proces i wszystko robi przez API aplikacji, która sprawdza te uprawnienia i zapisuje każde wywołanie w dzienniku."))
                    {
                        return null;
                    }

                    app.Approvals.Approve(manifest);
                    app.Audit.Record(manifest.Id, "approve", string.Join(",", manifest.Permissions), AuditOutcome.Info);
                    await app.ReloadExternalPluginsAsync();
                    return app.PluginBlocker(plugin) is { } still ? $"Zgoda zapisana, ale wtyczka nie może się uruchomić: {still}" : null;

                case "disable" when plugin?.Manifest is { } manifest:
                    app.Approvals.Disable(manifest.Id);
                    app.Audit.Record(manifest.Id, "disable", "wyłączona przez użytkownika", AuditOutcome.Info);
                    await app.ReloadExternalPluginsAsync();
                    return null;

                case "log":
                    var id = plugin?.Manifest?.Id ?? (rowIds.FirstOrDefault()?.StartsWith("builtin:", StringComparison.Ordinal) == true ? rowIds[0][8..] : null);
                    var entries = app.Audit.Recent(id);
                    return entries.Count == 0
                        ? "Dziennik jest pusty."
                        : $"Dziennik audytu{(id is null ? "" : $" — {id}")} ({entries.Count} wpisów, najnowsze na dole):\n\n" + string.Join('\n', entries);

                case "search":
                    var problem = plugin is not null ? app.PluginBlocker(plugin) : rowIds.FirstOrDefault()?.StartsWith("runtime:", StringComparison.Ordinal) == true
                        ? $"install {rowIds[0][8..]} Windows 11"
                        : null;
                    if (string.IsNullOrWhiteSpace(problem))
                    {
                        return "Wybierz wtyczkę lub środowisko z problemem.";
                    }

                    OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(problem));
                    return null;

                case "template":
                    var choice = await host.PromptAsync("Nowa wtyczka z przykładu", "Język przykładu: python, node albo powershell", "python");
                    if (choice is null || !Templates.TryGetValue(choice.Trim(), out var template))
                    {
                        return choice is null ? null : "Dostępne przykłady: python, node, powershell.";
                    }

                    var target = Path.Combine(app.DataDirectory, "plugins", template);
                    var written = ExtractTemplate(template, target);
                    await app.ReloadExternalPluginsAsync();
                    OpenUrl(target);
                    return $"Utworzono {target} ({written} plików). Wtyczka jest wyłączona — zaznacz ją i włącz (Ctrl+E). " +
                        "Opis protokołu: docs/plugins.md w repozytorium.";

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

        private DiscoveredPlugin? Find(string rowId) =>
            rowId.StartsWith("ext:", StringComparison.Ordinal) ? app.DiscoveredPlugins.FirstOrDefault(p => p.Folder == rowId[4..]) : null;

        private IEnumerable<IPlugin> BuiltIns() =>
            app.Router.Providers.Cast<IPlugin>()
                .Concat(app.BuiltInPanels)
                .Concat([app.FileSearch, app.Viewer])
                .DistinctBy(p => p.Id)
                .OrderBy(p => p.Manifest.Name, StringComparer.CurrentCulture);

        private List<KeyValuePair<string, string>> Audit(string pluginId, int count) =>
            [.. app.Audit.Recent(pluginId).TakeLast(count).Select(e => new KeyValuePair<string, string>(
                $"{e.Time.ToLocalTime():HH:mm:ss} {e.Outcome}", $"{e.Operation} {e.Detail}"))];

        private static string Permissions(PluginManifest manifest) =>
            manifest.Permissions.Count == 0 ? "bez uprawnień" : string.Join(", ", manifest.Permissions);

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
                throw new InvalidOperationException($"Brak osadzonego przykładu {template}.");
            }

            return count;
        }
    }
}
