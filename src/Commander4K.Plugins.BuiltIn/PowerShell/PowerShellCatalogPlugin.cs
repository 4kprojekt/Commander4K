using System.Text;
using System.Text.RegularExpressions;
using Commander4K.Core.Execution;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.BuiltIn.PowerShell;

/// <summary>
/// Catalogue of ready PowerShell commands (config/powershell.json) plus the user's own script files
/// (<c>*.ps1</c> in config/powershell, subfolders = submenus): every entry is a menu/palette command and a row of the
/// "PowerShell" panel. A script file gets the selected files as its arguments. Output of non-elevated commands is
/// captured and shown in a viewer.
/// </summary>
public sealed partial class PowerShellCatalogPlugin(string catalogPath) : ICommandPlugin, IPanelPlugin
{
    public const string PluginId = "builtin.powershell";

    /// <summary>Command ids of script files: <c>script:</c> + the path relative to <see cref="ScriptsFolder"/>.</summary>
    public const string ScriptPrefix = "script:";

    private const string OpenFolderId = "scripts-folder";
    private const string ReloadId = "scripts-reload";

    private IReadOnlyList<PowerShellCommand> _commands = PowerShellCatalog.Defaults;
    private IReadOnlyList<string> _scripts = [];

    public string Id => PluginId;

    public string DisplayName => "PowerShell";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.powershell", Loc.T("Polecenia PowerShell"), Loc.T("Katalog poleceń PowerShell uruchamianych na folderach i plikach."), PluginPermissions.ShellRun, PluginPermissions.FileRead);

    public string Title => Loc.T("Polecenia PowerShell");

    public string Description => Loc.T("Gotowe polecenia PowerShell (config/powershell.json) i własne skrypty .ps1 (folder config/powershell) do uruchamiania na bieżącym folderze i zaznaczonych plikach.");

    public string CatalogPath { get; } = catalogPath;

