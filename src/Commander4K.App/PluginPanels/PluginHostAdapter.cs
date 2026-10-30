using Avalonia.Threading;
using Commander4K.App.Panels;
using Commander4K.PluginSdk;

namespace Commander4K.App.PluginPanels;

/// <summary>
/// The window as seen by plugins. Calls may come from any thread and are marshalled to the UI thread. When the active
/// panel shows a plugin, "the active file panel" is the other one, which is the file list the user is looking at.
/// </summary>
public sealed class PluginHostAdapter(
    Func<(FilePanelView Active, FilePanelView Other)> panels,
    Action<string, bool> notify,
    Func<string, string, Task> showText,
    Func<string, string, Task<bool>> confirm,
    Func<string, string, string, Task<string?>> prompt,
    Func<string, string?, Task> openPanel,
    Func<string, Task> editFile) : IPluginHost
{
    public string? ActivePath => OnUi(() => FilePanel().ActiveTab.Path);

    public string? OtherPath => OnUi(() =>
    {
        var (active, other) = panels();
        return (active.Plugin is null ? other : active).ActiveTab.Path;
    });

    public IReadOnlyList<VfsEntry> SelectedEntries => OnUi(() => FilePanel().ActiveTab.MarkedOrCurrent());

    public Task NavigateAsync(string path, string? focusName = null) =>
        Dispatcher.UIThread.InvokeAsync(() => FilePanel().NavigateAsync(path, focusName));

    public void Notify(string message, bool isError = false) => Dispatcher.UIThread.Post(() => notify(message, isError));

    public Task ShowTextAsync(string title, string text) => Dispatcher.UIThread.InvokeAsync(() => showText(title, text));

    public Task<bool> ConfirmAsync(string title, string message) => Dispatcher.UIThread.InvokeAsync(() => confirm(title, message));

    public Task<string?> PromptAsync(string title, string message, string initial = "") =>
        Dispatcher.UIThread.InvokeAsync(() => prompt(title, message, initial));

    public Task EditFileAsync(string path) => Dispatcher.UIThread.InvokeAsync(() => editFile(path));

    public Task OpenPanelAsync(string panelPluginId, string? argument = null) =>
        Dispatcher.UIThread.InvokeAsync(() => openPanel(panelPluginId, argument));

    private FilePanelView FilePanel()
    {
        var (active, other) = panels();
        return active.Plugin is null ? active : other;
    }

    private static T OnUi<T>(Func<T> read) => Dispatcher.UIThread.CheckAccess() ? read() : Dispatcher.UIThread.Invoke(read);
}
