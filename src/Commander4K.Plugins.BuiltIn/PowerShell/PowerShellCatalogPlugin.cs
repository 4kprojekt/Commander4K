using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.BuiltIn.PowerShell;

/// <summary>
/// Catalogue of ready PowerShell commands (config/powershell.json): every entry is a menu/palette command and a row
/// of the "PowerShell" panel. Output of non-elevated commands is captured and shown in a viewer.
/// </summary>
public sealed partial class PowerShellCatalogPlugin(string catalogPath) : ICommandPlugin, IPanelPlugin
{
    public const string PluginId = "builtin.powershell";

    private IReadOnlyList<PowerShellCommand> _commands = PowerShellCatalog.Defaults;

    public string Id => PluginId;

    public string DisplayName => "PowerShell";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.powershell", Loc.T("Polecenia PowerShell"), Loc.T("Katalog poleceń PowerShell uruchamianych na folderach i plikach."), PluginPermissions.ShellRun, PluginPermissions.FileRead);

    public string Title => Loc.T("Polecenia PowerShell");

    public string Description => Loc.T("Katalog gotowych poleceń PowerShell (config/powershell.json) do uruchamiania na bieżącym folderze i zaznaczonych plikach.");

    public string CatalogPath { get; } = catalogPath;

    public IReadOnlyList<PluginCommand> Commands =>
        [.. _commands.Select(c => new PluginCommand(c.Id, c.Title, "PowerShell/" + c.Category, c.Description))];

    public event Action? CommandsChanged;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _commands = await PowerShellCatalog.LoadOrCreateAsync(CatalogPath, cancellationToken).ConfigureAwait(false);
        CommandsChanged?.Invoke();
    }

    public Task ExecuteAsync(string commandId, IPluginHost host, CancellationToken cancellationToken) =>
        _commands.FirstOrDefault(c => c.Id == commandId) is { } command
            ? RunAsync(command, host, cancellationToken)
            : Task.CompletedTask;

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

        if ((command.Confirm || command.RequiresAdmin)
            && !await host.ConfirmAsync(command.Title, Loc.T("Uruchomić{0}?\n\n{1}", (command.RequiresAdmin ? " jako administrator" : ""), script)).ConfigureAwait(false))
        {
            return;
        }

        var directory = host.ActivePath is { } active ? LocalFileSystemPlugin.ToLocalPath(active) : null;
        try
        {
            // Everything runs through the host, which checks the shell.run permission and audits the command line.
            var result = await host.RunProcessAsync(BuildRequest(FindShell(), script, directory, command.RequiresAdmin), cancellationToken).ConfigureAwait(false);
            if (!command.RequiresAdmin)
            {
                await host.ShowTextAsync($"PowerShell — {command.Title}", $"PS> {script}\n\n{FormatResult(result)}").ConfigureAwait(false);
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

            return new PanelSnapshot(Columns, rows, Loc.T("{0} poleceń · {1} · powłoka: {2}", plugin._commands.Count, plugin.CatalogPath, Path.GetFileName(FindShell())));
        }

        public Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken) =>
            Task.FromResult(plugin._commands.FirstOrDefault(c => c.Id == rowId) is { } command
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
                default:
                    return null;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
