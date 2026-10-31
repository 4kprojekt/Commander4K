using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.PluginSdk;

namespace Commander4K.App.Preview;

/// <summary>
/// A line or bar chart drawn straight onto the drawing context in the skin's colours: the Y range with its unit on the
/// left, series in accent, success, warning, error and highlight colours.
/// </summary>
public sealed class ChartControl(SkinBrushes brushes) : Control
{
    private ChartData? _chart;

    public ChartData? Chart
    {
        get => _chart;
        set
        {
            _chart = value;
            InvalidateVisual();
        }
    }

    public SkinBrushes Brushes { get; set; } = brushes;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Surface2, bounds, (float)Brushes.ControlRadius);
        if (_chart is not { } chart || chart.Series.All(s => s.Points.Count == 0))
        {
            return;
        }

        var points = chart.Series.SelectMany(s => s.Points).ToList();
        var min = chart.Min ?? Math.Min(0, points.Min(p => p.Y));
        var max = chart.Max ?? points.Max(p => p.Y);
        if (max <= min)
        {
            max = min + 1;
        }

        var maxLabel = Label(max, chart.Unit);
        var minLabel = Label(min, chart.Unit);
        var labelWidth = Math.Max(maxLabel.Width, minLabel.Width) + 6;
        context.DrawText(maxLabel, new Point(3, 2));
        context.DrawText(minLabel, new Point(3, bounds.Height - minLabel.Height - 2));

        var plot = new Rect(labelWidth, 4, Math.Max(1, bounds.Width - labelWidth - 4), Math.Max(1, bounds.Height - 8));
        context.DrawLine(new Pen(Brushes.PanelBorder, 1), plot.BottomLeft, plot.BottomRight);
        double Y(double value) => plot.Bottom - ((value - min) / (max - min) * plot.Height);

        if (chart.Kind == ChartKind.Bar)
        {
            var count = chart.Series.Max(s => s.Points.Count);
            var group = plot.Width / count;
            var bar = Math.Max(1, (group - 4) / chart.Series.Count);
            for (var s = 0; s < chart.Series.Count; s++)
            {
                for (var i = 0; i < chart.Series[s].Points.Count; i++)
                {
                    var top = Y(chart.Series[s].Points[i].Y);
                    var left = plot.Left + (i * group) + 2 + (s * bar);
                    context.FillRectangle(Colour(s), new Rect(left, Math.Min(top, plot.Bottom), bar, Math.Abs(plot.Bottom - top)));
                }
            }

            return;
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        double X(double value) => maxX > minX ? plot.Left + ((value - minX) / (maxX - minX) * plot.Width) : plot.Right;
        for (var s = 0; s < chart.Series.Count; s++)
        {
            var series = chart.Series[s].Points;
            var pen = new Pen(Colour(s), 1.6);
            if (series.Count == 1)
            {
                context.DrawEllipse(Colour(s), null, new Point(X(series[0].X), Y(series[0].Y)), 2.5, 2.5);
                continue;
            }

            for (var i = 1; i < series.Count; i++)
            {
                context.DrawLine(pen, new Point(X(series[i - 1].X), Y(series[i - 1].Y)), new Point(X(series[i].X), Y(series[i].Y)));
            }
        }
    }

    private IBrush Colour(int series) => (series % 5) switch
    {
        0 => Brushes.Accent,
        1 => Brushes.Success,
        2 => Brushes.Warning,
        3 => Brushes.Error,
        _ => Brushes.HighlightForeground,
    };

    private FormattedText Label(double value, string? unit) =>
        new(value.ToString(Math.Abs(value) >= 100 ? "N0" : "0.#", CultureInfo.CurrentCulture) + (unit is null ? string.Empty : " " + unit),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Max(9, Brushes.FontSize - 3), Brushes.MutedForeground);
}
