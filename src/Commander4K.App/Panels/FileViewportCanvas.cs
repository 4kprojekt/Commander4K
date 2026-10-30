using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Theming;
using Commander4K.Core.Theming;
using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

/// <summary>
/// Owner-drawn file list. Instead of one control per cell it paints only the rows inside the viewport straight onto
/// the <see cref="DrawingContext"/> (Skia), so a 500 000-entry directory costs the same per frame as a 50-entry one.
/// </summary>
public sealed class FileViewportCanvas : Control
{
    private static readonly string[] SpinnerFrames = ["|", "/", "-", "\\"];
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private readonly DispatcherTimer _spinner;
    private PanelTab? _tab;
    private SkinBrushes? _brushes;
    private bool _isActive;
    private int _spinnerFrame;

    public FileViewportCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        _spinner = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Render, (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            InvalidateVisual();
        });
    }

    /// <summary>Raised when the user clicks into the panel (it becomes the active one).</summary>
    public event Action? Activated;

    /// <summary>Raised on double-click of a row.</summary>
    public event Action<VfsEntry>? RowActivated;

    public event Action<SortColumn>? HeaderClicked;

    public PanelTab? Tab
    {
        get => _tab;
        set
        {
            if (_tab is not null)
            {
                _tab.Changed -= OnTabChanged;
            }

            _tab = value;
            if (_tab is not null)
            {
                _tab.Changed += OnTabChanged;
            }

            OnTabChanged();
        }
    }

    public SkinBrushes? Brushes
    {
        get => _brushes;
        set
        {
            _brushes = value;
            InvalidateVisual();
        }
    }

    public bool IsActivePanel
    {
        get => _isActive;
        set
        {
            _isActive = value;
            InvalidateVisual();
        }
    }

    /// <summary>Rows that fit in the viewport; drives PageUp/PageDown.</summary>
    public int VisibleRows => _brushes is null ? 1 : Math.Max(1, (int)(Layout().RowsArea.Height / _brushes.RowHeight));

    public void EnsureCursorVisible()
    {
        if (_tab is null)
        {
            return;
        }

        var visible = VisibleRows;
        if (_tab.Cursor < _tab.TopIndex)
        {
            _tab.TopIndex = _tab.Cursor;
        }
        else if (_tab.Cursor >= _tab.TopIndex + visible)
        {
            _tab.TopIndex = _tab.Cursor - visible + 1;
        }

        _tab.TopIndex = Math.Clamp(_tab.TopIndex, 0, Math.Max(0, _tab.Entries.Count - visible));
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var b = _brushes;
        if (b is null)
        {
            return;
        }

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(b.PanelBackground, bounds);
        DrawFrame(context, b, bounds);

        var layout = Layout();
        var tab = _tab;
        if (b.Skin.Chrome.ShowColumnHeaders)
        {
            DrawHeader(context, b, layout, tab?.Sort ?? SortOrder.Default);
        }

        if (tab is null)
        {
            return;
        }

        var rows = layout.RowsArea;
        using (context.PushClip(rows))
        {
            if (tab.Entries.Count <= 1 && tab.IsLoading)
            {
                DrawSkeleton(context, b, rows, tab.Entries.Count);
            }

            var visible = (int)Math.Ceiling(rows.Height / b.RowHeight);
            var top = Math.Clamp(tab.TopIndex, 0, Math.Max(0, tab.Entries.Count - 1));
            for (var i = top; i < Math.Min(tab.Entries.Count, top + visible); i++)
            {
                var y = rows.Y + ((i - top) * b.RowHeight);
                DrawRow(context, b, layout, tab, tab.Entries[i], i, y);
            }

            DrawScrollbar(context, b, rows, tab, visible);
        }

        if (tab.Error is { } error)
        {
            DrawCentered(context, b, rows, error, b.Error);
        }

        if (tab.IsLoading)
        {
            DrawLoadingBar(context, b, layout, tab);
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Activated?.Invoke();
        if (_tab is null || _brushes is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        var layout = Layout();
        if (layout.HeaderArea.Contains(point))
        {
            HeaderClicked?.Invoke(point.X >= layout.DateX ? SortColumn.Date : point.X >= layout.SizeX ? SortColumn.Size : SortColumn.Name);
            return;
        }

        if (!layout.RowsArea.Contains(point))
        {
            return;
        }

        var index = _tab.TopIndex + (int)((point.Y - layout.RowsArea.Y) / _brushes.RowHeight);
        if (index >= _tab.Entries.Count)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            _tab.ToggleMark(_tab.Entries[index]); // Total Commander: right click marks.
        }

        _tab.MoveCursor(index);
        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            RowActivated?.Invoke(_tab.Entries[index]);
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_tab is null)
        {
            return;
        }

        _tab.TopIndex = Math.Clamp(_tab.TopIndex - (int)(e.Delta.Y * 3), 0, Math.Max(0, _tab.Entries.Count - VisibleRows));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSpinner();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _spinner.Stop();
    }

    private void OnTabChanged()
    {
        UpdateSpinner();
        if (_tab is not null && Bounds.Height > 0)
        {
            EnsureCursorVisible();
        }

        InvalidateVisual();
    }

    private void UpdateSpinner()
    {
        if (_tab is { IsLoading: true } || _tab?.SizesInProgress.Count > 0)
        {
            _spinner.Start();
        }
        else
        {
            _spinner.Stop();
        }
    }

    private CanvasLayout Layout()
    {
        var b = _brushes;
        if (b is null)
        {
            return default;
        }

        var inset = b.Skin.Chrome.BorderStyle switch
        {
            PanelBorderStyle.Double => 6,
            PanelBorderStyle.Single => 1,
            _ => 0,
        };
        var inner = new Rect(Bounds.Size).Deflate(inset);
        var headerHeight = b.Skin.Chrome.ShowColumnHeaders ? b.RowHeight : 0;
        var header = new Rect(inner.X, inner.Y, inner.Width, headerHeight);
        var footer = _tab is { IsLoading: true } ? b.RowHeight : 0;
        var rows = new Rect(inner.X, inner.Y + headerHeight, inner.Width, Math.Max(0, inner.Height - headerHeight - footer));

        var charWidth = b.FontSize * 0.6;
        var dateWidth = inner.Width > 420 ? charWidth * 17 : 0;
        var sizeWidth = inner.Width > 260 ? charWidth * 11 : 0;
        var dateX = inner.Right - dateWidth - 6;
        var sizeX = dateX - sizeWidth - 6;
        return new CanvasLayout(inner, header, rows, sizeX, sizeWidth, dateX, dateWidth);
    }

    private static void DrawFrame(DrawingContext context, SkinBrushes b, Rect bounds)
    {
        switch (b.Skin.Chrome.BorderStyle)
        {
            case PanelBorderStyle.Single:
                context.DrawRectangle(null, b.BorderPen, bounds.Deflate(0.5), b.Skin.Chrome.CornerRadius, b.Skin.Chrome.CornerRadius);
                break;
            case PanelBorderStyle.Double:
                // Box-drawing look of Norton Commander: two parallel lines.
                context.DrawRectangle(null, b.BorderPen, bounds.Deflate(1.5));
                context.DrawRectangle(null, b.BorderPen, bounds.Deflate(4.5));
                break;
        }
    }

    private static void DrawHeader(DrawingContext context, SkinBrushes b, CanvasLayout layout, SortOrder sort)
    {
        context.FillRectangle(b.HeaderBackground, layout.HeaderArea);
        var arrow = sort.Descending ? " ▼" : " ▲";
        var y = layout.HeaderArea.Y;
        DrawText(context, b, "Nazwa" + (sort.Column is SortColumn.Name or SortColumn.Extension ? arrow : ""), b.HeaderForeground,
            layout.Inner.X + 6, y, layout.SizeX - layout.Inner.X - 12, b.BoldTypeface);
        if (layout.SizeWidth > 0)
        {
            DrawText(context, b, "Rozmiar" + (sort.Column == SortColumn.Size ? arrow : ""), b.HeaderForeground,
                layout.SizeX, y, layout.SizeWidth, b.BoldTypeface, TextAlignment.Right);
        }

        if (layout.DateWidth > 0)
        {
            DrawText(context, b, "Data" + (sort.Column == SortColumn.Date ? arrow : ""), b.HeaderForeground,
                layout.DateX, y, layout.DateWidth, b.BoldTypeface);
        }

        context.DrawLine(b.BorderPen, layout.HeaderArea.BottomLeft, layout.HeaderArea.BottomRight);
    }

    private void DrawRow(DrawingContext context, SkinBrushes b, CanvasLayout layout, PanelTab tab, VfsEntry entry, int index, double y)
    {
        var rowRect = new Rect(layout.Inner.X, y, layout.Inner.Width, b.RowHeight);
        var isCursor = index == tab.Cursor;
        var isMarked = tab.Marked.Contains(entry.Path);
        if (isCursor)
        {
            if (_isActive)
            {
                context.FillRectangle(b.CursorBackground, rowRect);
            }
            else
            {
                context.FillRectangle(b.InactiveCursorBackground, rowRect);
                context.DrawRectangle(null, b.InactiveCursorPen, rowRect.Deflate(0.5));
            }
        }

        var foreground = isCursor && _isActive && !isMarked ? b.CursorForeground
            : isMarked ? b.SelectedForeground
            : ForegroundFor(b, entry);

        var x = layout.Inner.X + 6;
        if (b.Skin.Chrome.ShowIcons)
        {
            DrawIcon(context, foreground, entry, x, y, b.RowHeight);
            x += b.RowHeight;
        }

        var name = b.Skin.Chrome.UppercaseNames ? entry.Name.ToUpperInvariant() : entry.Name;
        if (isMarked && !b.Skin.Chrome.ShowIcons)
        {
            name = "√" + name;
        }

        var typeface = entry.IsContainer ? b.BoldTypeface : b.Typeface;
        DrawText(context, b, name, foreground, x, y, layout.SizeX - x - 6, typeface);

        if (layout.SizeWidth > 0)
        {
            DrawText(context, b, SizeText(tab, entry), foreground, layout.SizeX, y, layout.SizeWidth, b.Typeface, TextAlignment.Right);
        }

        if (layout.DateWidth > 0)
        {
            var info = entry.Kind is VfsEntryKind.File or VfsEntryKind.Directory && entry.LastWriteTime != default
                ? entry.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Culture)
                : entry.Description ?? string.Empty;
            DrawText(context, b, info, foreground, layout.DateX, y, layout.DateWidth, b.Typeface);
        }
    }

    private static string SizeText(PanelTab tab, VfsEntry entry)
    {
        if (entry.Kind == VfsEntryKind.ParentLink)
        {
            return "<W GÓRĘ>";
        }

        if (entry.IsContainer)
        {
            if (tab.DirectorySizes.TryGetValue(entry.Path, out var size))
            {
                return (tab.SizesInProgress.Contains(entry.Path) ? "…" : string.Empty) + FormatSize(size);
            }

            return entry.Kind switch
            {
                VfsEntryKind.Drive when entry.Size > 0 => FormatSize(entry.Size),
                VfsEntryKind.Host => "<HOST>",
                VfsEntryKind.Share => "<UDZIAŁ>",
                VfsEntryKind.RegistryKey => "<KLUCZ>",
                _ => "<KATALOG>",
            };
        }

        return entry.Kind == VfsEntryKind.Action ? string.Empty : FormatSize(entry.Size);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return string.Empty;
        }

        if (bytes < 1024)
        {
            return bytes.ToString("N0", Culture);
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value.ToString(value < 10 ? "0.0" : "0", Culture) + " " + units[unit];
    }

    private static IBrush ForegroundFor(SkinBrushes b, VfsEntry entry)
    {
        if ((entry.Flags & (VfsEntryFlags.Hidden | VfsEntryFlags.System)) != 0)
        {
            return b.HiddenForeground;
        }

        return entry.Kind switch
        {
            VfsEntryKind.Directory or VfsEntryKind.ParentLink or VfsEntryKind.Drive or VfsEntryKind.RegistryKey => b.DirectoryForeground,
            VfsEntryKind.Host or VfsEntryKind.Share or VfsEntryKind.Action => b.NetworkForeground,
            VfsEntryKind.Archive => b.ArchiveForeground,
            VfsEntryKind.File => Path.GetExtension(entry.Name).ToLowerInvariant() switch
            {
                ".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".msi" => b.ExecutableForeground,
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".xz" or ".bz2" or ".zst" => b.ArchiveForeground,
                _ => b.FileForeground,
            },
            _ => b.FileForeground,
        };
    }

    private static void DrawIcon(DrawingContext context, IBrush brush, VfsEntry entry, double x, double y, double rowHeight)
    {
        var size = Math.Round(rowHeight * 0.55);
        var top = y + ((rowHeight - size) / 2);
        var pen = new Pen(brush, 1);
        if (entry.IsContainer)
        {
            // Folder: tab + body.
            context.FillRectangle(brush, new Rect(x, top, size * 0.45, size * 0.25));
            context.DrawRectangle(brush, null, new Rect(x, top + (size * 0.18), size * 1.1, size * 0.82), 1.5, 1.5);
        }
        else
        {
            context.DrawRectangle(null, pen, new Rect(x + 1.5, top, size * 0.8, size), 1, 1);
        }
    }

    private static void DrawSkeleton(DrawingContext context, SkinBrushes b, Rect rows, int skipRows)
    {
        for (var i = skipRows; i < skipRows + 6; i++)
        {
            var y = rows.Y + (i * b.RowHeight) + (b.RowHeight * 0.3);
            var width = rows.Width * (0.35 + (0.08 * ((i * 7) % 5)));
            context.FillRectangle(b.Skeleton, new Rect(rows.X + 8, y, width, b.RowHeight * 0.4), 3);
        }
    }

    private void DrawLoadingBar(DrawingContext context, SkinBrushes b, CanvasLayout layout, PanelTab tab)
    {
        var bar = new Rect(layout.Inner.X, layout.RowsArea.Bottom, layout.Inner.Width, b.RowHeight);
        context.FillRectangle(b.StatusBackground, bar);
        var count = Math.Max(0, tab.Entries.Count - (tab.Entries.Count > 0 && tab.Entries[0].Kind == VfsEntryKind.ParentLink ? 1 : 0));
        var text = $" {SpinnerFrames[_spinnerFrame]}  Wczytywanie… {count:N0}   [Esc – Anuluj]";
        DrawText(context, b, text, b.StatusForeground, bar.X + 4, bar.Y, bar.Width - 8, b.Typeface);
    }

    private static void DrawScrollbar(DrawingContext context, SkinBrushes b, Rect rows, PanelTab tab, int visible)
    {
        var total = tab.Entries.Count;
        if (total <= visible || rows.Height <= 0)
        {
            return;
        }

        var thumbHeight = Math.Max(16, rows.Height * visible / total);
        var thumbY = rows.Y + ((rows.Height - thumbHeight) * tab.TopIndex / Math.Max(1, total - visible));
        context.FillRectangle(b.Skeleton, new Rect(rows.Right - 5, thumbY, 4, thumbHeight), 2);
    }

    private static void DrawCentered(DrawingContext context, SkinBrushes b, Rect area, string text, IBrush brush)
    {
        var formatted = Format(b, "⚠ " + text, brush, b.Typeface, area.Width - 24);
        formatted.TextAlignment = TextAlignment.Center;
        context.DrawText(formatted, new Point(area.X + 12, area.Y + (area.Height / 2) - formatted.Height));
    }

    private static void DrawText(DrawingContext context, SkinBrushes b, string text, IBrush brush, double x, double y, double width,
        Typeface typeface, TextAlignment alignment = TextAlignment.Left)
    {
        if (width <= 4 || text.Length == 0)
        {
            return;
        }

        var formatted = Format(b, text, brush, typeface, width);
        formatted.TextAlignment = alignment;
        context.DrawText(formatted, new Point(x, y + ((b.RowHeight - formatted.Height) / 2)));
    }

    private static FormattedText Format(SkinBrushes b, string text, IBrush brush, Typeface typeface, double width) =>
        new(text, Culture, FlowDirection.LeftToRight, typeface, b.FontSize, brush)
        {
            MaxTextWidth = Math.Max(1, width),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private readonly record struct CanvasLayout(
        Rect Inner,
        Rect HeaderArea,
        Rect RowsArea,
        double SizeX,
        double SizeWidth,
        double DateX,
        double DateWidth);
}
