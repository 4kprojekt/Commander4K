using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Commander4K.App.Theming;

namespace Commander4K.App.Panels;

/// <summary>
/// Base of the app's own views that belong to a folder and open as a panel tab on it (settings on <c>config/</c>,
/// skins on <c>config/skins/</c>): the design system's frame painted from the skin, rebuilt by
/// <see cref="ApplySkin"/> without losing the scroll position. The panel's Pliki/Podgląd switch toggles between the
/// folder's files and this view; Esc (<see cref="CloseRequested"/>) shows the files.
/// </summary>
public abstract class FolderModeView : Panel, IPanelModeView
{
    private ScrollViewer? _scroll;

    protected FolderModeView(SkinBrushes brushes)
    {
        Brushes = brushes;
        PointerPressed += (_, _) => Activated?.Invoke();
    }

    public event Action? CloseRequested;

    public event Action? Activated;

    public abstract string Title { get; }

    public Control View => this;

    public virtual bool IsActivePanel { set { } }

    protected SkinBrushes Brushes { get; private set; }

    /// <summary>The window this view is shown in, as the owner of its dialogs.</summary>
    protected Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    public void ApplySkin(SkinBrushes brushes)
    {
        if (ReferenceEquals(brushes, Brushes) && Children.Count > 0)
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

    public virtual bool HandleKey(KeyEventArgs e) => false;

    public virtual void AppendText(string text)
    {
    }

    public virtual ValueTask DisposeModeAsync() => ValueTask.CompletedTask;

    /// <summary>Builds the content in the current skin; derived constructors call it once their fields are set.</summary>
    protected void Build()
    {
        Background = Brushes.PanelBackground;
        Children.Clear();
        Children.Add(BuildLayout());
    }

    protected abstract Control BuildLayout();

    /// <summary>A scrolling body whose position survives a skin change.</summary>
    protected ScrollViewer Scroll(Control content)
    {
        _scroll = new ScrollViewer { Content = content };
        return _scroll;
    }

    /// <summary>Back to the folder's files.</summary>
    protected void RequestClose() => CloseRequested?.Invoke();
}
