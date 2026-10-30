using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

/// <summary>
/// Entry-type icons of the design system (16 px grid, 1.25 px outline in the text colour, soft fill on folders and
/// archives), drawn straight onto the file list. Geometries are parsed once; drawing only pushes a transform.
/// </summary>
internal static class FileIcons
{
    private const double Grid = 16;

    public enum Kind : byte { Up, Folder, File, Executable, Archive, Image, Drive, Network, Add }

    private static readonly Dictionary<Kind, (Geometry Stroke, Geometry? Fill, double FillOpacity)> Shapes = new()
    {
        [Kind.Up] = (Path("M6 3.25L2.75 6.5 6 9.75 M2.75 6.5h6.5a4 4 0 0 1 4 4v2.25"), null, 0),
        [Kind.Folder] = Filled("M1.75 4.25c0-.83.67-1.5 1.5-1.5h3.1l1.4 1.5h5c.83 0 1.5.67 1.5 1.5v6.5c0 .83-.67 1.5-1.5 1.5H3.25c-.83 0-1.5-.67-1.5-1.5z", 0.22),
        [Kind.File] = (Path("M3.75 1.75h5.5l3 3v9.5h-8.5z M9.25 1.75v3h3"), null, 0),
        [Kind.Executable] = (Group(Rounded(1.75, 2.75, 12.5, 10.5, 1.5), Path("M1.75 5.75h12.5 M4.5 8.25l1.75 1.5-1.75 1.5 M8.25 11.25h3")), null, 0),
        [Kind.Archive] = (Path("M2.25 4.75h11.5v8.5c0 .55-.45 1-1 1h-9.5c-.55 0-1-.45-1-1z M1.75 2.25h12.5v2.5H1.75z M6.5 7.25h3"),
            Path("M2.25 4.75h11.5v8.5c0 .55-.45 1-1 1h-9.5c-.55 0-1-.45-1-1z"), 0.18),
        [Kind.Image] = (Group(Rounded(1.75, 2.75, 12.5, 10.5, 1.5), new EllipseGeometry(new Rect(4.25, 5, 2.5, 2.5)), Path("M2 12l3.75-3.5 2.75 2.5 2-1.75 3.5 3")), null, 0),
        [Kind.Drive] = (Group(Rounded(1.75, 4.75, 12.5, 6.5, 1.5)), new EllipseGeometry(new Rect(10.75, 7.25, 1.5, 1.5)), 1),
        [Kind.Network] = (Group(Rounded(2, 2, 12, 4.5, 1), Rounded(2, 9.5, 12, 4.5, 1), Path("M4.5 4.25h1 M4.5 11.75h1")), null, 0),
        [Kind.Add] = (Path("M8 3v10 M3 8h10"), null, 0),
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".svg", ".heic", ".avif",
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".msi", ".sh",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".zst", ".jar", ".nupkg", ".cab", ".iso",
    };

    public static Kind For(in VfsEntry entry) => entry.Kind switch
    {
        VfsEntryKind.ParentLink => Kind.Up,
        VfsEntryKind.Drive => Kind.Drive,
        VfsEntryKind.Host or VfsEntryKind.Share => Kind.Network,
        VfsEntryKind.Action => Kind.Add,
        VfsEntryKind.Archive => Kind.Archive,
        VfsEntryKind.Directory or VfsEntryKind.RegistryKey => Kind.Folder,
        _ => System.IO.Path.GetExtension(entry.Name) is var extension && ExecutableExtensions.Contains(extension) ? Kind.Executable
            : ArchiveExtensions.Contains(extension) ? Kind.Archive
            : ImageExtensions.Contains(extension) ? Kind.Image
            : Kind.File,
    };

    public static bool IsExecutable(string name) => ExecutableExtensions.Contains(System.IO.Path.GetExtension(name));

    public static bool IsArchive(string name) => ArchiveExtensions.Contains(System.IO.Path.GetExtension(name));

    /// <summary>Draws <paramref name="kind"/> in a <paramref name="size"/>-pixel square at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void Draw(DrawingContext context, Kind kind, IBrush brush, double x, double y, double size, double opacity = 1)
    {
        var (stroke, fill, fillOpacity) = Shapes[kind];
        var scale = size / Grid;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(Math.Round(x), Math.Round(y)));
        using var fade = opacity < 1 ? context.PushOpacity(opacity) : default(DrawingContext.PushedState?);
        if (fill is not null)
        {
            var color = (brush as ISolidColorBrush)?.Color ?? Colors.Gray;
            context.DrawGeometry(new ImmutableSolidColorBrush(color, fillOpacity), null, fill);
        }

        context.DrawGeometry(null, new ImmutablePen(brush.ToImmutable(), 1.25, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), stroke);
    }

    private static (Geometry, Geometry?, double) Filled(string data, double opacity)
    {
        var geometry = Path(data);
        return (geometry, geometry, opacity);
    }

    private static Geometry Path(string data) => Geometry.Parse(data);

    private static Geometry Rounded(double x, double y, double width, double height, double radius) =>
        new RectangleGeometry(new Rect(x, y, width, height), radius, radius);

    private static GeometryGroup Group(params Geometry[] parts)
    {
        var group = new GeometryGroup();
        foreach (var part in parts)
        {
            group.Children.Add(part);
        }

        return group;
    }
}
