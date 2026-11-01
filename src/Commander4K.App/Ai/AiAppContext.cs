using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Panels;
using Commander4K.App.PluginPanels;
using Commander4K.Core.Plugins;
using Commander4K.Core.Updates;
using Commander4K.PluginSdk;

namespace Commander4K.App.Ai;

/// <summary>
/// What AI assistants are told about the running app: both panels (path, mode, cursor, marks, a sample of the
/// listing), plugin runtimes and installed plugins — plus the plugin guide, an example and the SDK clients for
/// writing plugins. Shared by the in-app chat (system prompt and the <c>get_context</c> tool) and the browser panel
/// (typed into Gemini/ChatGPT/Claude on request). The text is English on purpose: it is a prompt, not UI.
/// </summary>
public sealed class AiAppContext(AppHost host, Func<(FilePanelView Active, FilePanelView Other, bool ActiveIsLeft)> panels)
{
    private const int ListingSample = 40;
    private const int MarkedSample = 50;

    public AppHost Host => host;

    /// <summary>Markdown snapshot of the app state. Safe from any thread (panel state is read on the UI thread).</summary>
    /// <param name="showListing">Which panel locations may show their contents (cursor, marks, listing sample); the
    /// paths themselves are always shown. <c>null</c> = all of them (the in-app chat). MCP passes its allowed folders.</param>
    public string Describe(Func<string, bool>? showListing = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.Invoke(() => Describe(showListing));
        }

        var (active, other, activeIsLeft) = panels();
        var text = new StringBuilder()
            .AppendLine($"# Commander4K state ({DateTimeOffset.Now:yyyy-MM-dd HH:mm})")
            .AppendLine($"- App: Commander4K {AppVersion.Display}, UI language {Core.Localization.Loc.Language}, skin {host.Settings.Skin}")
            .AppendLine($"- System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), user {Environment.UserName}")
            .AppendLine();
        Panel(text, "Active", activeIsLeft ? "left" : "right", active, isActive: true, showListing);
        Panel(text, "Other", activeIsLeft ? "right" : "left", other, isActive: false, showListing);

        text.AppendLine("## Plugins")
            .AppendLine($"- Plugin folders: {string.Join(", ", host.PluginRoots)}")
            .AppendLine("- Runtimes: " + string.Join(", ", host.Runtimes.Select(r => r.IsInstalled ? $"{r.Type} {r.Version}".TrimEnd() : $"{r.Type} (not installed)")));
        if (host.DiscoveredPlugins.Count == 0)
        {
            text.AppendLine("- External plugins: none installed");
        }

        foreach (var plugin in host.DiscoveredPlugins)
        {
            var state = host.PluginBlocker(plugin) is { } blocker ? "inactive: " + blocker : "running";
            text.AppendLine(plugin.Manifest is { } manifest
                ? $"- {manifest.Id} \"{manifest.Name}\" {manifest.Version} ({manifest.Runtime.Type}; permissions: {string.Join(", ", manifest.Permissions.DefaultIfEmpty("none"))}; {state}) in {plugin.Folder}"
                : $"- {plugin.Folder}: invalid ({string.Join(" ", plugin.Errors)})");
        }

