namespace Commander4K.Core.Input;

/// <summary>Built-in command ids referenced from <c>config/keymap.json</c>; names follow Total Commander's <c>cm_</c> style.</summary>
public static class CommandIds
{
    /// <summary><c>panel:&lt;pluginId&gt;</c> opens a panel plugin.</summary>
    public const string PanelPrefix = "panel:";

    /// <summary><c>plugin:&lt;pluginId&gt;:&lt;commandId&gt;</c> runs a command plugin's command.</summary>
    public const string PluginPrefix = "plugin:";

    public const string Help = "cm_Help";
    public const string CommandPalette = "cm_CommandPalette";
    public const string Settings = "cm_Settings";

    /// <summary>Switches a panel's mode: files, quick preview or any plugin panel (F2).</summary>
    public const string PanelModes = "cm_PanelModes";

    /// <summary>Settings filtered to plugin columns (Git, EXIF, SHA-256).</summary>
    public const string Columns = "cm_Columns";

    /// <summary>Packs the selection into a new archive in the other panel (Alt+F5).</summary>
    public const string Pack = "cm_Pack";

    /// <summary>Extracts the archive under the cursor into the other panel (Alt+F9).</summary>
    public const string Unpack = "cm_Unpack";

    /// <summary>Internal WebView2 browser in the opposite panel (Ctrl+Alt+W).</summary>
    public const string Browser = "cm_Browser";

    /// <summary>Asks the configured AI assistant (ChatGPT, Gemini, Claude…) in the internal browser (Ctrl+Alt+A).</summary>
    public const string AskAi = "cm_AskAi";

    /// <summary>Shows or hides all docked palettes (Ctrl+B).</summary>
    public const string TogglePalettes = "cm_TogglePalettes";

    /// <summary>Settings filtered to palette positions.</summary>
    public const string Palettes = "cm_Palettes";

    /// <summary>Adds the active folder to the favourites palette (Ctrl+D).</summary>
    public const string AddFavorite = "cm_AddFavorite";
    public const string CheckForUpdates = "cm_CheckForUpdates";
    public const string About = "cm_About";
    public const string AddConnection = "cm_AddConnection";
    public const string ToggleHidden = "cm_ToggleHidden";
    public const string SwitchPanel = "cm_SwitchPanel";
    public const string Open = "cm_Open";
    public const string GoToParent = "cm_GoToParent";
    public const string Refresh = "cm_Refresh";
    public const string NewTab = "cm_NewTab";
    public const string CloseTab = "cm_CloseTab";
    public const string LeftDriveMenu = "cm_LeftDriveMenu";
    public const string RightDriveMenu = "cm_RightDriveMenu";
    public const string OpenNetwork = "cm_OpenNetwork";
    public const string OpenRegistry = "cm_OpenRegistry";
    public const string GoToFrecent = "cm_GoToFrecent";
    public const string ToggleSelection = "cm_ToggleSelection";
    public const string CalculateDirSizes = "cm_CalculateDirSizes";
    public const string View = "cm_View";
    public const string QuickView = "cm_QuickView";
    public const string Edit = "cm_Edit";
    public const string Copy = "cm_Copy";
    public const string Move = "cm_Move";
    public const string MakeDirectory = "cm_MkDir";
    public const string Delete = "cm_Delete";
    public const string SearchAsync = "cm_SearchAsync";
    public const string Compare = "cm_Compare";
    public const string OpenTerminal = "cm_OpenTerminal";
    public const string CancelCurrentOperation = "cm_CancelCurrentOperation";
    /// <summary>Opens the skin picker (Norton Commander, Midnight Commander, modern...).</summary>
    public const string SelectSkin = "cm_SelectSkin";
    /// <summary>Switches straight to the next skin, for quick previewing.</summary>
    public const string NextSkin = "cm_NextSkin";
    public const string Exit = "cm_Exit";
}
