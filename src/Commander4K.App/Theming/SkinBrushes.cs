using Avalonia.Media;
using Avalonia.Media.Immutable;
using Commander4K.Core.Theming;

namespace Commander4K.App.Theming;

/// <summary>Avalonia resources materialised once per skin, so rendering never allocates brushes or typefaces.</summary>
public sealed class SkinBrushes
{
    public SkinBrushes(Skin skin)
    {
        Skin = skin;
        var p = skin.Palette;
        WindowBackground = Brush(p.WindowBackground);
        PanelBackground = Brush(p.PanelBackground);
        PanelBorder = Brush(p.PanelBorder);
        HeaderBackground = Brush(p.HeaderBackground);
        HeaderForeground = Brush(p.HeaderForeground);
        FileForeground = Brush(p.FileForeground);
        DirectoryForeground = Brush(p.DirectoryForeground);
        HiddenForeground = Brush(p.HiddenForeground);
        ExecutableForeground = Brush(p.ExecutableForeground);
        ArchiveForeground = Brush(p.ArchiveForeground);
        NetworkForeground = Brush(p.NetworkForeground);
        SelectedForeground = Brush(p.SelectedForeground);
        CursorBackground = Brush(p.CursorBackground);
        CursorForeground = Brush(p.CursorForeground);
        InactiveCursorBackground = Brush(p.InactiveCursorBackground);
        StatusBackground = Brush(p.StatusBackground);
        StatusForeground = Brush(p.StatusForeground);
        CommandLineBackground = Brush(p.CommandLineBackground);
        CommandLineForeground = Brush(p.CommandLineForeground);
        FunctionKeyNumberBackground = Brush(p.FunctionKeyNumberBackground);
        FunctionKeyNumberForeground = Brush(p.FunctionKeyNumberForeground);
        FunctionKeyLabelBackground = Brush(p.FunctionKeyLabelBackground);
        FunctionKeyLabelForeground = Brush(p.FunctionKeyLabelForeground);
        Accent = Brush(p.Accent);
        Progress = Brush(p.Progress);
        Error = Brush(p.Error);
        Skeleton = new ImmutableSolidColorBrush(ToColor(p.HiddenForeground), 0.25);

        BorderPen = new ImmutablePen(PanelBorder, 1);
        InactiveCursorPen = new ImmutablePen(CursorBackground, 1);
        FontFamily = FontFamily.Parse(skin.Typography.FontFamily);
        Typeface = new Typeface(FontFamily);
        BoldTypeface = new Typeface(FontFamily, weight: FontWeight.SemiBold);
        FontSize = skin.Typography.FontSize;
        RowHeight = Math.Ceiling(skin.Typography.FontSize * skin.Typography.RowHeightFactor);
    }

    public Skin Skin { get; }

    public IImmutableSolidColorBrush WindowBackground { get; }
    public IImmutableSolidColorBrush PanelBackground { get; }
    public IImmutableSolidColorBrush PanelBorder { get; }
    public IImmutableSolidColorBrush HeaderBackground { get; }
    public IImmutableSolidColorBrush HeaderForeground { get; }
    public IImmutableSolidColorBrush FileForeground { get; }
    public IImmutableSolidColorBrush DirectoryForeground { get; }
    public IImmutableSolidColorBrush HiddenForeground { get; }
    public IImmutableSolidColorBrush ExecutableForeground { get; }
    public IImmutableSolidColorBrush ArchiveForeground { get; }
    public IImmutableSolidColorBrush NetworkForeground { get; }
    public IImmutableSolidColorBrush SelectedForeground { get; }
    public IImmutableSolidColorBrush CursorBackground { get; }
    public IImmutableSolidColorBrush CursorForeground { get; }
    public IImmutableSolidColorBrush InactiveCursorBackground { get; }
    public IImmutableSolidColorBrush StatusBackground { get; }
    public IImmutableSolidColorBrush StatusForeground { get; }
    public IImmutableSolidColorBrush CommandLineBackground { get; }
    public IImmutableSolidColorBrush CommandLineForeground { get; }
    public IImmutableSolidColorBrush FunctionKeyNumberBackground { get; }
    public IImmutableSolidColorBrush FunctionKeyNumberForeground { get; }
    public IImmutableSolidColorBrush FunctionKeyLabelBackground { get; }
    public IImmutableSolidColorBrush FunctionKeyLabelForeground { get; }
    public IImmutableSolidColorBrush Accent { get; }
    public IImmutableSolidColorBrush Progress { get; }
    public IImmutableSolidColorBrush Error { get; }
    public IImmutableSolidColorBrush Skeleton { get; }

    public ImmutablePen BorderPen { get; }
    public ImmutablePen InactiveCursorPen { get; }
    public FontFamily FontFamily { get; }
    public Typeface Typeface { get; }
    public Typeface BoldTypeface { get; }
    public double FontSize { get; }
    public double RowHeight { get; }

    public static Color ToColor(RgbColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static ImmutableSolidColorBrush Brush(RgbColor c) => new(ToColor(c));
}
