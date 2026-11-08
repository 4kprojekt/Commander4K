using System.Globalization;
using System.Text.Json;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Previews;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>
/// Dashboard cards that need no folder: a clock, a note and the disks of this computer. They read through the
/// folder preview's host like every other built-in card, so reads stay audited.
/// </summary>
public static class WidgetCards
{
    public const string Clock = "clock";
    public const string Note = "note";
    public const string Disks = "disks";

    private const int MaxNoteBytes = 64 * 1024;

    public static readonly IReadOnlyList<string> All = [Clock, Note, Disks];

    public static bool IsWidget(string? card) => card is not null && All.Contains(card, StringComparer.OrdinalIgnoreCase);

    /// <summary>How often a widget is read when the tile names no <c>refresh</c>.</summary>
    public static TimeSpan? DefaultRefresh(string? card) => card?.ToLowerInvariant() switch
    {
        Clock => TimeSpan.FromSeconds(15),
        Disks => TimeSpan.FromMinutes(1),
        _ => null,
    };

    /// <param name="target">The tile's resolved <c>open</c>: the note file or the disk to show; <c>null</c> = none.</param>
    public static async Task<PreviewCard> LoadAsync(string card, C4kItem tile, string? target, IPluginHost host, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = Options(tile.Options);
        switch (card.ToLowerInvariant())
        {
            case Clock:
                return ClockCard(tile, options, now);
            case Note:
                return await NoteCardAsync(tile, options, target, host, cancellationToken).ConfigureAwait(false);
            case Disks:
                return await DisksCardAsync(target, host, cancellationToken).ConfigureAwait(false);
            default:
                return new PreviewCard(null, Loc.T("Nieznany widok karty „{0}”.", card), "⚠", CardStatus.Error, [], []);
        }
    }

    /// <summary>Time in the tile's <c>options.timeZone</c> (IANA or Windows id), local time otherwise.</summary>
    public static PreviewCard ClockCard(C4kItem tile, JsonElement? options, DateTimeOffset now)
    {
        var zoneName = String(options, "timeZone");
        var items = new List<CardItem>();
        var shown = now.ToLocalTime();
        if (zoneName is not null)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(zoneName, out var zone))
            {
                shown = TimeZoneInfo.ConvertTime(now, zone);
            }
            else
            {
                items.Add(new CardValue(Loc.T("Strefa"), Loc.T("nieznana: {0}", zoneName), CardStatus.Warning));
            }
        }

        var culture = Loc.Culture;
        items.Insert(0, new CardValue(Loc.T("Data"), shown.ToString("dddd, d MMMM yyyy", culture)));
        items.Add(new CardValue(Loc.T("Tydzień"), ISOWeek.GetWeekOfYear(shown.DateTime).ToString(CultureInfo.InvariantCulture)));
        if (zoneName is not null)
        {
            items.Add(new CardValue(Loc.T("Przesunięcie"), "UTC" + shown.ToString("zzz", CultureInfo.InvariantCulture)));
        }

        var title = shown.ToString("HH:mm", CultureInfo.InvariantCulture);
        return new PreviewCard(title, tile.Description ?? zoneName, tile.Icon ?? "🕒", CardStatus.None, items, []);
    }

    /// <summary><c>options.text</c>, or the first part of the text file in <c>open</c>.</summary>
    private static async Task<PreviewCard> NoteCardAsync(C4kItem tile, JsonElement? options, string? target, IPluginHost host, CancellationToken cancellationToken)
    {
        if (String(options, "text") is { } text)
        {
            return new PreviewCard(null, tile.Description, tile.Icon ?? "🗒", CardStatus.None, [new CardText(text)], []);
        }

        if (target is null)
        {
            return new PreviewCard(null, Loc.T("Karta „note” potrzebuje „options.text” albo „open” z plikiem tekstowym."), "⚠", CardStatus.Error, [], []);
        }

        var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, target, MaxNoteBytes, cancellationToken).ConfigureAwait(false);
        var content = PreviewReading.Decode(data, length).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return new PreviewCard(null, tile.Description, tile.Icon ?? "🗒", CardStatus.None,
            [new CardText(truncated ? content + "…" : content)], [new CardAction(Loc.T("Otwórz"), target)]);
    }

    /// <summary>Every fixed disk with its free space; with <c>open</c>, only the disk holding that path.</summary>
    private static async Task<PreviewCard> DisksCardAsync(string? target, IPluginHost host, CancellationToken cancellationToken)
    {
        var info = await host.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
        var drives = info.Drives.Where(d => d.TotalBytes is > 0).ToList();
        if (target is not null && Path.GetPathRoot(target) is { Length: > 0 } root)
        {
            drives = [.. drives.Where(d => string.Equals(d.Name.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))];
        }

        if (drives.Count == 0)
        {
            return new PreviewCard(null, Loc.T("Brak dysków do pokazania."), "💽", CardStatus.Warning, [], []);
        }

        var worst = CardStatus.Ok;
        var items = new List<CardItem>();
        foreach (var drive in drives)
        {
            var total = drive.TotalBytes!.Value;
            var free = drive.FreeBytes ?? 0;
            var used = Math.Clamp((double)(total - free) / total, 0, 1);
            if (used >= 0.95)
            {
                worst = CardStatus.Error;
            }
            else if (used >= 0.9 && worst == CardStatus.Ok)
            {
                worst = CardStatus.Warning;
            }

            var label = drive.Label is { Length: > 0 } name ? $"{drive.Name.TrimEnd('\\')} {name}" : drive.Name.TrimEnd('\\');
            items.Add(new CardProgress(label, used, Loc.T("{0} wolne z {1}", FormatSize(free), FormatSize(total))));
        }

        return new PreviewCard(null, info.MachineName, "💽", worst, items, [new CardAction(Loc.T("Odśwież"), CardActions.Refresh)]);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 || value >= 100 ? "0" : "0.#", Loc.Culture) + " " + units[unit];
    }

    private static JsonElement? Options(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement? options, string name) =>
        options is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;
}
