using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Security;
using Commander4K.Core.Theming;
using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

/// <summary>
/// Owner-drawn file list. Instead of one control per cell it paints only the rows inside the viewport straight onto
/// the <see cref="DrawingContext"/> (Skia), so a 500 000-entry directory costs the same per frame as a 50-entry one.
/// </summary>
public sealed class FileViewportCanvas : Control
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;
    private static readonly Dictionary<ShapedKey, FormattedText> ShapedCache = [];

    private readonly DispatcherTimer _spinner;
    private PanelTab? _tab;
    private SkinBrushes? _brushes;
    private bool _isActive;
    private ColumnDrag? _drag;
    private string? _hoverDivider;
    private string? _sizeTipPath;

    /// <summary>Keys of the built-in columns in <see cref="ColumnWidths"/>; plugin columns use <see cref="ActiveColumn.Key"/>.</summary>
    public const string SizeColumnKey = "size";

    public const string DateColumnKey = "date";

    private const double MinColumnWidth = 32;
    private const double MaxColumnWidth = 480;
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    public FileViewportCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        // Animates the wave dots (loading bar, folder sizing, and row operations); runs only while something is busy.
        _spinner = new DispatcherTimer(WaveDots.FrameInterval, DispatcherPriority.Render, (_, _) => InvalidateVisual());
    }

    /// <summary>Raised when the user clicks into the panel (it becomes the active one).</summary>
    public event Action? Activated;

    /// <summary>Raised on double-click of a row.</summary>
    public event Action<VfsEntry>? RowActivated;

    public event Action<SortColumn>? HeaderClicked;

    /// <summary>Right click on a row (the cursor has moved there unless the row is part of the marked set).</summary>
    public event Action<VfsEntry>? RightClicked;

    /// <summary>
    /// Column key → width dragged by the user (at the 13 px font size), shared with the settings; <c>null</c> = fixed
    /// widths. The canvas writes into it while dragging a header divider.
    /// </summary>
    public IDictionary<string, double>? ColumnWidths { get; set; }

    /// <summary>Raised when a drag of a header divider ends, or a double-click on one restores the default width.</summary>
    public event Action? ColumnWidthsChanged;

    /// <summary>Screen position just below the cursor row (for the context menu opened from the keyboard).</summary>
    public PixelPoint? CursorRowScreenPoint()
    {
        if (_tab is null || _brushes is null)
        {
            return null;
        }

        var layout = Layout();
        var y = layout.RowsArea.Y + ((_tab.Cursor - _tab.TopIndex + 1) * _brushes.RowHeight);
        return this.PointToScreen(new Point(layout.Inner.X + 40, Math.Min(y, layout.RowsArea.Bottom)));
    }

    public PanelTab? Tab
    {
        get => _tab;
        set
        {
            if (_tab is not null)
            {
                _tab.Changed -= OnTabChanged;
                _tab.SizesUpdated -= InvalidateVisual;
                _tab.ColumnData.Updated -= InvalidateVisual;
            }

            _tab = value;
            if (_tab is not null)
            {
                _tab.Changed += OnTabChanged;
                _tab.SizesUpdated += InvalidateVisual;
                _tab.ColumnData.Updated += InvalidateVisual;
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
            DrawHeader(context, b, layout, tab?.Sort ?? SortOrder.Default, _drag?.Key ?? _hoverDivider);
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
        if (DividerAt(layout, point) is { } divider && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                // Double-click on the edge, as in Explorer: back to the default width.
                _drag = null;
                ColumnWidths!.Remove(divider.Key);
                ColumnWidthsChanged?.Invoke();
                InvalidateVisual();
            }
            else
            {
                // The name keeps at least as much room as plugin columns need to stay visible.
                var spare = Math.Max(0, layout.NameRight - layout.Inner.X - (140 * layout.Scale));
                _drag = new ColumnDrag(divider.Key, point.X, divider.Width, divider.Width + spare, layout.Scale);
                e.Pointer.Capture(this);
            }

            e.Handled = true;
            return;
        }

        if (layout.HeaderArea.Contains(point))
        {
            if (point.X < layout.NameRight || point.X >= layout.SizeX)
            {
                HeaderClicked?.Invoke(point.X >= layout.DateX ? SortColumn.Date : point.X >= layout.SizeX ? SortColumn.Size : SortColumn.Name);
            }

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
            // The panel decides: Windows context menu (default) or marking, as in Total Commander.
            var clicked = _tab.Entries[index];
            if (!_tab.Marked.Contains(clicked.Path))
            {
                _tab.MoveCursor(index);
            }

            e.Handled = true;
            Dispatcher.UIThread.Post(() => RightClicked?.Invoke(clicked)); // After the press, so no pointer capture is held.
            return;
        }

        _tab.MoveCursor(index);
        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            RowActivated?.Invoke(_tab.Entries[index]);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag is { } drag && ColumnWidths is { } widths)
        {
            // Columns are anchored on the right, so dragging their left edge to the left widens them.
            var width = Math.Clamp(drag.StartWidth + (drag.StartX - point.X), MinColumnWidth * drag.Scale, Math.Max(drag.StartWidth, drag.MaxWidth));
            widths[drag.Key] = Math.Round(Math.Clamp(width / drag.Scale, MinColumnWidth, MaxColumnWidth));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var layout = _brushes is null ? (CanvasLayout?)null : Layout();
        var hover = layout is { } current ? DividerAt(current, point)?.Key : null;
        if (hover != _hoverDivider)
        {
            _hoverDivider = hover;
            Cursor = hover is null ? null : ResizeCursor;
            InvalidateVisual();
        }

        UpdateSizeTip(layout is { } l && hover is null ? IncompleteSizeAt(l, point) : null);
    }

    /// <summary>The folder whose size cell is under <paramref name="point"/>, when its size skipped unreadable subfolders.</summary>
    private string? IncompleteSizeAt(CanvasLayout layout, Point point)
    {
        if (_tab is null || _brushes is null || layout.SizeWidth <= 0 || !layout.RowsArea.Contains(point)
            || point.X < layout.SizeX || point.X >= layout.SizeX + layout.SizeWidth)
        {
            return null;
        }

        var index = _tab.TopIndex + (int)((point.Y - layout.RowsArea.Y) / _brushes.RowHeight);
        return index < _tab.Entries.Count && _tab.IncompleteSizes.ContainsKey(_tab.Entries[index].Path) ? _tab.Entries[index].Path : null;
    }

    /// <summary>The "!" before an incomplete folder size explains itself in a tooltip while the pointer is on that cell.</summary>
    private void UpdateSizeTip(string? path)
    {
        if (path == _sizeTipPath)
        {
            return;
        }

        _sizeTipPath = path;
        if (path is null || _tab is null || !_tab.IncompleteSizes.TryGetValue(path, out var inaccessible))
        {
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
            return;
        }

        var folders = Loc.P(inaccessible, "{0} podfolderu|{0} podfolderów|{0} podfolderów");
        ToolTip.SetTip(this, ProcessElevation.IsAdministrator
            ? Loc.T("Rozmiar może być niedokładny: brak dostępu do {0}, nawet jako administrator.", folders)
            : Loc.T("Rozmiar może być niedokładny: brak uprawnień do {0}. Spróbuj uruchomić Commander4K jako administrator.", folders));
        ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            e.Pointer.Capture(null);
            EndDrag();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag is null && _hoverDivider is not null)
        {
            _hoverDivider = null;
            Cursor = null;
            InvalidateVisual();
        }

        UpdateSizeTip(null);
    }

    private void EndDrag()
    {
        if (_drag is null)
        {
            return;
        }

        _drag = null;
        ColumnWidthsChanged?.Invoke();
        InvalidateVisual();
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
        if (_sizeTipPath is not null && _tab?.IncompleteSizes.ContainsKey(_sizeTipPath) != true)
        {
            UpdateSizeTip(null);
        }

        if (_tab is not null && Bounds.Height > 0)
        {
            EnsureCursorVisible();
        }

        InvalidateVisual();
    }

    private void UpdateSpinner()
    {
        if (_tab is { IsLoading: true } || _tab?.SizesInProgress.Count > 0 || _tab?.BusyEntries.Count > 0)
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

        // Modern skins: the panel card (FilePanelView) draws the border, rows are inset; retro skins draw their frames here.
        var inset = b.IsModern ? 0 : b.Skin.Chrome.BorderStyle switch
        {
            PanelBorderStyle.Double => 6,
            PanelBorderStyle.Single => 1,
            _ => 0,
        };
        var inner = new Rect(Bounds.Size).Deflate(inset);
        var headerHeight = !b.Skin.Chrome.ShowColumnHeaders ? 0 : b.IsModern ? Math.Round(b.FontSize * 2) : b.RowHeight;
        var header = new Rect(inner.X, inner.Y, inner.Width, headerHeight);
        var footer = 0;
        var top = b.IsModern ? 4 : 0; // Design system: 4 px above the first row.
        var rows = new Rect(inner.X, inner.Y + headerHeight + top, inner.Width, Math.Max(0, inner.Height - headerHeight - footer - top));

        // Size and date columns (design system: 84 px and 124 px at 13 px), 8 px apart, 13 px from the right edge.
        var scale = b.FontSize / 13.0;
        var gap = b.IsModern ? 8 * scale : 6;
        var right = inner.Right - (b.IsModern ? 13 : 6);
        var dateWidth = inner.Width > 420 ? CustomWidth(DateColumnKey, scale) ?? (b.IsModern ? 124 * scale : b.FontSize * 0.6 * 17) : 0;
        var sizeWidth = inner.Width > 260 ? CustomWidth(SizeColumnKey, scale) ?? (b.IsModern ? 84 * scale : b.FontSize * 0.6 * 11) : 0;
        var dateX = right - dateWidth;
        var sizeX = dateX - sizeWidth - gap;

        // Plugin columns sit between name and size, in settings order, as long as the name keeps ~140 px.
        var room = sizeX - inner.X - 140 * scale;
        var extras = new List<ExtraColumnLayout>();
        var nameRight = sizeX - gap;
        foreach (var column in _tab?.ColumnData.Columns ?? [])
        {
            var width = CustomWidth(column.Key, scale) ?? column.Definition.DefaultWidth * scale;
            if (room - width - gap < 0)
            {
                break;
            }

            room -= width + gap;
            extras.Add(new ExtraColumnLayout(column, 0, width));
        }

        for (var i = extras.Count - 1; i >= 0; i--)
        {
            extras[i] = extras[i] with { X = nameRight - extras[i].Width };
            nameRight -= extras[i].Width + gap;
        }

        return new CanvasLayout(inner, header, rows, sizeX, sizeWidth, dateX, dateWidth, extras, nameRight, gap, scale);
    }

    /// <summary>A width the user dragged, stored at the 13 px font size so it follows the density setting.</summary>
    private double? CustomWidth(string key, double scale) =>
        ColumnWidths is { } widths && widths.TryGetValue(key, out var width) && width > 0
            ? Math.Clamp(width, MinColumnWidth, MaxColumnWidth) * scale
            : null;

    private static IEnumerable<ColumnDivider> Dividers(CanvasLayout layout)
    {
        foreach (var extra in layout.Extras)
        {
            yield return new ColumnDivider(extra.Column.Key, extra.X, extra.Width);
        }

        if (layout.SizeWidth > 0)
        {
            yield return new ColumnDivider(SizeColumnKey, layout.SizeX, layout.SizeWidth);
        }

        if (layout.DateWidth > 0)
        {
            yield return new ColumnDivider(DateColumnKey, layout.DateX, layout.DateWidth);
        }
    }

    /// <summary>The divider under the pointer: within 4 px of the middle of the gap left of a column, in the header.</summary>
    private ColumnDivider? DividerAt(CanvasLayout layout, Point point)
    {
        if (ColumnWidths is null || !layout.HeaderArea.Contains(point))
        {
            return null;
        }

        foreach (var divider in Dividers(layout))
        {
            if (Math.Abs(point.X - (divider.X - (layout.Gap / 2))) <= 4)
            {
                return divider;
            }
        }

        return null;
    }

    private static void DrawFrame(DrawingContext context, SkinBrushes b, Rect bounds)
    {
        if (b.IsModern)
        {
            return; // The panel card around the list has the border.
        }

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

    private static void DrawHeader(DrawingContext context, SkinBrushes b, CanvasLayout layout, SortOrder sort, string? highlightedDivider)
    {
        context.FillRectangle(b.HeaderBackground, layout.HeaderArea);
        var y = layout.HeaderArea.Y;
        var height = layout.HeaderArea.Height;
        if (!b.IsModern)
        {
            var arrow = sort.Descending ? " ▼" : " ▲";
            DrawText(context, b, Loc.T("Nazwa") + (sort.Column is SortColumn.Name or SortColumn.Extension ? arrow : ""), b.HeaderForeground,
                layout.Inner.X + 6, y, layout.NameRight - layout.Inner.X - 12, b.BoldTypeface);
            foreach (var extra in layout.Extras)
            {
                DrawText(context, b, extra.Column.Definition.Header, b.HeaderForeground, extra.X, y, extra.Width, b.BoldTypeface,
                    Align(extra.Column.Definition.Alignment));
            }

            if (layout.SizeWidth > 0)
            {
                DrawText(context, b, Loc.T("Rozmiar") + (sort.Column == SortColumn.Size ? arrow : ""), b.HeaderForeground,
                    layout.SizeX, y, layout.SizeWidth, b.BoldTypeface, TextAlignment.Right);
            }

            if (layout.DateWidth > 0)
            {
                DrawText(context, b, Loc.T("Data") + (sort.Column == SortColumn.Date ? arrow : ""), b.HeaderForeground,
                    layout.DateX, y, layout.DateWidth, b.BoldTypeface);
            }
        }
        else
        {
            // Design system: 11 px semibold capitals, the name aligned with the text of the rows, an accent sort chevron.
            var size = Math.Round(b.FontSize * 11 / 13);
            var nameX = layout.Inner.X + RowTextOffset(b);
            void Column(string title, double x, double width, bool sorted, TextAlignment alignment)
            {
                var text = title.ToUpper(Loc.Culture);
                var shown = DrawText(context, b, text, b.HeaderForeground, x, y, width - (sorted ? 14 : 0), b.BoldTypeface, alignment, height, size);
                if (sorted)
                {
                    var chevronX = alignment == TextAlignment.Right ? x + width - shown - 12 : x + shown + 4;
                    DrawChevron(context, b.Accent, alignment == TextAlignment.Right ? x + width - 10 : chevronX, y + (height / 2), sort.Descending);
                }
            }

            Column(Loc.T("Nazwa"), nameX, layout.NameRight - nameX, sort.Column is SortColumn.Name or SortColumn.Extension, TextAlignment.Left);
            foreach (var extra in layout.Extras)
            {
                Column(extra.Column.Definition.Header, extra.X, extra.Width, false, Align(extra.Column.Definition.Alignment));
            }

            if (layout.SizeWidth > 0)
            {
                Column(Loc.T("Rozmiar"), layout.SizeX, layout.SizeWidth, sort.Column == SortColumn.Size, TextAlignment.Right);
            }

            if (layout.DateWidth > 0)
            {
                Column(Loc.T("Data"), layout.DateX, layout.DateWidth, sort.Column == SortColumn.Date, TextAlignment.Left);
            }
        }

        context.DrawLine(b.BorderPen, layout.HeaderArea.BottomLeft + new Vector(0, -0.5), layout.HeaderArea.BottomRight + new Vector(0, -0.5));
        foreach (var divider in Dividers(layout))
        {
            if (divider.Key == highlightedDivider)
            {
                var x = Math.Round(divider.X - (layout.Gap / 2)) + 0.5;
                context.DrawLine(new Pen(b.Accent, 2), new Point(x, layout.HeaderArea.Y + 3), new Point(x, layout.HeaderArea.Bottom - 3));
            }
        }
    }

    /// <summary>Row inset + padding + icon + gap: where names start (36 px in the design system at 13 px).</summary>
    private static double RowTextOffset(SkinBrushes b) =>
        b.IsModern ? 4 + 8 + IconSize(b) + 8 : 6 + (b.Skin.Chrome.ShowIcons ? b.RowHeight : 0);

    private static double IconSize(SkinBrushes b) => Math.Round(b.FontSize * 16 / 13);

    private static void DrawChevron(DrawingContext context, IBrush brush, double x, double centerY, bool down)
    {
        var pen = new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geometry = new PolylineGeometry(down
            ? [new Point(x, centerY - 1.5), new Point(x + 2.5, centerY + 1), new Point(x + 5, centerY - 1.5)]
            : [new Point(x, centerY + 1.5), new Point(x + 2.5, centerY - 1), new Point(x + 5, centerY + 1.5)], false);
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawRow(DrawingContext context, SkinBrushes b, CanvasLayout layout, PanelTab tab, VfsEntry entry, int index, double y)
    {
        if (b.IsModern)
        {
            DrawModernRow(context, b, layout, tab, entry, index, y);
            return;
        }

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
        var busy = tab.BusyEntries.Contains(entry.Path);
        var dotSize = DotSize(b);
        var nameWidth = layout.NameRight - x - 6 - (busy ? WaveDots.WidthFor(dotSize) + 8 : 0);
        var nameDrawn = DrawText(context, b, name, foreground, x, y, nameWidth, typeface);
        if (busy)
        {
            WaveDots.Draw(context, foreground, x + nameDrawn + 4, y + (b.RowHeight / 2), dotSize);
        }
        foreach (var extra in layout.Extras)
        {
            if (tab.ColumnData.Get(extra.Column, entry) is { Text.Length: > 0 } value)
            {
                DrawText(context, b, value.Text, value.Text.StartsWith('⚠') ? b.Error : foreground, extra.X, y, extra.Width, b.Typeface,
                    Align(extra.Column.Definition.Alignment));
            }
        }

        if (layout.SizeWidth > 0)
        {
            DrawSize(context, b, layout, tab, entry, foreground, y);
        }

        if (layout.DateWidth > 0)
        {
            var info = entry.Kind is VfsEntryKind.File or VfsEntryKind.Directory && entry.LastWriteTime != default
                ? entry.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Culture)
                : entry.Description ?? string.Empty;
            DrawText(context, b, info, foreground, layout.DateX, y, layout.DateWidth, b.Typeface);
        }
    }

    /// <summary>
    /// Design-system row: inset 4 px, 3 px corners; active cursor filled, inactive cursor outlined; marked rows get
    /// their own background and a 3 px edge; type icon; sizes and dates in the muted colour; folders semibold.
    /// </summary>
    private void DrawModernRow(DrawingContext context, SkinBrushes b, CanvasLayout layout, PanelTab tab, VfsEntry entry, int index, double y)
    {
        var rowRect = new Rect(layout.Inner.X + 4, y, layout.Inner.Width - 8, b.RowHeight);
        var isCursor = index == tab.Cursor;
        var isMarked = tab.Marked.Contains(entry.Path);
        var activeCursor = isCursor && _isActive;
        if (activeCursor)
        {
            context.FillRectangle(b.CursorBackground, rowRect, 3);
        }
        else if (isCursor)
        {
            context.DrawRectangle(b.InactiveCursorBackground, b.InactiveCursorPen, rowRect.Deflate(0.5), 3, 3);
        }
        else if (isMarked && b.SelectedBackground is { } markedBackground)
        {
            context.FillRectangle(markedBackground, rowRect, 3);
        }

        if (isMarked)
        {
            context.FillRectangle(b.SelectedForeground, new Rect(rowRect.X - 1, y + 4, 3, b.RowHeight - 8), 1.5f);
        }

        var hidden = (entry.Flags & (VfsEntryFlags.Hidden | VfsEntryFlags.System)) != 0;
        var foreground = activeCursor ? (isMarked ? b.CursorSelectedForeground : b.CursorForeground)
            : isMarked ? b.SelectedForeground
            : ForegroundFor(b, entry);
        IBrush meta = activeCursor ? foreground : isMarked ? b.SelectedForeground : b.MutedForeground;

        var x = rowRect.X + 8;
        if (b.Skin.Chrome.ShowIcons)
        {
            var size = IconSize(b);
            FileIcons.Draw(context, FileIcons.For(entry), foreground, x, y + ((b.RowHeight - size) / 2), size, hidden ? 0.6 : 1);
            x += size + 8;
        }

        var name = b.Skin.Chrome.UppercaseNames ? entry.Name.ToUpperInvariant() : entry.Name;
        var bold = entry.IsContainer || isMarked;
        var busy = tab.BusyEntries.Contains(entry.Path);
        var dotSize = DotSize(b);
        var nameWidth = layout.NameRight - x - (busy ? WaveDots.WidthFor(dotSize) + 8 : 0);
        var nameDrawn = DrawText(context, b, name, foreground, x, y, nameWidth, bold ? b.BoldTypeface : b.Typeface);
        if (busy)
        {
            WaveDots.Draw(context, foreground, x + nameDrawn + 4, y + (b.RowHeight / 2), dotSize);
        }
        foreach (var extra in layout.Extras)
        {
            if (tab.ColumnData.Get(extra.Column, entry) is not { Text.Length: > 0 } value)
            {
                continue;
            }

            if (extra.Column.Plugin.Id == "builtin.columns.git")
            {
                // Git: a status dot plus the word, coloured by meaning (changes amber, new green, conflicts red).
                var text = value.Text.TrimStart('●', ' ');
                IBrush color = activeCursor ? foreground
                    : text == Loc.T("zmieniony") || text == Loc.T("w indeksie") ? b.Warning
                    : text == Loc.T("nowy") || text == Loc.T("dodany") ? b.Success
                    : text == Loc.T("konflikt") || text == Loc.T("usunięty") ? b.Error
                    : b.MutedForeground;
                context.FillRectangle(color, new Rect(extra.X, y + (b.RowHeight / 2) - 3, 6, 6), 3);
                DrawText(context, b, text, color, extra.X + 12, y, extra.Width - 12, b.Typeface);
                continue;
            }

            DrawText(context, b, value.Text, value.Text.StartsWith('⚠') ? b.Error : meta, extra.X, y, extra.Width, b.Typeface,
                Align(extra.Column.Definition.Alignment));
        }

        if (layout.SizeWidth > 0)
        {
            DrawSize(context, b, layout, tab, entry, meta, y);
        }

        if (layout.DateWidth > 0)
        {
            var info = entry.Kind is VfsEntryKind.File or VfsEntryKind.Directory && entry.LastWriteTime != default
                ? entry.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Culture)
                : entry.Description ?? string.Empty;
            DrawText(context, b, info, meta, layout.DateX, y, layout.DateWidth, b.Typeface);
        }
    }

    /// <summary>
    /// Size column; a folder still being sized shows the wave dots before its running total, and a "!" when some
    /// subfolders could not be read (the total is then too small; the tooltip says why).
    /// </summary>
    private static void DrawSize(DrawingContext context, SkinBrushes b, CanvasLayout layout, PanelTab tab, VfsEntry entry, IBrush brush, double y)
    {
        var shown = DrawText(context, b, SizeText(tab, entry), brush, layout.SizeX, y, layout.SizeWidth, b.Typeface, TextAlignment.Right);
        if (entry.IsContainer && tab.IncompleteSizes.ContainsKey(entry.Path))
        {
            var right = layout.SizeX + layout.SizeWidth - shown - (shown > 0 ? b.FontSize * 0.4 : 0);
            shown += layout.SizeX + layout.SizeWidth - right
                + DrawText(context, b, "!", b.Warning, layout.SizeX, y, right - layout.SizeX, b.BoldTypeface, TextAlignment.Right);
        }

        if (entry.IsContainer && tab.SizesInProgress.Contains(entry.Path))
        {
            var dot = DotSize(b);
            var x = layout.SizeX + layout.SizeWidth - shown - (shown > 0 ? dot * 1.5 : 0) - WaveDots.WidthFor(dot);
            WaveDots.Draw(context, brush, Math.Max(layout.SizeX, x), y + (b.RowHeight / 2), dot);
        }
    }

    private static double DotSize(SkinBrushes b) => Math.Max(3, Math.Round(b.FontSize * 0.3));

    private static string SizeText(PanelTab tab, VfsEntry entry)
    {
        if (entry.Kind == VfsEntryKind.ParentLink)
        {
            return Loc.T("<W GÓRĘ>");
        }

        if (entry.IsContainer)
        {
            if (tab.DirectorySizes.TryGetValue(entry.Path, out var size))
            {
                return FormatSize(size);
            }

            if (tab.SizesInProgress.Contains(entry.Path))
            {
                return string.Empty; // Only the dots until the first total arrives.
            }

            return entry.Kind switch
            {
                VfsEntryKind.Drive when entry.Size > 0 => FormatSize(entry.Size),
                VfsEntryKind.Host => Loc.T("<HOST>"),
                VfsEntryKind.Share => Loc.T("<UDZIAŁ>"),
                VfsEntryKind.RegistryKey => Loc.T("<KLUCZ>"),
                _ => Loc.T("<KATALOG>"),
            };
        }

        return entry.Kind == VfsEntryKind.Action ? string.Empty
            : FileIcons.IsFolderConfig(entry) ? Loc.T("<WIDOK>")
            : FormatSize(entry.Size);
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

        if (FileIcons.IsFolderConfig(entry))
        {
            return b.Accent;
        }

        return entry.Kind switch
        {
            VfsEntryKind.Directory or VfsEntryKind.ParentLink or VfsEntryKind.Drive or VfsEntryKind.RegistryKey => b.DirectoryForeground,
            VfsEntryKind.Host or VfsEntryKind.Share or VfsEntryKind.Action => b.NetworkForeground,
            VfsEntryKind.Archive => b.ArchiveForeground,
            VfsEntryKind.File => FileIcons.IsExecutable(entry.Name) ? b.ExecutableForeground
                : FileIcons.IsArchive(entry.Name) ? b.ArchiveForeground
                : b.FileForeground,
            _ => b.FileForeground,
        };
    }

    private static void DrawIcon(DrawingContext context, IBrush brush, VfsEntry entry, double x, double y, double rowHeight)
    {
        var size = Math.Round(rowHeight * 0.55);
        var top = y + ((rowHeight - size) / 2);
        var icon = FileIcons.For(entry);
        if (icon is FileIcons.Kind.Lan or FileIcons.Kind.Internet or FileIcons.Kind.Router or FileIcons.Kind.Nas or FileIcons.Kind.Web)
        {
            FileIcons.Draw(context, icon, brush, x, top, size);
            return;
        }

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

    /// <summary>Draws one line centred vertically in a row (or <paramref name="height"/>); returns the drawn width.</summary>
    private static double DrawText(DrawingContext context, SkinBrushes b, string text, IBrush brush, double x, double y, double width,
        Typeface typeface, TextAlignment alignment = TextAlignment.Left, double? height = null, double? fontSize = null)
    {
        if (width <= 4 || text.Length == 0)
        {
            return 0;
        }

        var formatted = Shaped(b, text, brush, typeface, width, alignment, fontSize);
        context.DrawText(formatted, new Point(x, Math.Round(y + (((height ?? b.RowHeight) - formatted.Height) / 2))));
        return Math.Min(width, formatted.WidthIncludingTrailingWhitespace);
    }

    /// <summary>
    /// Shaping text (HarfBuzz) is most of a frame's cost, and the list repaints 30× a second while folders are sized
    /// or a listing streams in; unchanged cells reuse their shaped line. UI thread only.
    /// </summary>
    private static FormattedText Shaped(SkinBrushes b, string text, IBrush brush, Typeface typeface, double width, TextAlignment alignment,
        double? fontSize)
    {
        var key = new ShapedKey(text, brush, typeface, Math.Round(width), fontSize ?? b.FontSize, alignment);
        if (ShapedCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (ShapedCache.Count >= 4096)
        {
            ShapedCache.Clear(); // Scrolling through a huge folder: start over rather than grow without bound.
        }

        var formatted = Format(b, text, brush, typeface, key.Width, key.FontSize);
        formatted.TextAlignment = alignment;
        ShapedCache[key] = formatted;
        return formatted;
    }

    private readonly record struct ShapedKey(string Text, IBrush Brush, Typeface Typeface, double Width, double FontSize, TextAlignment Alignment);

    private static FormattedText Format(SkinBrushes b, string text, IBrush brush, Typeface typeface, double width, double? fontSize = null) =>
        new(text, Culture, FlowDirection.LeftToRight, typeface, fontSize ?? b.FontSize, brush)
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
        double DateWidth,
        IReadOnlyList<ExtraColumnLayout> Extras,
        double NameRight,
        double Gap,
        double Scale);

    private readonly record struct ExtraColumnLayout(ActiveColumn Column, double X, double Width);

    /// <summary>A draggable column edge in the header: the left edge of a right-anchored column (the name fills the rest).</summary>
    private readonly record struct ColumnDivider(string Key, double X, double Width);

    private sealed record ColumnDrag(string Key, double StartX, double StartWidth, double MaxWidth, double Scale);

    private static TextAlignment Align(ColumnAlignment alignment) => alignment switch
    {
        ColumnAlignment.Right => TextAlignment.Right,
        ColumnAlignment.Center => TextAlignment.Center,
        _ => TextAlignment.Left,
    };
}
