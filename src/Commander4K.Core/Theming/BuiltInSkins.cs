namespace Commander4K.Core.Theming;

/// <summary>Skins shipped with the application. Colours of the retro skins use the standard 16-colour CGA/VGA palette.</summary>
public static class BuiltInSkins
{
    public const string ModernDarkId = "modern-dark";
    public const string ModernLightId = "modern-light";
    public const string NortonCommanderId = "norton-commander";
    public const string MidnightCommanderId = "midnight-commander";
    public const string TotalCommanderClassicId = "total-commander-classic";

    private const string MonospaceFonts = "Cascadia Mono, Consolas, Courier New";

    // CGA / VGA text-mode palette.
    private static readonly RgbColor Black = RgbColor.Parse("#000000");
    private static readonly RgbColor Blue = RgbColor.Parse("#0000AA");
    private static readonly RgbColor Green = RgbColor.Parse("#00AA00");
    private static readonly RgbColor Cyan = RgbColor.Parse("#00AAAA");
    private static readonly RgbColor Red = RgbColor.Parse("#AA0000");
    private static readonly RgbColor LightGray = RgbColor.Parse("#AAAAAA");
    private static readonly RgbColor DarkGray = RgbColor.Parse("#555555");
    private static readonly RgbColor LightGreen = RgbColor.Parse("#55FF55");
    private static readonly RgbColor LightCyan = RgbColor.Parse("#55FFFF");
    private static readonly RgbColor LightRed = RgbColor.Parse("#FF5555");
    private static readonly RgbColor LightMagenta = RgbColor.Parse("#FF55FF");
    private static readonly RgbColor Yellow = RgbColor.Parse("#FFFF55");
    private static readonly RgbColor White = RgbColor.Parse("#FFFFFF");

    public static Skin ModernDark { get; } = new()
    {
        Id = ModernDarkId,
        DisplayName = "Nowoczesny (ciemny)",
        Typography = new SkinTypography { FontFamily = "Segoe UI Variable Text, Segoe UI, Inter", FontSize = 13 },
        Chrome = new SkinChrome { BorderStyle = PanelBorderStyle.Single, CornerRadius = 6, ThemeVariant = "Dark" },
        Palette = new SkinPalette
        {
            WindowBackground = RgbColor.Parse("#1B1B1F"),
            PanelBackground = RgbColor.Parse("#222228"),
            PanelBorder = RgbColor.Parse("#34343C"),
            HeaderBackground = RgbColor.Parse("#2A2A31"),
            HeaderForeground = RgbColor.Parse("#A0A0AB"),
            FileForeground = RgbColor.Parse("#E4E4E7"),
            DirectoryForeground = RgbColor.Parse("#7DB4FF"),
            HiddenForeground = RgbColor.Parse("#71717A"),
            ExecutableForeground = RgbColor.Parse("#6EE7A0"),
            ArchiveForeground = RgbColor.Parse("#F0B86E"),
            NetworkForeground = RgbColor.Parse("#C4A5FF"),
            SelectedForeground = RgbColor.Parse("#FFD166"),
            CursorBackground = RgbColor.Parse("#2F5FD0"),
            CursorForeground = White,
            InactiveCursorBackground = RgbColor.Parse("#33333B"),
            StatusBackground = RgbColor.Parse("#1F1F24"),
            StatusForeground = RgbColor.Parse("#A0A0AB"),
            CommandLineBackground = RgbColor.Parse("#18181C"),
            CommandLineForeground = RgbColor.Parse("#E4E4E7"),
            FunctionKeyNumberBackground = RgbColor.Parse("#1B1B1F"),
            FunctionKeyNumberForeground = RgbColor.Parse("#7DB4FF"),
            FunctionKeyLabelBackground = RgbColor.Parse("#2A2A31"),
            FunctionKeyLabelForeground = RgbColor.Parse("#E4E4E7"),
            Accent = RgbColor.Parse("#4F8CFF"),
            Progress = RgbColor.Parse("#4F8CFF"),
            Error = RgbColor.Parse("#FF6B6B"),
        },
    };

    public static Skin ModernLight { get; } = new()
    {
        Id = ModernLightId,
        DisplayName = "Nowoczesny (jasny)",
        Typography = new SkinTypography { FontFamily = "Segoe UI Variable Text, Segoe UI, Inter", FontSize = 13 },
        Chrome = new SkinChrome { BorderStyle = PanelBorderStyle.Single, CornerRadius = 6, ThemeVariant = "Light" },
        Palette = new SkinPalette
        {
            WindowBackground = RgbColor.Parse("#F3F3F5"),
            PanelBackground = White,
            PanelBorder = RgbColor.Parse("#DCDCE0"),
            HeaderBackground = RgbColor.Parse("#F7F7F9"),
            HeaderForeground = RgbColor.Parse("#5F5F6B"),
            FileForeground = RgbColor.Parse("#1F1F24"),
            DirectoryForeground = RgbColor.Parse("#1E5BD8"),
            HiddenForeground = RgbColor.Parse("#9A9AA5"),
            ExecutableForeground = RgbColor.Parse("#1C8A4C"),
            ArchiveForeground = RgbColor.Parse("#A8620F"),
            NetworkForeground = RgbColor.Parse("#6B3FD0"),
            SelectedForeground = RgbColor.Parse("#C2410C"),
            CursorBackground = RgbColor.Parse("#D6E4FF"),
            CursorForeground = RgbColor.Parse("#0B1F4D"),
            InactiveCursorBackground = RgbColor.Parse("#ECECF0"),
            StatusBackground = RgbColor.Parse("#ECECF0"),
            StatusForeground = RgbColor.Parse("#5F5F6B"),
            CommandLineBackground = White,
            CommandLineForeground = RgbColor.Parse("#1F1F24"),
            FunctionKeyNumberBackground = RgbColor.Parse("#F3F3F5"),
            FunctionKeyNumberForeground = RgbColor.Parse("#1E5BD8"),
            FunctionKeyLabelBackground = RgbColor.Parse("#E4E4EA"),
            FunctionKeyLabelForeground = RgbColor.Parse("#1F1F24"),
            Accent = RgbColor.Parse("#2F6FED"),
            Progress = RgbColor.Parse("#2F6FED"),
            Error = RgbColor.Parse("#D92D2D"),
        },
    };