    /// <summary>The user's script files, next to the catalogue: <c>config/powershell</c>.</summary>
    public string ScriptsFolder { get; } = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!, "powershell");

    public IReadOnlyList<PluginCommand> Commands =>
    [
        .. _commands.Select(c => new PluginCommand(c.Id, c.Title, "PowerShell/" + c.Category, c.Description)),
        .. _scripts.Select(s => new PluginCommand(ScriptPrefix + s, Path.GetFileNameWithoutExtension(s), "PowerShell/" + ScriptCategory(s),
            Loc.T("Skrypt {0}; zaznaczone pliki są jego parametrami.", s))),
        new PluginCommand(OpenFolderId, Loc.T("Otwórz folder skryptów"), "PowerShell", Loc.T("Folder config/powershell: każdy plik .ps1 w nim jest poleceniem w tym menu.")),
        new PluginCommand(ReloadId, Loc.T("Przeładuj polecenia i skrypty"), "PowerShell", Loc.T("Czyta ponownie powershell.json i folder skryptów.")),
    ];

    /// <summary>Menu of a script: "Skrypty", or "Skrypty/<its subfolder>".</summary>
    private static string ScriptCategory(string relative) =>
        Path.GetDirectoryName(relative) is { Length: > 0 } folder ? Loc.T("Skrypty") + "/" + folder.Replace('\\', '/') : Loc.T("Skrypty");

    public event Action? CommandsChanged;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await CallerThread.LeaveUi(); // reads the catalogue and lists the scripts folder
        _commands = await PowerShellCatalog.LoadOrCreateAsync(CatalogPath, cancellationToken).ConfigureAwait(false);
        _scripts = ListScripts();
        CommandsChanged?.Invoke();
    }

    /// <summary>The <c>.ps1</c> files of <see cref="ScriptsFolder"/> (relative paths, sorted); the folder gets an example the first time.</summary>
    private IReadOnlyList<string> ListScripts()
    {
        try
        {
            if (!Directory.Exists(ScriptsFolder))
            {
                Directory.CreateDirectory(ScriptsFolder);
                File.WriteAllText(Path.Combine(ScriptsFolder, "Przykład - pokaż parametry.ps1"), ExampleScript);
            }

            return [.. Directory.EnumerateFiles(ScriptsFolder, "*.ps1", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Select(file => Path.GetRelativePath(ScriptsFolder, file))
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private const string ExampleScript = """
        # Każdy plik .ps1 w tym folderze jest poleceniem w menu PowerShell > Skrypty (podfolder = podmenu).
        # Zaznaczone pliki (albo plik pod kursorem) trafiają do skryptu jako parametry; folderem roboczym jest folder panelu.
        param([Parameter(ValueFromRemainingArguments)] [string[]] $Files)

        "Folder: $(Get-Location)"
        "Parametry: $($Files.Count)"
        $Files | ForEach-Object { Get-Item -LiteralPath $_ | Select-Object Name, Length, LastWriteTime }

        """;

    public async Task ExecuteAsync(string commandId, IPluginHost host, CancellationToken cancellationToken)
    {
        if (commandId == OpenFolderId)
        {
            await host.OpenFolderAsync(ScriptsFolder).ConfigureAwait(false);
        }
        else if (commandId == ReloadId)
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            host.Notify(Loc.T("Polecenia PowerShell: {0}, skrypty: {1}.", _commands.Count, _scripts.Count));
        }
        else if (commandId.StartsWith(ScriptPrefix, StringComparison.Ordinal) && _scripts.Contains(commandId[ScriptPrefix.Length..]))
        {
            await RunScriptFileAsync(commandId[ScriptPrefix.Length..], host, cancellationToken).ConfigureAwait(false);
        }
        else if (_commands.FirstOrDefault(c => c.Id == commandId) is { } command)
        {
            await RunAsync(command, host, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a script file with the selected entries as its arguments, one each.</summary>
    private Task RunScriptFileAsync(string relative, IPluginHost host, CancellationToken cancellationToken)
    {
        var selected = host.SelectedEntries;
        var files = selected.Select(e => LocalFileSystemPlugin.ToLocalPath(e.Path)).OfType<string>().ToList();
        if (files.Count != selected.Count)
        {
            // Dropping them silently would run the script on fewer files than the user marked.
            host.Notify(Loc.T("Skrypt dostaje ścieżki plików: zaznacz pliki na dysku albo w udziale sieciowym."), isError: true);
            return Task.CompletedTask;
        }

        return RunScriptAsync(Path.GetFileNameWithoutExtension(relative), ScriptCommandLine(Path.Combine(ScriptsFolder, relative), files), false, false, host, cancellationToken);
    }

    /// <summary><c>&amp; 'script.ps1' 'file 1' 'file 2'</c>: every path a quoted literal, so names cannot inject code.</summary>
    internal static string ScriptCommandLine(string script, IReadOnlyList<string> files) =>
        "& " + Quote(script) + string.Concat(files.Select(file => " " + Quote(file)));

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session(this, host);

    /// <summary>Expands placeholders into PowerShell literals; returns <c>null</c> when the user cancelled a prompt.</summary>
    internal static async Task<string?> ExpandAsync(string script, IPluginHost host)
    {
        var selected = host.SelectedEntries
            .Select(e => LocalFileSystemPlugin.ToLocalPath(e.Path))
            .OfType<string>()
            .ToList();
        var folder = host.ActivePath is { } active ? LocalFileSystemPlugin.ToLocalPath(active) : null;

        var result = new StringBuilder();
        var position = 0;
        foreach (Match match in PlaceholderRegex().Matches(script))
        {
            result.Append(script, position, match.Index - position);
            var name = match.Groups["name"].Value;
            string? value = name switch
            {
                "path" => Quote(folder ?? Environment.CurrentDirectory),
                "file" => Quote(selected.FirstOrDefault() ?? throw new InvalidOperationException(Loc.T("To polecenie wymaga pliku pod kursorem."))),
                "files" => selected.Count == 0
                    ? throw new InvalidOperationException(Loc.T("To polecenie wymaga zaznaczonych plików."))
                    : string.Join(',', selected.Select(Quote)),
                _ when name.StartsWith("input:", StringComparison.Ordinal) =>
                    await host.PromptAsync("PowerShell", name[6..]).ConfigureAwait(false) is { } answer ? Quote(answer) : null,
                _ => match.Value,
            };
            if (value is null)
            {
                return null;
            }

            result.Append(value);
            position = match.Index + match.Length;
        }

        result.Append(script, position, script.Length - position);
        return result.ToString();
    }

    /// <summary>PowerShell single-quoted literal: no variable expansion, quotes doubled.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private async Task RunAsync(PowerShellCommand command, IPluginHost host, CancellationToken cancellationToken)
    {
        string? script;
        try
        {
            script = await ExpandAsync(command.Script, host).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            host.Notify(ex.Message, isError: true);
            return;
        }

        if (script is null)
        {
            return;
        }

        await RunScriptAsync(command.Title, script, command.RequiresAdmin, command.Confirm, host, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunScriptAsync(string title, string script, bool requiresAdmin, bool confirm, IPluginHost host, CancellationToken cancellationToken)
    {
        if ((confirm || requiresAdmin)
            && !await host.ConfirmAsync(title, Loc.T("Uruchomić{0}?\n\n{1}", (requiresAdmin ? " jako administrator" : ""), script)).ConfigureAwait(false))
        {
            return;
        }

        var directory = host.ActivePath is { } active ? LocalFileSystemPlugin.ToLocalPath(active) : null;
        try
        {
            // Everything runs through the host, which checks the shell.run permission and audits the command line.
            var result = await host.RunProcessAsync(BuildRequest(FindShell(), script, directory, requiresAdmin), cancellationToken).ConfigureAwait(false);
            if (!requiresAdmin)
            {
                await host.ShowTextAsync($"PowerShell — {title}", $"PS> {script}\n\n{FormatResult(result)}").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            host.Notify(ex.Message, isError: true);
        }
    }

    /// <summary>Command line for a script: UTF-8 output, no profile, formatted to a wide text table.</summary>
    internal static ProcessRunRequest BuildRequest(string shell, string script, string? directory, bool elevated = false) =>
        elevated
            ? new ProcessRunRequest(shell, ["-NoProfile", "-NoExit", "-Command", script], directory, Elevated: true)
            : new ProcessRunRequest(shell,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                 "[Console]::OutputEncoding = [Text.Encoding]::UTF8; & { " + script + " } | Out-String -Width 220"],
                directory);

    internal static string FormatResult(ProcessRunResult result)
    {
        var text = result.Output.TrimEnd();
        return (string.IsNullOrWhiteSpace(result.Error) ? text : $"{text}\n\n[stderr]\n{result.Error.TrimEnd()}")
            + Loc.T("\n\n[kod wyjścia {0}]", result.ExitCode);
    }

    /// <summary>PowerShell 7 when installed, otherwise the Windows PowerShell 5.1 that ships with Windows.</summary>
    internal static string FindShell()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    }

    [GeneratedRegex(@"\{(?<name>path|file|files|input:[^}]+)\}")]
    private static partial Regex PlaceholderRegex();

    private sealed class Session(PowerShellCatalogPlugin plugin, IPluginHost host) : IPanelSession
    {
        private static readonly ColumnDefinition[] Columns =
        [
            new("title", Loc.T("Polecenie"), ColumnAlignment.Left, 320),
            new("description", Loc.T("Opis"), ColumnAlignment.Left, 480),
            new("flags", Loc.T("Uwagi"), ColumnAlignment.Left, 140),
        ];

        public TimeSpan? RefreshInterval => null;

        public IReadOnlyList<PanelAction> Actions { get; } =
        [
            new("run", Loc.T("Uruchom"), "Enter"),
            new("edit", Loc.T("Edytuj katalog (powershell.json)"), "F4", AppliesToRows: false),
            new("folder", Loc.T("Otwórz folder skryptów"), "F7", AppliesToRows: false),
            new("reload", Loc.T("Przeładuj katalog"), "Ctrl+R", AppliesToRows: false),
        ];

        public async Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            await plugin.ReloadAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<PanelRow>();
            foreach (var category in plugin._commands.GroupBy(c => c.Category))
            {
                rows.Add(new PanelRow("cat:" + category.Key, null, [category.Key, Loc.T("{0} poleceń", category.Count()), ""], RowSeverity.Highlight, ExpandedByDefault: true));
                foreach (var command in category)
                {
                    var flags = string.Join(", ", new[] { command.RequiresAdmin ? "administrator" : null, command.Confirm ? "potwierdzenie" : null }.OfType<string>());
                    rows.Add(new PanelRow(command.Id, "cat:" + category.Key, [command.Title, command.Description, flags],
                        command.RequiresAdmin ? RowSeverity.Warning : RowSeverity.Normal));
                }
            }

            const string scripts = "cat:" + ScriptPrefix;
            rows.Add(new PanelRow(scripts, null, [Loc.T("Skrypty"), plugin.ScriptsFolder, ""], RowSeverity.Highlight, ExpandedByDefault: true));
            foreach (var script in plugin._scripts)
            {
                rows.Add(new PanelRow(ScriptPrefix + script, scripts, [script, Loc.T("Zaznaczone pliki są parametrami skryptu."), ""]));
            }

            return new PanelSnapshot(Columns, rows, Loc.T("{0} poleceń · {1} · {2} skryptów · {3} · powłoka: {4}",
                plugin._commands.Count, plugin.CatalogPath, plugin._scripts.Count, plugin.ScriptsFolder, Path.GetFileName(FindShell())));
        }

        public Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken) =>
            Task.FromResult(rowId.StartsWith(ScriptPrefix, StringComparison.Ordinal)
                ? new PanelDetails([new PanelDetailSection(rowId[ScriptPrefix.Length..],
                [
                    new(Loc.T("Plik"), Path.Combine(plugin.ScriptsFolder, rowId[ScriptPrefix.Length..])),
                    new(Loc.T("Parametry"), Loc.T("Zaznaczone pliki są parametrami skryptu.")),
                    new(Loc.T("Id (do keymap.json)"), $"plugin:{PluginId}:{rowId}"),
                ])])
                : plugin._commands.FirstOrDefault(c => c.Id == rowId) is { } command
                ? new PanelDetails([new PanelDetailSection(command.Title,
                [
                    new(Loc.T("Opis"), command.Description),
                    new(Loc.T("Skrypt"), command.Script),
                    new(Loc.T("Uprawnienia"), command.RequiresAdmin ? "administrator (osobne okno)" : Loc.T("zwykłe (wynik w podglądzie)")),
                    new(Loc.T("Id (do keymap.json)"), $"plugin:{PluginId}:{command.Id}"),
                ])])
                : (PanelDetails?)null);

        public async Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            switch (actionId)
            {
                case "run":
                    foreach (var id in rowIds.Where(id => !id.StartsWith("cat:", StringComparison.Ordinal)))
                    {
                        await plugin.ExecuteAsync(id, host, cancellationToken).ConfigureAwait(false);
                    }

                    return null;
                case "edit":
                    await host.EditFileAsync(plugin.CatalogPath).ConfigureAwait(false);
                    return null;
                case "folder":
                    await host.OpenFolderAsync(plugin.ScriptsFolder).ConfigureAwait(false);
                    return null;
                default:
                    return null;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
