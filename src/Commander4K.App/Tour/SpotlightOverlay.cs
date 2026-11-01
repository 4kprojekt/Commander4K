using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Commander4K.App.Theming;

namespace Commander4K.App.Tour;

/// <summary>
/// A driver.js-like hint: the window dimmed except a cut-out around <c>target</c>, a focus ring around it, and a bubble
/// with an arrow and an OK button. A click anywhere closes it; the window closes it on keys (Esc/Enter are swallowed).
/// </summary>
public sealed class SpotlightOverlay : Control
{
    private const double Gap = 6;
    private const double ArrowSize = 8;

    private readonly SkinBrushes _brushes;
    private readonly Control _target;
    private readonly Border _bubble;
    private bool _closed;

    public SpotlightOverlay(SkinBrushes brushes, Control target, string text, string buttonText)
    {
        _brushes = brushes;
        _target = target;

        var ok = new Button
        {
            Content = buttonText,
            Padding = new Thickness(14, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = brushes.CursorForeground,
            Background = brushes.Accent,
            CornerRadius = new CornerRadius(brushes.IsModern ? 4 : 0),
        };
        ok.Click += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        _bubble = new Border
        {
            MaxWidth = 340,
            Padding = new Thickness(14, 12),
            Background = brushes.Surface2,
            BorderBrush = brushes.FocusRing,
            BorderThickness = new Thickness(brushes.IsModern ? 1 : 2),
            CornerRadius = new CornerRadius(brushes.IsModern ? 8 : 0),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = brushes.WindowForeground,
                        FontFamily = brushes.FontFamily,
                        FontSize = brushes.FontSize,
                    },
                    ok,
                },
            },
        };
        VisualChildren.Add(_bubble);
        LogicalChildren.Add(_bubble);
    }

    public event Action<SpotlightOverlay>? Closed;

    public bool IsClosed => _closed;

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Closed?.Invoke(this);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        Close();
    }

    /// <summary>The target's bounds in this overlay, grown by <see cref="Gap"/>; empty when it is not on screen.</summary>
    private Rect Hole() =>
        _target.IsEffectivelyVisible && _target.TranslatePoint(default, this) is { } origin
            ? new Rect(origin, _target.Bounds.Size).Inflate(Gap)
            : default;

    /// <summary>The whole window: the overlay layer is a canvas, which offers its children infinite space.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = TopLevel.GetTopLevel(this)?.ClientSize ?? availableSize;
        _bubble.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var hole = Hole();
        var size = _bubble.DesiredSize;
        var x = Math.Clamp(hole.Right - size.Width, 8, Math.Max(8, finalSize.Width - size.Width - 8));
        var below = hole.Bottom + ArrowSize + 4;
        var y = below + size.Height <= finalSize.Height ? below : Math.Max(8, hole.Top - ArrowSize - 4 - size.Height);
        _bubble.Arrange(new Rect(new Point(x, y), size));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var all = new Rect(Bounds.Size);
        var hole = Hole();
        var dim = new SolidColorBrush(_brushes.WindowBackground.Color, 0.6);
        var radius = _brushes.IsModern ? 8 : 0;
        context.FillRectangle(Brushes.Transparent, all); // Hit-testable everywhere, so any click closes it.
        var cut = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), new RectangleGeometry(hole, radius, radius));
        context.DrawGeometry(dim, null, cut);
        var bubble = _bubble.Bounds;
        if (hole.Width <= 0 || bubble.Width < 32)
        {
            return; // Not laid out yet.
        }

        context.DrawRectangle(null, new Pen(_brushes.FocusRing, 2), hole, radius, radius);

        // The arrow from the bubble toward the hole.
        var tipX = Math.Clamp(hole.Center.X, bubble.Left + 12, bubble.Right - 12);
        var pointsDown = bubble.Top < hole.Top;
        var baseY = pointsDown ? bubble.Bottom : bubble.Top;
        var tipY = pointsDown ? baseY + ArrowSize : baseY - ArrowSize;
        var arrow = new PolylineGeometry([new Point(tipX - ArrowSize, baseY), new Point(tipX, tipY), new Point(tipX + ArrowSize, baseY)], isFilled: true);
        context.DrawGeometry(_brushes.FocusRing, null, arrow);
    }
}
