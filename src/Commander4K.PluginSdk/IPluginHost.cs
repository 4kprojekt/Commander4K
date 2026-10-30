namespace Commander4K.PluginSdk;

/// <summary>
/// What a plugin may ask of the application. Implemented by the host; every member is safe to call from any thread
/// (the host marshals to the UI thread itself).
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// Location of the active file panel (any VFS path). When the active panel shows a plugin, this and
    /// <see cref="SelectedEntries"/> refer to the other panel — the file list the user is looking at.
    /// </summary>
    string? ActivePath { get; }

    /// <summary>Marked entries of the active file panel, or the entry under the cursor when nothing is marked.</summary>
    IReadOnlyList<VfsEntry> SelectedEntries { get; }

    /// <summary>Location of the other (inactive) file panel.</summary>
    string? OtherPath { get; }

    /// <summary>Opens <paramref name="path"/> in the active file panel, placing the cursor on <paramref name="focusName"/>.</summary>
    Task NavigateAsync(string path, string? focusName = null);

    /// <summary>Short message in the status area.</summary>
    void Notify(string message, bool isError = false);

    /// <summary>Shows text in a read-only viewer window (reports, command output).</summary>
    Task ShowTextAsync(string title, string text);

    /// <summary>Asks the user to confirm; returns <c>false</c> when cancelled.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Asks for a line of text; <c>null</c> when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string initial = "");

    /// <summary>Opens a file in the user's configured editor (tools.json).</summary>
    Task EditFileAsync(string path);

    /// <summary>Opens one of the host's panel plugins by <see cref="IPlugin.Id"/> in the active panel.</summary>
    Task OpenPanelAsync(string panelPluginId, string? argument = null);
}
