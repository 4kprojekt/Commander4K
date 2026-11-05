using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Browser;
using Commander4K.App.Dialogs;
using Commander4K.App.PluginPanels;
using Commander4K.Core.Input;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.App.Mcp;

/// <summary>
/// MCP tools bound to the window: app context, panels, commands and plugins. Handlers run off the UI thread; every
/// touch of window state goes through <see cref="Dispatcher.UIThread"/>. Plugin code runs through its own
/// <see cref="ScopedPluginHost"/> (<see cref="AppHost.HostFor"/>), like the UI does. Paths a handler takes from the
/// window itself (panel location, marked entries) are checked against the allowed folders here, because the dispatcher
/// only sees declared path arguments. Everything returned is English — it is read by the model.
/// </summary>
public static class McpAppTools
{
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(60);
    public const int MaxPanelRows = 500;
    public const int MaxTiles = 200;

    private const string PanelDescription = "Which panel: left, right, active (has focus) or other.";
    private const string GuideNote =
        "Write the plugin files into a folder inside the allowed folders, run check_plugin, then install_plugin and enable_plugin.";

    public static IReadOnlyList<McpTool> Create(
        MainWindow window,
        AppHost host,
        McpAppContext context,
        Func<IReadOnlyList<string>> allowedRoots,
        Func<IReadOnlyList<string>> protectedRoots)
    {
        McpAccess Access() => new(allowedRoots(), protectedRoots());
        return Tools(window, host, context, Access);
    }

