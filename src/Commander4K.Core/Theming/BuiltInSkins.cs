using Commander4K.Core.Localization;
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

    /// <summary>Design-system font stack for the modern skins (Windows 11 UI font first).</summary>
    private const string UiFonts = "Segoe UI Variable Text, Segoe UI, Inter";

    /// <summary>"Grafit, ciemna" from docs/DesignSystem — the default skin.</summary>
    public static Skin ModernDark { get; } = new()
    {
        Id = ModernDarkId,
        DisplayName = Loc.T("Grafit (ciemna)"),
        Typography = new SkinTypography { FontFamily = UiFonts, FontSize = 13, RowHeightFactor = 1.7 },
        Chrome = new SkinChrome { BorderStyle = PanelBorderStyle.Single, CornerRadius = 6, ThemeVariant = "Dark" },
        Palette = new SkinPalette
        {
            WindowBackground = RgbColor.Parse("#0B0E13"),
            PanelBackground = RgbColor.Parse("#12161D"),
            PanelBorder = RgbColor.Parse("#262D38"),
            HeaderBackground = RgbColor.Parse("#181D25"),
            HeaderForeground = RgbColor.Parse("#9AA6B6"),
            FileForeground = RgbColor.Parse("#D5DBE3"),
            DirectoryForeground = RgbColor.Parse("#F3C969"),
            HiddenForeground = RgbColor.Parse("#7D8796"),
            ExecutableForeground = RgbColor.Parse("#6EDC9F"),
            ArchiveForeground = RgbColor.Parse("#D49BFF"),
            NetworkForeground = RgbColor.Parse("#6CC4FF"),
            SelectedForeground = RgbColor.Parse("#FF8F5E"),
            CursorBackground = RgbColor.Parse("#1F56C4"),
            CursorForeground = White,
            InactiveCursorBackground = RgbColor.Parse("#1C2430"),
            StatusBackground = RgbColor.Parse("#181D25"),
            StatusForeground = RgbColor.Parse("#9AA6B6"),
            CommandLineBackground = RgbColor.Parse("#0E1218"),
            CommandLineForeground = RgbColor.Parse("#D5DBE3"),
            FunctionKeyNumberBackground = RgbColor.Parse("#0E1218"),
            FunctionKeyNumberForeground = RgbColor.Parse("#7FA8FF"),
            FunctionKeyLabelBackground = RgbColor.Parse("#181D25"),
            FunctionKeyLabelForeground = RgbColor.Parse("#C3CBD6"),
            Accent = RgbColor.Parse("#4C8DFF"),
            Progress = RgbColor.Parse("#43C59E"),
            Error = RgbColor.Parse("#FF6B6B"),
            WindowForeground = RgbColor.Parse("#C3CBD6"),
            MutedForeground = RgbColor.Parse("#7D8796"),
            Surface2 = RgbColor.Parse("#1A2029"),
            HoverBackground = RgbColor.Parse("#1D2430"),
            PressedBackground = RgbColor.Parse("#252D3A"),
            FocusRing = RgbColor.Parse("#4C8DFF"),
            ActiveHeaderBackground = RgbColor.Parse("#16243A"),
            ActiveHeaderForeground = RgbColor.Parse("#DCE7FF"),
            SelectedBackground = RgbColor.Parse("#2A1D17"),
            CursorSelectedForeground = RgbColor.Parse("#FFD9C7"),
            InactiveCursorBorder = RgbColor.Parse("#5A6A82"),
            Warning = RgbColor.Parse("#F2B544"),
            Success = RgbColor.Parse("#43C59E"),
            WarningBackground = RgbColor.Parse("#2A2212"),
            ErrorBackground = RgbColor.Parse("#2E1618"),
            HighlightBackground = RgbColor.Parse("#142440"),
            HighlightForeground = RgbColor.Parse("#8DB6FF"),
            CodeBackground = RgbColor.Parse("#0E1218"),
        },
    };

    /// <summary>"Grafit, jasna" from docs/DesignSystem.</summary>
    public static Skin ModernLight { get; } = new()
    {
        Id = ModernLightId,
        DisplayName = Loc.T("Grafit (jasna)"),
        Typography = new SkinTypography { FontFamily = UiFonts, FontSize = 13, RowHeightFactor = 1.7 },
        Chrome = new SkinChrome { BorderStyle = PanelBorderStyle.Single, CornerRadius = 6, ThemeVariant = "Light" },
        Palette = new SkinPalette
        {
            WindowBackground = RgbColor.Parse("#E6E9EE"),
            PanelBackground = White,
            PanelBorder = RgbColor.Parse("#D3D8DF"),
            HeaderBackground = RgbColor.Parse("#F4F6F9"),
            HeaderForeground = RgbColor.Parse("#4E5968"),
            FileForeground = RgbColor.Parse("#1D232B"),
            DirectoryForeground = RgbColor.Parse("#8A5A00"),
            HiddenForeground = RgbColor.Parse("#6B7480"),
            ExecutableForeground = RgbColor.Parse("#0B7A47"),
            ArchiveForeground = RgbColor.Parse("#8B3FC4"),
            NetworkForeground = RgbColor.Parse("#0B6FB8"),
            SelectedForeground = RgbColor.Parse("#C2410C"),
            CursorBackground = RgbColor.Parse("#1F56C4"),
            CursorForeground = White,
            InactiveCursorBackground = RgbColor.Parse("#EAF0F8"),
            StatusBackground = RgbColor.Parse("#F4F6F9"),
            StatusForeground = RgbColor.Parse("#4E5968"),
            CommandLineBackground = White,
            CommandLineForeground = RgbColor.Parse("#1D232B"),
            FunctionKeyNumberBackground = RgbColor.Parse("#E6E9EE"),
            FunctionKeyNumberForeground = RgbColor.Parse("#1F56C4"),
            FunctionKeyLabelBackground = RgbColor.Parse("#F4F6F9"),
            FunctionKeyLabelForeground = RgbColor.Parse("#2C3440"),
            Accent = RgbColor.Parse("#1F5BD6"),
            Progress = RgbColor.Parse("#138A6A"),
            Error = RgbColor.Parse("#C42B2B"),
            WindowForeground = RgbColor.Parse("#2C3440"),
            MutedForeground = RgbColor.Parse("#6B7480"),
            Surface2 = White,
            HoverBackground = RgbColor.Parse("#EEF2F7"),
            PressedBackground = RgbColor.Parse("#E1E7EF"),
            FocusRing = RgbColor.Parse("#1F5BD6"),
            ActiveHeaderBackground = RgbColor.Parse("#E4ECFB"),
            ActiveHeaderForeground = RgbColor.Parse("#163E8F"),
            SelectedBackground = RgbColor.Parse("#FDEEE6"),
            CursorSelectedForeground = RgbColor.Parse("#FFD9C7"),
            InactiveCursorBorder = RgbColor.Parse("#7A8DAB"),
            Warning = RgbColor.Parse("#9A5B00"),
            Success = RgbColor.Parse("#138A6A"),
            WarningBackground = RgbColor.Parse("#FFF4DD"),
            ErrorBackground = RgbColor.Parse("#FDECEC"),
            HighlightBackground = RgbColor.Parse("#E7EFFD"),
            HighlightForeground = RgbColor.Parse("#1747A6"),
            CodeBackground = RgbColor.Parse("#F4F6F9"),
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
        DisplayName = Loc.T("Total Commander (klasyczny)"),
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
