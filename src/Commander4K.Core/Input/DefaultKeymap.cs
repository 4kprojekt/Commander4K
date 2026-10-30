namespace Commander4K.Core.Input;

/// <summary>Total Commander-style defaults, written to <c>config/keymap.json</c> on first run.</summary>
public static class DefaultKeymap
{
    public static IReadOnlyDictionary<string, string> Bindings { get; } = new Dictionary<string, string>
    {
        ["F1"] = CommandIds.Help,
        ["Ctrl+Shift+P"] = CommandIds.CommandPalette,
        ["Ctrl+Alt+I"] = CommandIds.PanelPrefix + "builtin.sysinfo",
        ["Ctrl+Alt+T"] = CommandIds.PanelPrefix + "builtin.taskmanager",
        ["Ctrl+Alt+L"] = CommandIds.PanelPrefix + "builtin.logs",
        ["Ctrl+Alt+P"] = CommandIds.PanelPrefix + "builtin.powershell",
        ["Ctrl+,"] = CommandIds.Settings,
        ["F2"] = CommandIds.SelectSkin,
        ["F10"] = CommandIds.Exit,
        ["Ctrl+H"] = CommandIds.ToggleHidden,
        ["Tab"] = CommandIds.SwitchPanel,
        ["Enter"] = CommandIds.Open,
        ["Backspace"] = CommandIds.GoToParent,
        ["Ctrl+R"] = CommandIds.Refresh,
        ["Ctrl+T"] = CommandIds.NewTab,
        ["Ctrl+W"] = CommandIds.CloseTab,
        ["Alt+F1"] = CommandIds.LeftDriveMenu,
        ["Alt+F2"] = CommandIds.RightDriveMenu,
        ["Ctrl+Shift+N"] = CommandIds.OpenNetwork,
        ["Ctrl+Shift+R"] = CommandIds.OpenRegistry,
        ["Ctrl+G"] = CommandIds.GoToFrecent,
        ["Alt+F10"] = CommandIds.GoToFrecent,
        ["Insert"] = CommandIds.ToggleSelection,
        ["Space"] = CommandIds.CalculateDirSizes,
        ["F3"] = CommandIds.View,
        ["Ctrl+Q"] = CommandIds.QuickView,
        ["F4"] = CommandIds.Edit,
        ["F5"] = CommandIds.Copy,
        ["F6"] = CommandIds.Move,
        ["F7"] = CommandIds.MakeDirectory,
        ["F8"] = CommandIds.Delete,
        ["Delete"] = CommandIds.Delete,
        ["Alt+F7"] = CommandIds.SearchAsync,
        ["Ctrl+F12"] = CommandIds.Compare,
        ["Ctrl+O"] = CommandIds.OpenTerminal,
        ["F9"] = CommandIds.OpenTerminal,
        ["Escape"] = CommandIds.CancelCurrentOperation,
        ["Ctrl+Shift+K"] = CommandIds.SelectSkin,
        ["Ctrl+Alt+K"] = CommandIds.NextSkin,
        ["Alt+F4"] = CommandIds.Exit,
    };
}