        text.AppendLine("- Built-in panel modes: " + string.Join(", ", host.BuiltInPanels.Select(p => p.Title)));
        return text.ToString();
    }

    /// <summary>
    /// The plugin guide (docs/plugins.md: protocol, SDK reference, manifest). The JSON schema is left out — the guide
    /// describes every manifest field — which keeps the request short enough for web chat boxes.
    /// </summary>
    public string PluginGuide() => Resource("docs/plugins.md");

    /// <summary>The manifest JSON schema (docs/plugin.schema.json).</summary>
    public string PluginSchema() => Resource("docs/plugin.schema.json");

    /// <summary>
    /// The example for the preferred installed runtime (Python, then Node, then PowerShell) as File:/code-block pairs;
    /// with <paramref name="preview"/> the dedicated quick-view example (Python) when Python is installed.
    /// </summary>
    public string? Example(bool preview = false)
    {
        var runtime = new[] { "python", "node", "powershell" }
            .FirstOrDefault(type => host.Runtimes.Any(r => r.IsInstalled && string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))) ?? "powershell";
        var key = preview && runtime == "python" ? "preview" : runtime;
        return PluginManagerPanel.Templates.TryGetValue(key, out var template) ? ExampleText(template, runtime) : null;
    }

    /// <summary>A bundled example by folder name (see <see cref="ExampleNames"/>), or <c>null</c> when there is none.</summary>
    public static string? Example(string name)
    {
        var folder = ExampleNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            return null;
        }

        var runtime = PluginManagerPanel.Templates.FirstOrDefault(t => t.Key != "preview" && string.Equals(t.Value, folder, StringComparison.OrdinalIgnoreCase)).Key
            ?? (folder.EndsWith("-python", StringComparison.OrdinalIgnoreCase) ? "python" : "plugin");
        return ExampleText(folder, runtime);
    }

    /// <summary>Folder names of the example plugins embedded in the app.</summary>
    public static IReadOnlyList<string> ExampleNames() =>
    [
        .. typeof(AiAppContext).Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith("examples/", StringComparison.OrdinalIgnoreCase) && n.IndexOf('/', "examples/".Length) > 0)
            .Select(n => n["examples/".Length..n.IndexOf('/', "examples/".Length)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    private static string ExampleText(string template, string runtime)
    {
        var prefix = $"examples/{template}/";
        var assembly = typeof(AiAppContext).Assembly;
        var text = new StringBuilder($"Example plugin \"{template}\" ({runtime}) in the required output format:\n\n");
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order())
        {
            var relative = name.Replace('\\', '/')[prefix.Length..];
            if (PluginAuthoring.SdkClients.Values.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                // The SDK client is added by the installer; main.* shows how it is used. Keeps the prompt short enough for web chats.
                text.Append("File: ").Append(relative).AppendLine(" — the SDK client, added by the app; do not write it.").AppendLine();
                continue;
            }

            var language = Path.GetExtension(relative).TrimStart('.') switch { "py" => "python", "js" => "javascript", "ps1" => "powershell", var other => other };
            text.Append("File: ").AppendLine(relative).Append("```").AppendLine(language).AppendLine(Resource(name).TrimEnd()).AppendLine("```").AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The SDK client for a runtime (from the embedded examples, which a test keeps identical to plugins-sdk).</summary>
    public static string? SdkClient(string runtime) =>
        PluginManagerPanel.Templates.TryGetValue(runtime, out var template) && PluginAuthoring.SdkClients.TryGetValue(runtime, out var file)
            ? TryResource($"examples/{template}/{file}")
            : null;

    /// <summary>The whole request for writing a plugin: guide, current state, example and the user's wish.</summary>
    public string PluginPrompt(string wish, bool preview = false) => PluginAuthoring.BuildPrompt(PluginGuide(), Describe(), Example(preview), wish);

    /// <summary>
    /// What the assistant needs to know about an unknown file: name, size and the first bytes (hex and, when it looks
    /// like text, as text). Read through the router off the UI thread; the user asked for it by clicking.
    /// </summary>
    public static async Task<string> DescribeFileAsync(AppHost host, VfsEntry entry, CancellationToken cancellationToken = default)
    {
        var head = new byte[512];
        var read = 0;
        try
        {
            await using var stream = await host.Router.OpenReadAsync(entry.Path, cancellationToken);
            read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"File \"{entry.Name}\" ({entry.Size} bytes) — could not be read: {ex.Message}";
        }

        var bytes = head.AsSpan(0, read);
        var text = new StringBuilder($"File \"{entry.Name}\", extension \"{Path.GetExtension(entry.Name)}\", {entry.Size:N0} bytes.\n");
        text.AppendLine($"First {read} bytes (hex):");
        for (var i = 0; i < Math.Min(read, 256); i += 16)
        {
            text.AppendLine(Convert.ToHexString(bytes.Slice(i, Math.Min(16, read - i))).ToLowerInvariant());
        }

        var printable = bytes.ToArray().Count(b => b is 9 or 10 or 13 || b is >= 32 and < 127 || b >= 0xC0);
        if (read > 0 && printable > read * 0.85)
        {
            text.AppendLine("As text:").AppendLine("```").AppendLine(Encoding.UTF8.GetString(bytes).TrimEnd('\0')).AppendLine("```");
        }

        return text.ToString();
    }

    /// <summary>Validates an answer's files and returns the draft (folder = plugins/&lt;id&gt; in the data folder).</summary>
    /// <param name="limits">File limits; <c>null</c> = <see cref="PluginLimits.Answer"/> (a folder on disk uses <see cref="PluginLimits.Folder"/>).</param>
    public PluginDraft PrepareInstall(IReadOnlyList<GeneratedFile> files, PluginLimits? limits = null) =>
        PluginAuthoring.Prepare(files, Path.Combine(host.DataDirectory, "plugins"), SdkClient, limits);

    private static void Panel(StringBuilder text, string role, string side, FilePanelView panel, bool isActive, Func<string, bool>? showListing)
    {
        var tab = panel.ActiveTab;
        text.AppendLine($"## {role} panel ({side}{(isActive ? ", has focus" : string.Empty)})")
            .AppendLine($"- Path: {tab.Path}")
            .AppendLine($"- Mode: {(panel.Mode is { } mode ? mode.Title + " (shown over the file list)" : "file list")}");
        if (panel.Tabs.Count > 1)
        {
            text.AppendLine($"- Tabs: {string.Join(" | ", panel.Tabs.Select(t => t.Path))}");
        }

        if (showListing is not null && !showListing(tab.Path))
        {
            text.AppendLine("- Contents hidden: this folder is outside the folders allowed for MCP.").AppendLine();
            return;
        }

        if (tab.CurrentEntry is { } cursor)
        {
            text.AppendLine($"- Cursor on: {Describe(cursor)}");
        }

        var marked = tab.Entries.Where(e => tab.Marked.Contains(e.Path)).ToList();
        if (marked.Count > 0)
        {
            text.AppendLine($"- Marked ({marked.Count}): {string.Join("; ", marked.Take(MarkedSample).Select(Describe))}{(marked.Count > MarkedSample ? "; …" : string.Empty)}");
        }

        var entries = tab.Entries.Where(e => e.Kind != VfsEntryKind.ParentLink).ToList();
        var folders = entries.Count(e => e.IsContainer);
        text.AppendLine($"- Listing: {folders} folders, {entries.Count - folders} files{(tab.IsLoading ? " (still loading)" : string.Empty)}");
        if (entries.Count > 0)
        {
            text.AppendLine($"- Entries{(entries.Count > ListingSample ? $" (first {ListingSample})" : string.Empty)}: {string.Join(", ", entries.Take(ListingSample).Select(e => e.IsContainer ? e.Name + "/" : e.Name))}");
        }

        text.AppendLine();
    }

    private static string Describe(VfsEntry entry) =>
        entry.IsContainer
            ? $"{entry.Name}/ ({entry.Kind})"
            : $"{entry.Name} ({(entry.Size >= 0 ? entry.Size.ToString("N0", CultureInfo.InvariantCulture) + " B" : "size unknown")}, modified {entry.LastWriteTime.LocalDateTime:yyyy-MM-dd HH:mm})";

    private static string Resource(string name) => TryResource(name) ?? throw new InvalidOperationException($"Missing embedded resource {name}.");

    private static string? TryResource(string name)
    {
        var assembly = typeof(AiAppContext).Assembly;
        // Resources from subfolders keep the OS separator in their names (examples/x\y.py); compare both sides with '/'.
        var wanted = name.Replace('\\', '/');
        var actual = assembly.GetManifestResourceNames().FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
        if (actual is null)
        {
            return null;
        }

        using var reader = new StreamReader(assembly.GetManifestResourceStream(actual)!);
        return reader.ReadToEnd();
    }
}
