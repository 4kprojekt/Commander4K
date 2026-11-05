namespace Commander4K.PluginSdk;

/// <summary>Colour of a card's status strip or of one value on it.</summary>
public enum CardStatus
{
    None,
    Ok,
    Warning,
    Error,
}

/// <summary>What a dashboard asks a preview to draw as a card.</summary>
/// <param name="Target">The tile's target; <c>null</c> for cards without one (an API request).</param>
/// <param name="OptionsJson">JSON object from the tile's <c>options</c>.</param>
/// <param name="Previous">The last card this preview returned for the tile, so refreshes can extend charts and compare readings.</param>
public sealed record PreviewCardRequest(VfsEntry? Target, string? OptionsJson, PreviewCard? Previous);

/// <summary>A small form of a preview shown on a dashboard: values, thumbnails, a chart, and buttons.</summary>
/// <param name="Title"><c>null</c> = the tile's title.</param>
public sealed record PreviewCard(
    string? Title,
    string? Subtitle,
    string? Icon,
    CardStatus Status,
    IReadOnlyList<CardItem> Items,
    IReadOnlyList<CardAction> Actions);

/// <summary>One element of a card, drawn in order.</summary>
public abstract record CardItem;

/// <summary>A label with a value: „CPU 12 %”.</summary>
public sealed record CardValue(string Label, string Value, CardStatus Status = CardStatus.None) : CardItem;

/// <summary>A row of small pictures (encoded PNG, JPEG…).</summary>
public sealed record CardThumbnails(IReadOnlyList<ReadOnlyMemory<byte>> Images) : CardItem;

public sealed record CardChart(ChartData Chart) : CardItem;

public sealed record CardText(string Text) : CardItem;

/// <summary>A bar filled to <paramref name="Fraction"/> (0–1): disk or memory use.</summary>
public sealed record CardProgress(string Label, double Fraction, string? Text = null) : CardItem;

/// <summary>A button on a card.</summary>
/// <param name="Target">Like <see cref="PreviewTile.Target"/>, plus <see cref="CardActions.Refresh"/>.</param>
public sealed record CardAction(string Label, string Target);

public static class CardActions
{
    /// <summary>Reads the card again.</summary>
    public const string Refresh = "c4k:refresh";

    /// <summary>Prefix of <see cref="OpenPanel"/> targets.</summary>
    public const string PanelPrefix = "c4k:panel/";

    /// <summary>Opens the panel <paramref name="panelId"/> (<c>builtin.sysinfo</c>, <c>&lt;plugin&gt;/&lt;panel&gt;</c>) opposite the dashboard.</summary>
    public static string OpenPanel(string panelId) => PanelPrefix + panelId;
}

public enum ChartKind
{
    Line,
    Bar,
}

/// <param name="X">For series over time: Unix time in seconds.</param>
public sealed record ChartPoint(double X, double Y, string? Label = null);

public sealed record ChartSeries(string Name, IReadOnlyList<ChartPoint> Points);

/// <summary>A chart the app draws in the skin's colours.</summary>
/// <param name="Min">Bottom of the Y axis; from the data when <c>null</c>.</param>
public sealed record ChartData(ChartKind Kind, IReadOnlyList<ChartSeries> Series, string? Unit = null, double? Min = null, double? Max = null)
{
    /// <summary>Points kept per series.</summary>
    public const int MaxPoints = 120;

    /// <summary>Adds a point to the series named <paramref name="series"/> (created when missing), keeping the last <see cref="MaxPoints"/>.</summary>
    public ChartData Append(string series, ChartPoint point)
    {
        // NaN or infinity (an API answering "NaN", a reading that overflowed) would break the drawing: skipped.
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            return this;
        }

        var all = Series.ToList();
        var index = all.FindIndex(s => s.Name == series);
        if (index < 0)
        {
            all.Add(new ChartSeries(series, [point]));
        }
        else
        {
            var points = all[index].Points;
            all[index] = all[index] with { Points = [.. points.Skip(Math.Max(0, points.Count + 1 - MaxPoints)), point] };
        }

        return this with { Series = all };
    }
}

/// <summary>A preview that can also draw itself as a card on a dashboard.</summary>
public interface IPreviewCardPlugin : IPreviewPlugin
{
    Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken);
}

/// <summary>
/// A panel that can also draw itself as a card on a dashboard (<c>"card": "builtin.sysinfo"</c>), so every panel can
/// be a widget. <see cref="PreviewCardRequest.Target"/> is the tile's <c>open</c> entry, or <c>null</c>.
/// </summary>
public interface IPanelCardPlugin : IPanelPlugin
{
    Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken);
}
