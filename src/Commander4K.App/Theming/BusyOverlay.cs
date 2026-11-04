using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Commander4K.App.Theming;

/// <summary>
/// The one "loading" card every panel mode (file preview, folder view, plugin panel, mail) shows in the middle of its
/// content area: wave dots and a short text. It never takes input, and appears only after a short delay so quick
/// loads do not flash it.
/// </summary>
public sealed class BusyOverlay : Border
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private readonly WaveDots _dots = new() { DotSize = 5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
    private readonly DispatcherTimer _delay = new() { Interval = Delay };
    private bool _wanted;

    public BusyOverlay()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(16, 10);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _dots, _text } };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            IsVisible = _wanted;
        };
    }

    /// <summary>Shows (after the delay) or hides the card; the text is the same line the status bar shows.</summary>
    public void Set(bool busy, string? text)
    {
        _wanted = busy;
        if (!busy)
        {
            _delay.Stop();
            IsVisible = false;
            return;
        }

        _text.Text = string.IsNullOrEmpty(text) ? "…" : text;
        if (!IsVisible && !_delay.IsEnabled)
        {
            _delay.Start();
        }
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        Background = brushes.PanelBackground;
        BorderBrush = brushes.PanelBorder;
        _dots.Foreground = brushes.Accent;
        _text.Foreground = brushes.WindowForeground;
        _text.FontFamily = brushes.FontFamily;
        _text.FontSize = brushes.FontSize;
    }
}
