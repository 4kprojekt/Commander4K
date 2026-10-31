using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Commander4K.App.Theming;

/// <summary>
/// The app's one busy indicator: three dots rising and fading in a wave. <see cref="Draw"/> paints it into owner-drawn
/// controls (the file list); the control form sits next to status texts. Every instance shares one clock, so all
/// indicators on screen move in step.
/// </summary>
public sealed class WaveDots : Control
{
    /// <summary>Frame interval of the animation (30 Hz).</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherTimer _timer;
    private IBrush? _foreground;
    private bool _attached;

    public WaveDots()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, (_, _) => InvalidateVisual());
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                UpdateTimer();
            }
        };
    }

    public IBrush? Foreground
    {
        get => _foreground;
        set
        {
            _foreground = value;
            InvalidateVisual();
        }
    }

    public double DotSize { get; set; } = 4;

    /// <summary>Width of the three dots for a given dot size.</summary>
    public static double WidthFor(double dotSize) => dotSize * 3 + dotSize * 1.2 * 2;

    /// <summary>Draws the dots with their left edge at <paramref name="x"/>, centred on <paramref name="centerY"/>.</summary>
    public static void Draw(DrawingContext context, IBrush brush, double x, double centerY, double dotSize)
    {
        var phase = Environment.TickCount64 / 1000.0 * Math.PI * 2 / 1.1; // One wave every 1.1 s.
        var amplitude = dotSize * 0.6;
        for (var i = 0; i < 3; i++)
        {
            var wave = Math.Sin(phase - (i * 0.9)); // -1..1, each dot a little behind the previous one.
            var lift = Math.Max(0, wave) * amplitude;
            using (context.PushOpacity(0.35 + (0.65 * ((wave + 1) / 2))))
            {
                var cx = x + (dotSize / 2) + (i * dotSize * 2.2);
                context.DrawEllipse(brush, null, new Point(cx, centerY - lift), dotSize / 2, dotSize / 2);
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_foreground is { } brush)
        {
            Draw(context, brush, (Bounds.Width - WidthFor(DotSize)) / 2, Bounds.Height / 2, DotSize);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(WidthFor(DotSize), DotSize * 3);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        if (IsVisible && _attached)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }
}
