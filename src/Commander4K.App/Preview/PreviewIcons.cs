using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Commander4K.App.Preview;

/// <summary>
/// The outline icons previews may name (<see cref="PluginSdk.PreviewIconNames"/>), from the design "Tryb panelu — klient
/// poczty": 16-unit grid, 1.3 px stroke, round caps.
/// </summary>
internal static class PreviewIcons
{
    private const string Envelope = "M3.25 3.25h9.5c.83 0 1.5.67 1.5 1.5v6.5c0 .83-.67 1.5-1.5 1.5h-9.5c-.83 0-1.5-.67-1.5-1.5v-6.5c0-.83.67-1.5 1.5-1.5z M2.25 4l5.75 4.5L13.75 4";
    private const string Folder = "M1.75 4.25c0-.83.67-1.5 1.5-1.5h3.1l1.4 1.5h5c.83 0 1.5.67 1.5 1.5v6.5c0 .83-.67 1.5-1.5 1.5H3.25c-.83 0-1.5-.67-1.5-1.5z";
    private const string Sent = "M14 2L7 9 M14 2l-4.5 12L7 9 2 6.5z";
    private const string Pencil = "M10.5 2.5l3 3L6 13H3v-3z";
    private const string Trash = "M2.75 4.25h10.5 M6.25 4.25V2.75h3.5v1.5 M4 4.25l.6 9h6.8l.6-9";
    public const string Shield = "M8 1.75l5 2v4c0 3-2.2 5.3-5 6.5-2.8-1.2-5-3.5-5-6.5v-4z";
    public const string ShieldCheck = Shield + " M5.75 8l1.6 1.6L10.5 6.5";
    public const string Search = "M2.5 7a4.5 4.5 0 1 0 9 0a4.5 4.5 0 1 0 -9 0 M10.5 10.5l3.25 3.25";
    public const string Document = "M3.75 1.75h5.5l3 3v9.5h-8.5z M9.25 1.75v3h3";
    public const string Picture = "M3.25 2.75h9.5c.83 0 1.5.67 1.5 1.5v7.5c0 .83-.67 1.5-1.5 1.5h-9.5c-.83 0-1.5-.67-1.5-1.5v-7.5c0-.83.67-1.5 1.5-1.5z M4.25 6.25a1.25 1.25 0 1 0 2.5 0a1.25 1.25 0 1 0 -2.5 0 M2 12l3.75-3.5 2.75 2.5 2-1.75 3.5 3";
    public const string Download = "M8 2.5v8 M4.75 7.5L8 10.75l3.25-3.25 M3 13.5h10";
    public const string Attachment = "M13.25 7.5l-5.3 5.3a3.25 3.25 0 0 1-4.6-4.6l5.65-5.65a2.1 2.1 0 0 1 3 3L6.3 11.2a1 1 0 0 1-1.4-1.4l5.1-5.1";
    public const string Flag = "M3.5 14V2.5 M3.5 2.75h8.5l-2 2.75 2 2.75H3.5";

    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mail"] = Envelope,
        ["inbox"] = Envelope,
        ["sent"] = Sent,
        ["send"] = Sent,
        ["drafts"] = Pencil,
        ["edit"] = Pencil,
        ["trash"] = Trash,
        ["delete"] = Trash,
        ["archive"] = "M2.25 4.75h11.5v8.5c0 .55-.45 1-1 1h-9.5c-.55 0-1-.45-1-1z M1.75 2.25h12.5v2.5H1.75z M6.5 7.25h3",
        ["junk"] = Shield,
        ["folder"] = Folder,
        ["move"] = Folder + " M6 9h4.5 M9 7.25L10.75 9 9 10.75",
        ["reply"] = "M6.5 4L2.75 7.5 6.5 11 M2.75 7.5h6.5a4 4 0 0 1 4 4v1",
        ["replyAll"] = "M5 4L1.5 7.5 5 11 M8.5 4L5 7.5 8.5 11 M5 7.5h5a4 4 0 0 1 4 4v1",
        ["forward"] = "M9.5 4l3.75 3.5L9.5 11 M13.25 7.5h-6.5a4 4 0 0 0-4 4v1",
        ["flag"] = Flag,
        ["unread"] = "M5 8a3 3 0 1 0 6 0a3 3 0 1 0 -6 0",
        ["attachment"] = Attachment,
        ["new"] = "M8 3v10 M3 8h10",
        ["refresh"] = "M13.5 8a5.5 5.5 0 1 1-1.6-3.9 M13.5 2.5v2.75h-2.75",
        ["download"] = Download,
    };

    public static string? Find(string? name) => name is not null && Named.TryGetValue(name, out var data) ? data : null;

    /// <summary>The icon as a stroked path of <paramref name="size"/> px: the whole 16-unit grid scaled, so icons line up.</summary>
    public static Viewbox Create(string data, IBrush stroke, double size = 14, double thickness = 1.3)
    {
        var grid = new Canvas { Width = 16, Height = 16 };
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        });
        return new Viewbox { Width = size, Height = size, Child = grid, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
    }
}
