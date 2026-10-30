using Avalonia.Controls;
using Avalonia.Input;
using Commander4K.App.Theming;

namespace Commander4K.App.Panels;

/// <summary>
/// A non-file mode a <see cref="FilePanelView"/> can show over its file list: a plugin panel, the quick preview, and
/// (later) the browser. The window routes keys and typing to the active panel's mode before the global keymap.
/// </summary>
public interface IPanelModeView
{
    /// <summary>Shown in the panel's path bar.</summary>
    string Title { get; }

    Control View { get; }

    bool IsActivePanel { set; }

    void ApplySkin(SkinBrushes brushes);

    /// <summary>Returns <c>false</c> for keys left to the global keymap.</summary>
    bool HandleKey(KeyEventArgs e);

    /// <summary>Text typed while the panel is active (filters, search boxes).</summary>
    void AppendText(string text);

    ValueTask DisposeModeAsync();

    event Action? CloseRequested;

    event Action? Activated;
}

/// <summary>A mode whose <see cref="IPanelModeView.Title"/> changes while it is shown (the browser's page title).</summary>
public interface ITitledModeView
{
    event Action? TitleChanged;
}