    /// <summary>Norton Commander 5: cyan on blue, double frames, white directories, yellow marks, cyan cursor bar.</summary>
    public static Skin NortonCommander { get; } = new()
    {
        Id = NortonCommanderId,
        DisplayName = "Norton Commander",
        Typography = new SkinTypography { FontFamily = MonospaceFonts, FontSize = 14, RowHeightFactor = 1.3 },
        Chrome = new SkinChrome
        {
            BorderStyle = PanelBorderStyle.Double,
            ShowIcons = false,
            UppercaseNames = true,
            ThemeVariant = "Dark",
        },
        Palette = new SkinPalette
        {
            WindowBackground = Black,
            PanelBackground = Blue,
            PanelBorder = LightCyan,
            HeaderBackground = Blue,
            HeaderForeground = Yellow,
            FileForeground = LightCyan,
            DirectoryForeground = White,
            HiddenForeground = Cyan,
            ExecutableForeground = LightCyan,
            ArchiveForeground = LightCyan,
            NetworkForeground = White,
            SelectedForeground = Yellow,
            CursorBackground = Cyan,
            CursorForeground = Black,
            InactiveCursorBackground = Blue,
            StatusBackground = Blue,
            StatusForeground = LightCyan,
            CommandLineBackground = Black,
            CommandLineForeground = LightGray,
            FunctionKeyNumberBackground = Black,
            FunctionKeyNumberForeground = White,
            FunctionKeyLabelBackground = Cyan,
            FunctionKeyLabelForeground = Black,
            Accent = Cyan,
            Progress = LightCyan,
            Error = LightRed,
        },
    };

    /// <summary>Midnight Commander default: white on blue, single frames, green executables, yellow marks.</summary>
    public static Skin MidnightCommander { get; } = new()
    {
        Id = MidnightCommanderId,
        DisplayName = "Midnight Commander",
        Typography = new SkinTypography { FontFamily = MonospaceFonts, FontSize = 14, RowHeightFactor = 1.3 },
        Chrome = new SkinChrome { BorderStyle = PanelBorderStyle.Single, ShowIcons = false, ThemeVariant = "Dark" },
        Palette = new SkinPalette
        {
            WindowBackground = Black,
            PanelBackground = Blue,
            PanelBorder = LightGray,
            HeaderBackground = Blue,
            HeaderForeground = Yellow,
            FileForeground = LightGray,
            DirectoryForeground = White,
            HiddenForeground = DarkGray,
            ExecutableForeground = LightGreen,
            ArchiveForeground = LightMagenta,
            NetworkForeground = LightCyan,
            SelectedForeground = Yellow,
            CursorBackground = Cyan,
            CursorForeground = Black,
            InactiveCursorBackground = Blue,
            StatusBackground = Blue,
            StatusForeground = LightGray,
            CommandLineBackground = Black,
            CommandLineForeground = LightGray,
            FunctionKeyNumberBackground = Black,
            FunctionKeyNumberForeground = White,
            FunctionKeyLabelBackground = Cyan,
            FunctionKeyLabelForeground = Black,
            Accent = Cyan,
            Progress = LightGreen,
            Error = LightRed,
        },
    };

    /// <summary>Total Commander classic: Windows-style white panels, navy cursor bar, red marked files.</summary>
    public static Skin TotalCommanderClassic { get; } = new()
    {
        Id = TotalCommanderClassicId,
        DisplayName = "Total Commander (klasyczny)",
        Typography = new SkinTypography { FontFamily = "Segoe UI, Tahoma, MS Sans Serif", FontSize = 12, RowHeightFactor = 1.45 },
        Chrome = new SkinChrome { BorderStyle = PanelBorderStyle.Single, ThemeVariant = "Light" },
        Palette = new SkinPalette
        {
            WindowBackground = RgbColor.Parse("#F0F0F0"),
            PanelBackground = White,
            PanelBorder = RgbColor.Parse("#A0A0A0"),
            HeaderBackground = RgbColor.Parse("#F0F0F0"),
            HeaderForeground = Black,
            FileForeground = Black,
            DirectoryForeground = Black,
            HiddenForeground = RgbColor.Parse("#808080"),
            ExecutableForeground = Black,
            ArchiveForeground = Black,
            NetworkForeground = Black,
            SelectedForeground = RgbColor.Parse("#FF0000"),
            CursorBackground = RgbColor.Parse("#000080"),
            CursorForeground = White,
            InactiveCursorBackground = RgbColor.Parse("#C0C0C0"),
            StatusBackground = RgbColor.Parse("#F0F0F0"),
            StatusForeground = Black,
            CommandLineBackground = White,
            CommandLineForeground = Black,
            FunctionKeyNumberBackground = RgbColor.Parse("#F0F0F0"),
            FunctionKeyNumberForeground = Black,
            FunctionKeyLabelBackground = RgbColor.Parse("#F0F0F0"),
            FunctionKeyLabelForeground = Black,
            Accent = RgbColor.Parse("#000080"),
            Progress = Green,
            Error = Red,
        },
    };

    public static IReadOnlyList<Skin> All { get; } =
        [ModernDark, ModernLight, NortonCommander, MidnightCommander, TotalCommanderClassic];
}
