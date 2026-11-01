using Avalonia.Controls;
using Avalonia.Media;

namespace Commander4K.App.Theming;

public static class WindowSkin
{
    public static void Apply(Window window, SkinBrushes brushes, bool panelSurface = false, bool raisedSurface = false)
    {
        window.Background = raisedSurface && brushes.IsModern
            ? brushes.Surface2
            : panelSurface ? brushes.PanelBackground : brushes.WindowBackground;
        window.Foreground = brushes.WindowForeground;
        window.FontFamily = brushes.FontFamily;
        window.FontSize = brushes.FontSize;
    }

    public static Button ApplyAccent(Button button, SkinBrushes brushes)
    {
        button.Background = brushes.Accent;
        button.BorderBrush = brushes.Accent;
        button.Foreground = brushes.AccentForeground;
        button.FontWeight = FontWeight.SemiBold;
        return button;
    }
}
