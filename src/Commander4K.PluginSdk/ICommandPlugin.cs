namespace Commander4K.PluginSdk;

/// <param name="Id">Unique within the plugin; the host exposes it as <c>plugin:&lt;pluginId&gt;:&lt;id&gt;</c> in the keymap.</param>
/// <param name="Category">Menu the command appears under, e.g. "Narzędzia" or "PowerShell/Sieć" (slash = submenu).</param>
/// <param name="DefaultShortcut">Suggested binding such as <c>Ctrl+Alt+I</c>; the user's keymap.json wins.</param>
public sealed record PluginCommand(string Id, string Title, string Category, string? Description = null, string? DefaultShortcut = null);

/// <summary>
/// Operations that plug into the menu bar, the command palette and the keymap (e.g. "compute checksums of the selected
/// files"). The list may change at runtime (user catalogues); the host re-reads it when <see cref="CommandsChanged"/> fires.
/// </summary>
public interface ICommandPlugin : IPlugin
{
    IReadOnlyList<PluginCommand> Commands { get; }

    event Action? CommandsChanged;

    bool CanExecute(string commandId, IPluginHost host) => true;

    Task ExecuteAsync(string commandId, IPluginHost host, CancellationToken cancellationToken);
}
