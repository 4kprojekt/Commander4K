using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Commander4K.App.Theming;

namespace Commander4K.App.Panels;

/// <summary>
/// What every plugin-driven panel mode shares: a content area (row 1) with the busy card (<see cref="BusyOverlay"/>)
/// over it, and a status bar (row 2) with wave dots and text. Modes put their own header in row 0 and content in row 1,
/// call <see cref="AttachChrome"/>, and report work through <see cref="SetStatus"/> and <see cref="ShowError"/>, so the
/// loading state looks the same in a preview, a folder view, the mail client and a plugin panel.
/// </summary>
public abstract class ModeViewBase : Grid
{
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WaveDots _statusDots = new() { IsVisible = false, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly BusyOverlay _overlay = new();
    private readonly Border _statusBar;
    private SkinBrushes? _chrome;

    protected ModeViewBase()
    {
        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        _statusText.SetValue(Grid.ColumnProperty, 1);
        _statusBar = new Border { Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _statusDots, _statusText } } };
        SetRow(_statusBar, 2);
        SetRow(_overlay, 1);
    }

    /// <summary>The status line's text as last set.</summary>
    protected string? StatusText => _statusText.Text;

    /// <summary>Adds the busy card and the status bar; call after the header and content were added.</summary>
    protected void AttachChrome()
    {
        Children.Add(_overlay);
        Children.Add(_statusBar);
    }

    /// <summary>Status line text; <paramref name="busy"/> adds the wave dots and the card over the content.</summary>
    protected void SetStatus(string? text, bool busy = false)
    {
        _statusText.Text = text;
        _statusText.Foreground = _chrome?.StatusForeground;
        _statusDots.IsVisible = busy;
        _overlay.Set(busy, text);
    }

    protected void ShowError(string? text)
    {
        SetStatus(text);
        if (_chrome is not null)
        {
            _statusText.Foreground = _chrome.Error;
        }
    }

    protected void ApplyChromeSkin(SkinBrushes brushes)
    {
        _chrome = brushes;
        var modern = brushes.IsModern;
        _statusBar.Background = brushes.StatusBackground;
        _statusBar.BorderBrush = brushes.PanelBorder;
        _statusBar.BorderThickness = new Thickness(0, modern ? 1 : 0, 0, 0);
        _statusBar.Height = modern ? 26 : double.NaN;
        _statusBar.Padding = modern ? new Thickness(12, 0) : new Thickness(8, 2);
        _statusText.Foreground = brushes.StatusForeground;
        _statusText.FontFamily = brushes.FontFamily;
        _statusText.FontSize = modern ? Math.Max(11, brushes.FontSize - 1) : brushes.FontSize - 1;
        _statusDots.Foreground = brushes.StatusForeground;
        _overlay.ApplySkin(brushes);
    }
}
