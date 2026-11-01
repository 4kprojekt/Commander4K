using Avalonia.Controls;
using Avalonia.Threading;
using Commander4K.App.Theming;

namespace Commander4K.App.Dialogs;

/// <summary>
/// Base of the non-modal tool windows (settings, AI providers, Claude Code connection): the design system's frame
/// (<see cref="Ds.WindowHeader"/>, a scrolling body, <see cref="Ds.Footer"/>) on the panel surface, rebuilt in a new
/// skin by <see cref="ApplySkin"/> without losing the scroll position. <c>MainWindow.FollowSkin</c> calls it.
/// </summary>
public abstract class ToolWindow : Window
{
    private ScrollViewer? _scroll;

    protected ToolWindow(string title, SkinBrushes brushes, double width, double height, double minWidth)
    {
        Brushes = brushes;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = minWidth;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    protected SkinBrushes Brushes { get; private set; }

    public void ApplySkin(SkinBrushes brushes)
    {
        if (ReferenceEquals(brushes, Brushes) && Content is not null)
        {
            return;
        }

        Brushes = brushes;
        var offset = _scroll?.Offset;
        Build();
        if (offset is { } kept)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_scroll is not null)
                {
                    _scroll.Offset = kept;
                }
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Builds the content in the current skin; derived constructors call it once their fields are set.</summary>
    protected void Build()
    {
        Ds.ApplySurface(this, Brushes, WindowSurface.Panel);
        Content = BuildLayout();
    }

    /// <summary>The whole window content; use <see cref="Frame"/> and <see cref="Scroll"/>.</summary>
    protected abstract Control BuildLayout();

    /// <summary>A scrolling body whose position survives a skin change.</summary>
    protected ScrollViewer Scroll(Control content)
    {
        _scroll = new ScrollViewer { Content = content };
        return _scroll;
    }

    /// <summary>Header band, footer band and the body between them.</summary>
    protected DockPanel Frame(string heading, string? description, Control body, Control? footer, Control? headerTrailing = null) =>
        Ds.Frame(Ds.WindowHeader(Brushes, heading, description, headerTrailing), footer, body);
}