    private static IReadOnlyList<McpTool> Tools(MainWindow window, AppHost host, McpAppContext context, Func<McpAccess> access) =>
    [
        new McpTool(
            new McpToolDefinition(
                "get_context",
                "Describes Commander4K right now: both panels (path, content — file list or the plugin shown, bound to its folder or not, what it sends to and receives from the other panel —, cursor, marked entries, a sample of the listing — contents only for folders inside the allowed folders), plugin runtimes and installed plugins. The panel model is in get_plugin_guide, section \"Panels: everything is a folder\".",
                McpSchema.Object(),
                McpToolMode.Allow),
            async (_, _) =>
            {
                var current = access();
                return new McpToolResult(await Dispatcher.UIThread.InvokeAsync(() => context.Describe(path => current.Refusal(path) is null)));
            }),
        new McpTool(
            new McpToolDefinition(
                "list_commands",
                "Lists the commands run_command accepts (built-in cm_* ids and panel:<id>) and plugin commands (plugin:<id>:<command>, run them with run_plugin_command).",
                McpSchema.Object(("filter", "string", "Only commands whose id or description contains this text (case-insensitive).", false)),
                McpToolMode.Allow),
            (call, _) => Task.FromResult(ListCommands(host, call.Optional("filter")))),
        new McpTool(
            new McpToolDefinition(
                "get_plugin_guide",
                "Returns the plugin author's guide (protocol, SDK reference, manifest), the plugin.json JSON schema and an example plugin.",
                McpSchema.Object(
                    ("runtime", "string", "Example for this runtime: python, node, powershell or preview (a quick-view plugin). Default: the preferred installed runtime.", false),
                    ("example", "string", "A bundled example by folder name (overrides runtime).", false)),
                McpToolMode.Allow),
            (call, _) => Task.FromResult(Guide(context, call.Optional("runtime"), call.Optional("example")))),
        new McpTool(
            new McpToolDefinition(
                "list_plugins",
                "Lists installed external plugins (id, version, folder, approval, why inactive, permissions, contributions) and the built-in panels and previews.",
                McpSchema.Object(),
                McpToolMode.Allow),
            (_, _) => Task.FromResult(ListPlugins(host))),
        new McpTool(
            new McpToolDefinition(
                "navigate",
                "Opens a folder in a panel (as plain navigation: no c4k.dir folder action runs) and waits for the listing.",
                McpSchema.Object(
                    ("panel", "string", PanelDescription, true),
                    ("path", "string", "Folder to open (disk path, \\\\server\\share, sftp://..., zip://...).", true)),
                McpToolMode.Allow,
                PathArguments: ["path"]),
            (call, ct) => NavigateAsync(window, call, ct),
            args => $"navigate {args["panel"]} {args["path"]}"),
        new McpTool(
            new McpToolDefinition(
                "select",
                "Marks entries in a panel by name (replacing the current marks; an empty list clears them) and optionally moves the cursor.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["panel"] = new JsonObject { ["type"] = "string", ["description"] = PanelDescription },
                        ["names"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Entry names in the panel's current folder.",
                        },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["description"] = "Name of the entry to put the cursor on." },
                    },
                    ["required"] = new JsonArray("panel", "names"),
                },
                McpToolMode.Allow),
            (call, _) => SelectAsync(window, call, access),
            args => $"select {args["panel"]} {args["names"]?.ToJsonString()}"),
        new McpTool(
            new McpToolDefinition(
                "set_panel_mode",
                "Switches what a panel shows: \"files\" (the file list), \"preview\" (quick view of the entry under the other panel's cursor) or \"panel:<id>\" (a plugin panel, ids from list_plugins).",
                McpSchema.Object(
                    ("panel", "string", PanelDescription, true),
                    ("mode", "string", "files, preview or panel:<id>.", true)),
                McpToolMode.Allow),
            (call, _) => SetModeAsync(window, call),
            args => $"set_panel_mode {args["panel"]} {args["mode"]}"),
        new McpTool(
            new McpToolDefinition(
                "run_command",
                "Runs a Commander4K command as if its key were pressed (ids from list_commands: cm_* or panel:<id>). It acts on the active panel and may open dialogs for the user.",
                McpSchema.Object(("id", "string", "Command id, e.g. cm_CalculateDirSizes.", true)),
                McpToolMode.Ask),
            (call, ct) => RunCommandAsync(window, host, call.Required("id"), access, ct),
            args => "run_command " + args["id"]),
        new McpTool(
            new McpToolDefinition(
                "run_plugin_command",
                "Runs a plugin command (from list_commands: plugin:<plugin>:<command>) on the visible file panel's folder and marked entries, which must be inside the allowed folders. Its output is shown to the user in Commander4K.",
                McpSchema.Object(
                    ("plugin", "string", "Plugin id.", true),
                    ("command", "string", "Command id within the plugin.", true)),
                McpToolMode.Ask),
            (call, ct) => RunPluginCommandAsync(window, call, access, ct),
            args => $"run_plugin_command {args["plugin"]}:{args["command"]}"),
        new McpTool(
            new McpToolDefinition(
                "load_plugin_panel",
                "Loads a panel plugin's data (task manager, system info, a plugin's own panel…) without showing it: the columns and up to 500 rows as tab-separated text.",
                McpSchema.Object(
                    ("plugin", "string", "Panel id (from list_plugins; external panels are <plugin id>/<panel id>).", true),
                    ("argument", "string", "What the panel is opened for, e.g. a log file path for panels that need a file (must be inside the allowed folders).", false),
                    ("filter", "string", "Filter text, as typed into the panel's filter box.", false)),
                McpToolMode.Ask),
            (call, ct) => LoadPanelAsync(window, host, call, access, ct),
            args => $"load_plugin_panel {args["plugin"]} {args["argument"]}".TrimEnd()),
        new McpTool(
            new McpToolDefinition(
                "render_preview",
                "Renders a file or folder with Commander4K's quick view (the highest-priority preview plugin that accepts it, or the given one): Markdown/text as text, tiles as JSON, images and binary files as a one-line description.",
                McpSchema.Object(
                    ("path", "string", "File or folder to preview.", true),
                    ("plugin", "string", "Preview plugin id (from list_plugins) instead of the automatic choice.", false)),
                McpToolMode.Ask,
                PathArguments: ["path"],
                // A folder preview reads its children (Markdown excerpts, notes overview), so an ancestor of a
                // protected folder is refused, as for the other recursive tools.
                ReadsTree: true),
            (call, ct) => RenderPreviewAsync(host, call, ct),
            args => "render_preview " + args["path"]),
        new McpTool(
            new McpToolDefinition(
                "install_plugin",
                "Installs a plugin after the user confirms its permissions in Commander4K. Give exactly one of: files (array of {path, content}) or folder (local plugin folder on disk). "
                + "Code that bypasses the SDK is not installed (the answer says what to fix). The plugin is installed disabled: call enable_plugin next.",
                McpPluginTools.FilesOrFolderSchema(),
                McpToolMode.Ask,
                AlwaysAsk: true,
                PathArguments: ["folder"],
                SelfConfirming: true,
                ReadsTree: true),
            (call, ct) => InstallAsync(window, context, call, ct),
            args => "install_plugin " + (args["folder"]?.ToString() ?? "(files)")),
        new McpTool(
            new McpToolDefinition(
                "enable_plugin",
                "Asks the user to enable an installed plugin, showing its permissions; on yes the plugin starts.",
                McpSchema.Object(("id", "string", "Plugin id.", true)),
                McpToolMode.Ask,
                AlwaysAsk: true,
                SelfConfirming: true),
            (call, ct) => EnableAsync(window, host, call, ct),
            args => "enable_plugin " + args["id"]),
        new McpTool(
            new McpToolDefinition(
                "reload_plugin",
                "Restarts an installed plugin's process: after install_plugin replaced its code, or when it hangs (Commander4K restarts all external plugins together). Permissions are checked again: grown permissions need enable_plugin.",
                McpSchema.Object(("id", "string", "Plugin id.", true)),
                McpToolMode.Ask),
            (call, _) => ReloadAsync(host, call.Required("id")),
            args => "reload_plugin " + args["id"]),
    ];

    private static McpToolResult ListCommands(AppHost host, string? filter)
    {
        var text = new StringBuilder();
        var count = 0;
        foreach (var command in host.ListCommands().OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (filter is { Length: > 0 }
                && !command.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !command.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            text.Append(command.Id).Append(" — ").AppendLine(command.Description);
            count++;
        }

        return new McpToolResult(count == 0 ? $"No commands match '{filter}'." : text.ToString());
    }

    private static McpToolResult Guide(McpAppContext context, string? runtime, string? example)
    {
        var names = McpAppContext.ExampleNames();
        string? exampleText;
        if (example is { Length: > 0 })
        {
            exampleText = McpAppContext.Example(example);
            if (exampleText is null)
            {
                return McpToolResult.Error($"Unknown example '{example}'. Available examples: {string.Join(", ", names)}.");
            }
        }
        else if (runtime is { Length: > 0 })
        {
            if (!PluginManagerPanel.Templates.TryGetValue(runtime.Trim(), out var template))
            {
                return McpToolResult.Error($"Unknown runtime '{runtime}'. Use one of: {string.Join(", ", PluginManagerPanel.Templates.Keys)}.");
            }

            exampleText = McpAppContext.Example(template);
        }
        else
        {
            exampleText = context.Example();
        }

        var text = new StringBuilder(context.PluginGuide().TrimEnd())
            .AppendLine().AppendLine()
            .AppendLine("# Manifest schema (plugin.json)").AppendLine()
            .AppendLine("```json").AppendLine(context.PluginSchema().TrimEnd()).AppendLine("```").AppendLine();
        if (exampleText is not null)
        {
            text.AppendLine(exampleText.TrimEnd()).AppendLine();
        }

        text.AppendLine($"Other bundled examples (get_plugin_guide example=<name>): {string.Join(", ", names)}.").AppendLine()
            .AppendLine(GuideNote);
        return new McpToolResult(text.ToString());
    }

    private static McpToolResult ListPlugins(AppHost host)
    {
        var text = new StringBuilder();
        if (host.DiscoveredPlugins.Count == 0)
        {
            text.AppendLine("No external plugins installed.");
        }

        foreach (var plugin in host.DiscoveredPlugins)
        {
            if (plugin.Manifest is not { } m)
            {
                text.AppendLine($"## (invalid) {plugin.Folder}").AppendLine("- errors: " + string.Join(" ", plugin.Errors)).AppendLine();
                continue;
            }

            var blocker = host.PluginBlocker(plugin);
            var running = host.ExternalPlugins.FirstOrDefault(e => string.Equals(e.Manifest.Id, m.Id, StringComparison.OrdinalIgnoreCase));
            var contributes = m.Contributes;
            text.AppendLine($"## {m.Id}")
                .AppendLine($"- name: {m.Name}, version {m.Version}, author {m.Author}")
                .AppendLine($"- folder: {plugin.Folder}")
                .AppendLine($"- runtime: {m.Runtime.Type}{(m.Runtime.Version is { } version ? " " + version : string.Empty)}")
                .AppendLine($"- approved: {(host.Approvals.IsApproved(m) ? "yes" : "no")}")
                .AppendLine($"- state: {(blocker is null ? running?.IsRunning == true ? "enabled, process running" : "enabled (starts on first use)" : "inactive: " + blocker)}")
                .AppendLine($"- permissions: {string.Join(", ", m.Permissions.DefaultIfEmpty("none"))}");
            if (contributes?.Commands is { Count: > 0 } commands)
            {
                text.AppendLine("- commands: " + string.Join(", ", commands.Select(c => $"{c.Id} ({c.Title})")));
            }

            if (contributes?.Panels is { Count: > 0 } panels)
            {
                text.AppendLine("- panels: " + string.Join(", ", panels.Select(p => $"{m.Id}/{p.Id} ({p.Title}{(p.RequiresArgument ? ", needs a file" : string.Empty)})")));
            }

            if (contributes?.Previews is { Count: > 0 } previews)
            {
                text.AppendLine("- previews: " + string.Join(", ", previews.Select(p => $"{m.Id}/{p.Id} ({string.Join(" ", p.Extensions)}, priority {p.Priority})")));
            }

            text.AppendLine();
        }

        text.AppendLine("## Built-in")
            .AppendLine("- panels: " + string.Join(", ", host.BuiltInPanels.Select(p => $"{p.Id} ({p.DisplayName}{(p.RequiresArgument ? ", needs a file" : string.Empty)})")))
            .AppendLine("- previews: " + string.Join(", ", host.BuiltInPreviews.Select(p => $"{p.Id} (priority {p.Priority})")))
            .AppendLine("- command plugins: " + string.Join(", ", host.CommandPlugins.Where(p => p.Manifest.IsBuiltIn).Select(p => p.Id)));
        return new McpToolResult(text.ToString());
    }

    private static string? PanelName(McpCall call, out McpToolResult? error)
    {
        var which = call.Required("panel").Trim().ToLowerInvariant();
        error = which is "left" or "right" or "active" or "other"
            ? null
            : McpToolResult.Error($"Unknown panel '{which}': use left, right, active or other.");
        return error is null ? which : null;
    }

    private static async Task<McpToolResult> NavigateAsync(MainWindow window, McpCall call, CancellationToken ct)
    {
        if (PanelName(call, out var error) is not { } which)
        {
            return error!;
        }

        var path = call.Required("path");
        var navigation = await Dispatcher.UIThread.InvokeAsync<Task>(() => window.McpNavigateAsync(window.McpPanel(which), path));
        try
        {
            // PanelTab.LoadAsync turns listing failures into the tab's Error; it never throws.
            await navigation.WaitAsync(LoadTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new McpToolResult($"The {which} panel is at {path}; the listing is still loading after {LoadTimeout.TotalSeconds:0} s.");
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var tab = window.McpPanel(which).ActiveTab;
            if (tab.Error is { } failure)
            {
                return McpToolResult.Error($"Could not open {tab.Path} in the {which} panel: {failure}");
            }

            var entries = tab.Entries.Count(e => e.Kind != VfsEntryKind.ParentLink);
            return new McpToolResult($"The {which} panel shows {tab.Path} ({entries} entries{(tab.IsLoading ? ", still loading" : string.Empty)}).");
        });
    }

    private static async Task<McpToolResult> SelectAsync(MainWindow window, McpCall call, Func<McpAccess> access)
    {
        if (PanelName(call, out var error) is not { } which)
        {
            return error!;
        }

        if (call.Arguments["names"] is not JsonArray array)
        {
            return McpToolResult.Error("'names' must be a list of entry names.");
        }

        var names = new List<string>();
        foreach (var node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var name))
            {
                return McpToolResult.Error("'names' must be a list of entry names.");
            }

            names.Add(name);
        }

        var cursor = call.Optional("cursor");
        var current = access();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var panel = window.McpPanel(which);
            var path = panel.ActiveTab.Path;
            if (current.Refusal(path) is { } refusal)
            {
                // The panel's folder comes from the window, not from an argument the dispatcher checked.
                return McpToolResult.Error(refusal);
            }

            var (marked, missing, cursorFound) = window.McpSelect(panel, names, cursor);
            var requested = names.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var text = new StringBuilder($"Marked {marked} of {requested} entries in {path}.");
            if (missing.Count > 0)
            {
                text.Append(" Not found: ").Append(string.Join(", ", missing)).Append('.');
            }

            if (!cursorFound)
            {
                text.Append($" Cursor entry not found: {cursor}.");
            }

            return new McpToolResult(text.ToString());
        });
    }

    private static async Task<McpToolResult> SetModeAsync(MainWindow window, McpCall call)
    {
        if (PanelName(call, out var error) is not { } which)
        {
            return error!;
        }

        var mode = call.Required("mode");
        var done = await Dispatcher.UIThread.InvokeAsync(() => window.McpSetModeAsync(window.McpPanel(which), mode));
        return done
            ? new McpToolResult($"The {which} panel now shows {mode}.")
            : McpToolResult.Error($"Could not switch the {which} panel to '{mode}'. Use files, preview or panel:<id> with an id from list_plugins (panels that need a file use the other panel's cursor).");
    }

    private static async Task<McpToolResult> RunCommandAsync(MainWindow window, AppHost host, string id, Func<McpAccess> access, CancellationToken ct)
    {
        if (id.StartsWith(CommandIds.PluginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Plugin commands work on the selection, which run_plugin_command checks against the allowed folders.
            return McpToolResult.Error("Plugin commands go through run_plugin_command (plugin and command ids).");
        }

        var command = host.ListCommands().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return McpToolResult.Error($"Unknown command '{id}'. Call list_commands.");
        }

        var current = access();
        var (refusal, target, run) = await Dispatcher.UIThread.InvokeAsync<(string?, string?, Task?)>(() =>
        {
            // Checked and started in one UI-thread step. Built-in commands act on the active panel's folder and
            // selection and write into the other panel (copy/move), so all of them must be inside the allowed folders.
            var context = window.McpCommandContext();
            if (context.FirstRefusal(current) is { } contextRefusal)
            {
                return (contextRefusal, null, null);
            }

            // ExecuteCommandAsync reports failures in the status bar itself; it never throws.
            return (null, context.Describe(), window.ExecuteCommandAsync(command.Id));
        });
        if (refusal is not null)
        {
            return McpToolResult.Error(refusal);
        }

        try
        {
            await run!.WaitAsync(CommandWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new McpToolResult($"Started {command.Id} ({command.Description}) on {target}; it is still running (it may be waiting for the user).");
        }

        return new McpToolResult($"Ran {command.Id} ({command.Description}) on {target}. Errors, if any, are shown in Commander4K's status bar.");
    }

    private static async Task<McpToolResult> RunPluginCommandAsync(MainWindow window, McpCall call, Func<McpAccess> access, CancellationToken ct)
    {
        var plugin = call.Required("plugin");
        var command = call.Required("command");
        var current = access();
        var (refusal, run) = await Dispatcher.UIThread.InvokeAsync<(string?, Task<string?>?)>(() =>
        {
            // Checked and started in one UI-thread step, so the context cannot change in between.
            // The plugin is told the active path, the other path and the selection (ExternalPlugin.Context).
            if (window.McpPluginContext().FirstRefusal(current) is { } contextRefusal)
            {
                return (contextRefusal, null);
            }

            return (null, window.McpRunPluginCommandAsync(plugin, command, ct));
        });
        if (refusal is not null)
        {
            return McpToolResult.Error(refusal);
        }

        string? failure;
        try
        {
            failure = await run!.WaitAsync(CommandWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new McpToolResult($"Started {plugin}:{command}; it is still running after {CommandWait.TotalSeconds:0} s.");
        }

        return failure is null
            ? new McpToolResult($"Ran {plugin}:{command}. Its output, if any, is shown to the user in Commander4K; check get_plugin_log for details.")
            : McpToolResult.Error(failure);
    }

    private static async Task<McpToolResult> LoadPanelAsync(MainWindow window, AppHost host, McpCall call, Func<McpAccess> access, CancellationToken ct)
    {
        var id = call.Required("plugin");
        if (host.FindPanel(id) is not { } plugin)
        {
            return McpToolResult.Error($"No panel '{id}'. Panels: {string.Join(", ", host.PanelPlugins.Select(p => p.Id))}.");
        }

        // Panels are given the window's context (active path, other path, selection) and their rows come back to the
        // model, so that context must be inside the allowed folders too.
        var current = access();
        if (await Dispatcher.UIThread.InvokeAsync(() => window.McpPluginContext().FirstRefusal(current)) is { } contextRefusal)
        {
            return McpToolResult.Error(contextRefusal);
        }

        var argument = call.Optional("argument");
        if (plugin.RequiresArgument)
        {
            if (argument is null)
            {
                return McpToolResult.Error($"The panel {plugin.Id} needs a file: pass its path as 'argument'.");
            }

            // File-bound panels read the argument; it is not a declared path argument, so it is checked here.
            if (current.Refusal(argument) is { } argumentRefusal)
            {
                return McpToolResult.Error(argumentRefusal);
            }

            argument = McpPathPolicy.Normalize(argument);
        }
        else
        {
            // Panels that take no file never see an unchecked argument (a plugin could treat it as a path).
            argument = null;
        }

        var request = new PanelRequest(argument, call.Optional("filter"));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(LoadTimeout);
        PanelSnapshot snapshot;
        try
        {
            await using var session = plugin.CreateSession(request, host.HostFor(plugin.Manifest));
            snapshot = await session.LoadAsync(request, null, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return McpToolResult.Error($"The panel {plugin.Id} did not load within {LoadTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return McpToolResult.Error($"The panel {plugin.Id} failed: {ex.Message}");
        }

        return new McpToolResult(FormatPanel(plugin, snapshot));
    }

    private static string FormatPanel(IPanelPlugin plugin, PanelSnapshot snapshot)
    {
        var text = new StringBuilder($"# {plugin.Title} ({plugin.Id})\n");
        if (snapshot.Status is { Length: > 0 } status)
        {
            text.AppendLine(status);
        }

        text.AppendLine(string.Join('\t', snapshot.Columns.Select(c => Cell(c.Header))));
        var byId = new Dictionary<string, PanelRow>(StringComparer.Ordinal);
        foreach (var row in snapshot.Rows)
        {
            byId.TryAdd(row.Id, row);
        }

        foreach (var row in snapshot.Rows.Take(MaxPanelRows))
        {
            // Tree rows (child processes, groups) are indented by depth in the first column.
            var depth = 0;
            for (var parent = row.ParentId; parent is not null && depth < 32 && byId.TryGetValue(parent, out var up); parent = up.ParentId)
            {
                depth++;
            }

            var cells = row.Cells.Select(Cell).ToList();
            if (cells.Count > 0 && depth > 0)
            {
                cells[0] = new string(' ', depth * 2) + cells[0];
            }

            text.AppendLine(string.Join('\t', cells));
        }

        if (snapshot.Rows.Count > MaxPanelRows)
        {
            text.AppendLine($"… {snapshot.Rows.Count - MaxPanelRows} more rows (use 'filter').");
        }

        return text.ToString();

        static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static async Task<McpToolResult> RenderPreviewAsync(AppHost host, McpCall call, CancellationToken ct)
    {
        var path = call.Required("path");
        var wanted = call.Optional("plugin");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(LoadTimeout);
        try
        {
            if (await host.Router.GetEntryAsync(path, limit.Token).ConfigureAwait(false) is not { } entry)
            {
                return McpToolResult.Error($"Not found: {path}");
            }

            IPreviewPlugin? plugin;
            if (wanted is { Length: > 0 })
            {
                plugin = host.PreviewPlugins.FirstOrDefault(p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase));
                if (plugin is null)
                {
                    return McpToolResult.Error($"No preview plugin '{wanted}'. Previews: {string.Join(", ", host.PreviewPlugins.Select(p => p.Id))}.");
                }

                if (!plugin.CanPreview(entry))
                {
                    return McpToolResult.Error($"The preview {plugin.Id} does not accept {entry.Name}.");
                }
            }
            else
            {
                // The same choice as the quick view (PreviewPanelView): the highest priority that accepts the entry.
                plugin = PreviewPluginResolver.Resolve(host.PreviewPlugins, entry);
                if (plugin is null)
                {
                    return McpToolResult.Error($"No preview for {entry.Name}.");
                }
            }

            var content = await plugin.LoadAsync(entry, host.HostFor(plugin.Manifest), null, limit.Token).ConfigureAwait(false);
            return new McpToolResult($"Preview of {entry.Path} by {plugin.Id}:\n\n" + FormatPreview(content));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return McpToolResult.Error($"The preview of {path} did not finish within {LoadTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return McpToolResult.Error($"The preview of {path} failed: {ex.Message}");
        }
    }

    private static string FormatPreview(PreviewContent content) => content switch
    {
        MarkdownPreview markdown => Clip(markdown.Markdown),
        TextPreview text => Clip(text.Text) + (text.Truncated ? "\n(the preview is truncated)" : string.Empty),
        TilesPreview tiles => TilesJson(tiles),
        ImagePreview image => $"Image ({ImageFormat(image.EncodedImage.Span)}, {image.EncodedImage.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes encoded). Use read_file with encoding base64 for the data.",
        HexPreview hex => $"Binary file, {hex.TotalLength.ToString("N0", CultureInfo.InvariantCulture)} bytes; first bytes: {Convert.ToHexString(hex.Data.Span[..Math.Min(32, hex.Data.Length)]).ToLowerInvariant()}",
        FolderPreview folder => FolderText(folder),
        MessagePreview message => message.Message,
        WebPreview web => $"Web preview: page {web.Page} of plugin {web.PluginId} (shown in the app window; not available as text).",
        BrowserPagePreview browser => $"Web page: {browser.Url} (shown in the app browser; not available as text).",
        _ => $"({content.GetType().Name}: no text form)",
    };

    private static string Clip(string text) =>
        text.Length <= McpFileTools.MaxTextChars ? text : text[..McpFileTools.MaxTextChars] + $"\n… (cut at {McpFileTools.MaxTextChars} characters)";

    private static string TilesJson(TilesPreview tiles)
    {
        var items = new JsonArray();
        foreach (var tile in tiles.Tiles.Take(MaxTiles))
        {
            items.Add(new JsonObject
            {
                ["title"] = tile.Title,
                ["subtitle"] = tile.Subtitle,
                ["icon"] = tile.Icon,
                ["target"] = tile.Target,
                ["hasImage"] = tile.Image is not null,
            });
        }

        var json = new JsonObject
        {
            ["title"] = tiles.Title,
            ["description"] = tiles.Description,
            ["tileCount"] = tiles.Tiles.Count,
            ["tiles"] = items,
        };
        return json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string FolderText(FolderPreview folder)
    {
        var text = new StringBuilder(
            $"Folder {folder.Path}: {folder.Files} files, {folder.Folders} folders, {folder.TotalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes{(folder.Complete ? string.Empty : " (sizes still being computed)")}\n");
        foreach (var item in folder.Items.Take(MaxPanelRows))
        {
            text.Append(item.IsDirectory ? item.Name + "/" : item.Name).Append('\t')
                .AppendLine(item.Size is { } size ? size.ToString(CultureInfo.InvariantCulture) : "?");
        }

        return text.ToString();
    }

    private static string ImageFormat(ReadOnlySpan<byte> data) => data switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "PNG",
        [0xFF, 0xD8, ..] => "JPEG",
        [(byte)'G', (byte)'I', (byte)'F', ..] => "GIF",
        [(byte)'B', (byte)'M', ..] => "BMP",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "WebP",
        _ => "unknown format",
    };

    private static async Task<McpToolResult> InstallAsync(MainWindow window, McpAppContext context, McpCall call, CancellationToken ct)
    {
        var hasFiles = call.Arguments["files"] is JsonArray;
        var folder = call.Optional("folder");
        if (hasFiles == (folder is not null))
        {
            return McpToolResult.Error("Give exactly one of 'files' or 'folder'.");
        }

        IReadOnlyList<GeneratedFile> files;
        PluginLimits limits;
        if (hasFiles)
        {
            try
            {
                files = McpPluginTools.ReadFiles((JsonArray)call.Arguments["files"]!);
            }
            catch (ArgumentException ex)
            {
                return McpToolResult.Error(ex.Message);
            }

            limits = PluginLimits.Answer;
        }
        else
        {
            if (McpPluginTools.LocalFolderError(folder!) is { } error)
            {
                return McpToolResult.Error(error);
            }

            var (read, failure) = await Task.Run(() => ReadFolder(folder!, ct), CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (read is null)
            {
                return McpToolResult.Error(failure!);
            }

            files = read;
            limits = PluginLimits.Folder;
        }

        // The installer's own dialog (name, permissions, files, target folder) is the user's confirmation.
        var result = await Dispatcher.UIThread.InvokeAsync(() =>
            PluginInstaller.InstallAsync(window, window.McpBrushes, context, files, () => Task.CompletedTask, limits: limits, forMcp: true));
        if (result.Installed)
        {
            return new McpToolResult(result.Message);
        }

        return McpToolResult.Error(result.FixRequest is { } fix ? result.Message + "\n\n" + fix : result.Message);
    }

    /// <summary>Reads a plugin folder; expected failures (and cancellation) come back as text and never leave the Task.Run lambda.</summary>
    private static (IReadOnlyList<GeneratedFile>? Files, string? Error) ReadFolder(string folder, CancellationToken ct)
    {
        try
        {
            return (PluginAuthoring.FromFolder(folder, PluginLimits.Folder, ct), null);
        }
        catch (OperationCanceledException)
        {
            return (null, "The request was cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<McpToolResult> EnableAsync(MainWindow window, AppHost host, McpCall call, CancellationToken ct)
    {
        var id = call.Required("id");
        if (FindPlugin(host, id) is not { } plugin)
        {
            return McpToolResult.Error($"No installed plugin '{id}'. Installed: {string.Join(", ", host.DiscoveredPlugins.Select(p => p.Manifest?.Id).OfType<string>().DefaultIfEmpty("none"))}.");
        }

        if (plugin.Manifest is not { } manifest)
        {
            return McpToolResult.Error($"The plugin in {plugin.Folder} is invalid: {string.Join(" ", plugin.Errors)}");
        }

        if (RuntimeCatalog.CheckRequirement(manifest.Runtime, host.Runtimes) is { } missing)
        {
            return McpToolResult.Error($"{manifest.Id} cannot run: {missing}");
        }

        if (host.Approvals.IsApproved(manifest))
        {
            return new McpToolResult($"{manifest.Id} {manifest.Version} is already enabled.");
        }

        var details = await PluginManagerPanel.ApprovalMessageAsync(plugin, ct).ConfigureAwait(false);
        var message = Loc.T("Klient MCP „{0}” prosi o włączenie wtyczki {1}.", call.ClientName, manifest.Id) + "\n\n" + details;
        var confirmed = await Dispatcher.UIThread.InvokeAsync(() =>
            new ConfirmDialog(window.McpBrushes, Loc.T("Włącz wtyczkę"), message, Loc.T("Włącz"), cancelIsDefault: true).ShowAsync(window));
        if (!confirmed)
        {
            return McpToolResult.Error($"The user declined enabling {manifest.Id}.");
        }

        return await PluginManagerPanel.ApproveAsync(host, plugin).ConfigureAwait(false) is { } still
            ? McpToolResult.Error($"Approved {manifest.Id}, but it cannot start: {still}")
            : new McpToolResult($"Enabled {manifest.Id} {manifest.Version}.");
    }

    private static async Task<McpToolResult> ReloadAsync(AppHost host, string id)
    {
        if (FindPlugin(host, id) is not { Manifest: { } manifest })
        {
            return McpToolResult.Error($"No installed plugin '{id}'.");
        }

        await host.ReloadExternalPluginsAsync().ConfigureAwait(false);
        var state = FindPlugin(host, id) is { } after && host.PluginBlocker(after) is { } blocker
            ? "it is inactive: " + blocker
            : "it starts on its next use";
        return new McpToolResult($"Restarted all external plugins (Commander4K reloads them together); {manifest.Id}: {state}.");
    }

    private static DiscoveredPlugin? FindPlugin(AppHost host, string id) =>
        host.DiscoveredPlugins.FirstOrDefault(p => string.Equals(p.Manifest?.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or TimeoutException
            or JsonRpcException or System.Text.Json.JsonException or ArgumentException;
}

/// <summary>The locations a command or plugin works on, read from the window on the UI thread.</summary>
/// <param name="Selected">Marked entries, or the entry under the cursor.</param>
public sealed record McpPanelContext(string ActivePath, string OtherPath, IReadOnlyList<VfsEntry> Selected)
{
    private const int DescribedNames = 20;

    /// <summary>
    /// Why the context may not be used: the first of active path, other path and selected entries that is outside the
    /// allowed folders or inside a protected one; <c>null</c> when all are fine.
    /// </summary>
    public string? FirstRefusal(McpAccess access) =>
        new[] { ActivePath, OtherPath }.Concat(Selected.Select(e => e.Path)).Select(access.Refusal).FirstOrDefault(r => r is not null);

    /// <summary>"folder X, selection a, b (other panel Y)" for results and the audit.</summary>
    public string Describe()
    {
        var names = Selected.Count == 0
            ? "nothing selected"
            : "selection " + string.Join(", ", Selected.Take(DescribedNames).Select(e => e.Name)) + (Selected.Count > DescribedNames ? $" and {Selected.Count - DescribedNames} more" : string.Empty);
        return $"{ActivePath} ({names}; other panel {OtherPath})";
    }
}

/// <summary>The allowed and protected folders at the moment of a call (a snapshot of both lists).</summary>
public sealed record McpAccess(IReadOnlyList<string> Allowed, IReadOnlyList<string> Protected)
{
    /// <summary>Why <paramref name="path"/> may not be used, or <c>null</c>; see <see cref="McpToolDispatcher.Refusal"/>.</summary>
    public string? Refusal(string path) => McpToolDispatcher.Refusal(path, Allowed, Protected);
}
