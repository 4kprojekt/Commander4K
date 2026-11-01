using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class PreviewCardTests
{
    [Fact]
    public void Append_creates_series_and_keeps_last_120()
    {
        var chart = new ChartData(ChartKind.Line, []);
        for (var i = 0; i < 130; i++)
        {
            chart = chart.Append("cpu", new ChartPoint(i, i * 2));
        }

        var series = Assert.Single(chart.Series);
        Assert.Equal("cpu", series.Name);
        Assert.Equal(ChartData.MaxPoints, series.Points.Count);
        Assert.Equal(10, series.Points[0].X);
        Assert.Equal(129, series.Points[^1].X);
    }

    [Fact]
    public void Append_ignores_points_that_are_not_finite()
    {
        var chart = new ChartData(ChartKind.Line, [])
            .Append("cpu", new ChartPoint(1, 10))
            .Append("cpu", new ChartPoint(2, double.NaN))
            .Append("cpu", new ChartPoint(3, double.PositiveInfinity))
            .Append("cpu", new ChartPoint(double.NaN, 5))
            .Append("mem", new ChartPoint(4, double.NegativeInfinity));

        Assert.Equal([10.0], Assert.Single(chart.Series).Points.Select(p => p.Y));
    }

    [Fact]
    public void Append_leaves_other_series_untouched()
    {
        var chart = new ChartData(ChartKind.Bar, [new ChartSeries("mem", [new ChartPoint(1, 5)])], Unit: "%");

        var next = chart.Append("cpu", new ChartPoint(2, 7));

        Assert.Equal(["mem", "cpu"], next.Series.Select(s => s.Name));
        Assert.Equal(5, next.Series[0].Points.Single().Y);
        Assert.Equal("%", next.Unit);
        Assert.Single(chart.Series); // the original is unchanged
    }
}
