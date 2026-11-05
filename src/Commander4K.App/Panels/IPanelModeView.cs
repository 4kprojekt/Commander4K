using Avalonia.Controls;
using Avalonia.Input;
using Commander4K.App.Theming;
using Commander4K.Core.Plugins;

namespace Commander4K.App.Panels;

/// <summary>
/// What a panel shows when it is not the file list: a plugin panel, the quick preview or a folder's own view, the
/// browser, an app page. The file list is the default content; a mode is either bound to the panel's folder (its
/// <c>c4k.dir</c>, the Pliki · Auto · Widok switch toggles between the two) or opened by itself (F2, a command).
/// The window routes keys and typing to the active panel's mode before the global keymap.
/// </summary>
/// <remarks>
/// Each mode states what it is to the other panel (<see cref="Kind"/>, <see cref="CanProvide"/>,
/// <see cref="CanReceive"/>); F5/F6 are routed from these by <see cref="PanelTransferRouting.Decide"/>, never from the
/// mode's type.
/// </remarks>
public interface IPanelModeView
{
    /// <summary>Shown in the panel's path bar and on its tab.</summary>
    string Title { get; }

    /// <summary>
    /// <see cref="PanelModeKind.Preview"/>: the panel's folder is still its folder (files copied here land in it);
    /// <see cref="PanelModeKind.Plugin"/>: a plugin panel, which may provide or receive files; anything else takes no files.
    /// </summary>
    PanelModeKind Kind => PanelModeKind.Other;

    /// <summary>F5 in this panel hands its selected rows to the other panel as files (<c>panel.provide</c>).</summary>
    bool CanProvide => false;

    /// <summary>F5 from the other panel sends its selection here (<c>panel.receive</c>).</summary>
    bool CanReceive => false;

    Control View { get; }

    /// <summary>The icon of the mode's tab and path bar; <c>null</c> keeps the default.</summary>
    string? TabIcon => null;

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
