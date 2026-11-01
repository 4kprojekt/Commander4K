using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;

namespace Commander4K.App.Theming;

/// <summary>
/// Small design-system building blocks shared by windows and panel modes. Modern skins get the rounded, flat look;
/// retro skins (corner radius 0) keep stock buttons and square fields.
/// </summary>
public static class Ds
{
    /// <summary>Search field: 6 px corners, a ⌕ glyph inside on the left, the text box keeps its own focus ring.</summary>
    public static Grid SearchField(SkinBrushes b, TextBox box, double height = 34)
    {
        box.Height = height;
        box.Padding = new Thickness(34, 0, 10, 0);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.CornerRadius = new CornerRadius(b.IsModern ? 6 : 0);
        box.Background = b.IsModern ? b.HeaderBackground : b.PanelBackground;
        return new Grid().Children(
            box,
            new TextBlock().Text("⌕").FontSize(17).Foreground(b.MutedForeground).IsHitTestVisible(false)
                .HorizontalAlignment(HorizontalAlignment.Left).VerticalAlignment(VerticalAlignment.Center).Margin(11, 0, 0, 1));
    }

    /// <summary>Compact toolbar button: flat in modern skins (hover and pressed come from the Fluent resources the skin sets).</summary>
    public static Button ToolButton(SkinBrushes b, object content, string tip, Action click)
    {
        var button = new Button().Content(content).OnClick(_ => click());
        ToolTip.SetTip(button, tip);
        StyleToolButton(button, b);
        return button;
    }

    public static void StyleToolButton(Button button, SkinBrushes b)
    {
        if (b.IsModern)
        {
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Foreground = b.WindowForeground;
            button.CornerRadius = new CornerRadius(4);
            button.Height = 26;
            button.MinWidth = 28;
            button.Padding = new Thickness(6, 0);
        }
        else
        {
            button.ClearValue(TemplatedControl.BackgroundProperty);
            button.ClearValue(TemplatedControl.BorderThicknessProperty);
            button.ClearValue(TemplatedControl.ForegroundProperty);
            button.ClearValue(TemplatedControl.CornerRadiusProperty);
            button.Height = 24;
            button.MinWidth = 0;
            button.Padding = new Thickness(8, 2);
        }

        button.FontSize = Math.Max(12, b.FontSize);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
    }

    /// <summary>The 11 px uppercase-style muted caption used above fields and sections.</summary>
    public static TextBlock Caption(SkinBrushes b, string text) =>
        new TextBlock().Text(text).FontSize(11).FontWeight(FontWeight.SemiBold).Foreground(b.MutedForeground).Margin(0, 0, 0, 4);
}
