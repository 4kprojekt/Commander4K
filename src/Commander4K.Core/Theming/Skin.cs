using System.Text.Json.Serialization;

namespace Commander4K.Core.Theming;

[JsonConverter(typeof(JsonStringEnumConverter<PanelBorderStyle>))]
public enum PanelBorderStyle : byte
{
    None,
    /// <summary>Thin line (Midnight Commander, Total Commander).</summary>
    Single,
    /// <summary>Double box-drawing line (Norton Commander, FAR).</summary>
    Double,
}

/// <summary>Every colour the shell paints. Owner-drawn rows (<c>FileViewportCanvas</c>) read these directly.</summary>
public sealed record SkinPalette
{
    public required RgbColor WindowBackground { get; init; }
    public required RgbColor PanelBackground { get; init; }
    public required RgbColor PanelBorder { get; init; }
    public required RgbColor HeaderBackground { get; init; }
    public required RgbColor HeaderForeground { get; init; }

    public required RgbColor FileForeground { get; init; }
    public required RgbColor DirectoryForeground { get; init; }
    public required RgbColor HiddenForeground { get; init; }
    public required RgbColor ExecutableForeground { get; init; }
    public required RgbColor ArchiveForeground { get; init; }
    public required RgbColor NetworkForeground { get; init; }

    /// <summary>Marked (Insert / Space) entries.</summary>
    public required RgbColor SelectedForeground { get; init; }
    public required RgbColor CursorBackground { get; init; }
    public required RgbColor CursorForeground { get; init; }
    /// <summary>Cursor row of the panel that does not have focus.</summary>
    public required RgbColor InactiveCursorBackground { get; init; }

    public required RgbColor StatusBackground { get; init; }
    public required RgbColor StatusForeground { get; init; }
    public required RgbColor CommandLineBackground { get; init; }
    public required RgbColor CommandLineForeground { get; init; }

    public required RgbColor FunctionKeyNumberBackground { get; init; }
    public required RgbColor FunctionKeyNumberForeground { get; init; }
    public required RgbColor FunctionKeyLabelBackground { get; init; }
    public required RgbColor FunctionKeyLabelForeground { get; init; }

    public required RgbColor Accent { get; init; }
    public required RgbColor Progress { get; init; }
    public required RgbColor Error { get; init; }

    // Design-system tokens (Grafit). Optional: older and retro skins leave them out and get values derived from the
    // colours above (see SkinBrushes), so every skin keeps working.

    /// <summary>Default text of chrome: menus, buttons, labels.</summary>
    public RgbColor? WindowForeground { get; init; }
    /// <summary>Secondary text: sizes, dates, descriptions, placeholders.</summary>
    public RgbColor? MutedForeground { get; init; }
    /// <summary>Raised surfaces: buttons, dialogs, menus, dropdowns.</summary>
    public RgbColor? Surface2 { get; init; }
    public RgbColor? HoverBackground { get; init; }
    public RgbColor? PressedBackground { get; init; }
    /// <summary>Keyboard focus ring of inputs and buttons.</summary>
    public RgbColor? FocusRing { get; init; }
    /// <summary>Path bar of the panel that has focus (with a 3 px accent edge).</summary>
    public RgbColor? ActiveHeaderBackground { get; init; }
    public RgbColor? ActiveHeaderForeground { get; init; }
    /// <summary>Row background of marked entries (plus a selectedForeground edge).</summary>
    public RgbColor? SelectedBackground { get; init; }
    /// <summary>Text of a marked entry under the cursor.</summary>
    public RgbColor? CursorSelectedForeground { get; init; }
    /// <summary>Outline of the cursor row in the panel without focus.</summary>
    public RgbColor? InactiveCursorBorder { get; init; }
    public RgbColor? Warning { get; init; }
    public RgbColor? Success { get; init; }
    public RgbColor? WarningBackground { get; init; }
    public RgbColor? ErrorBackground { get; init; }
    /// <summary>Info rows, chosen chips, quotes, the current line in editors.</summary>
    public RgbColor? HighlightBackground { get; init; }
    public RgbColor? HighlightForeground { get; init; }
    /// <summary>Code blocks, stack traces, the command line of viewers.</summary>
    public RgbColor? CodeBackground { get; init; }
}

public sealed record SkinTypography
{
    /// <summary>Comma-separated fallback list, first installed font wins.</summary>
    public required string FontFamily { get; init; }

    public double FontSize { get; init; } = 13;

    /// <summary>Row height as a multiple of the font size.</summary>
    public double RowHeightFactor { get; init; } = 1.6;
}

public sealed record SkinChrome
{
    public PanelBorderStyle BorderStyle { get; init; } = PanelBorderStyle.Single;

    public double CornerRadius { get; init; }

    public bool ShowIcons { get; init; } = true;

    public bool ShowColumnHeaders { get; init; } = true;

    /// <summary>DOS-era look: names rendered in upper case (display only, paths are untouched).</summary>
    public bool UppercaseNames { get; init; }

    /// <summary>Base Avalonia theme variant for standard controls (dialogs, menus): <c>Dark</c> or <c>Light</c>.</summary>
    public string ThemeVariant { get; init; } = "Dark";
}

/// <summary>A complete look of the application; built-ins live in <see cref="BuiltInSkins"/>, custom ones in <c>config/skins</c>.</summary>
public sealed record Skin
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required SkinPalette Palette { get; init; }

    public required SkinTypography Typography { get; init; }

    public SkinChrome Chrome { get; init; } = new();
}
