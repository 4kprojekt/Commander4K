using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.PluginSdk;
using ColumnDefinition = Commander4K.PluginSdk.ColumnDefinition;

namespace Commander4K.App.PluginPanels;

/// <summary>A row of the flattened tree as displayed (after filtering, sorting and expand/collapse).</summary>
public sealed record VisibleRow(PanelRow Row, int Depth, bool HasChildren, bool Expanded);

/// <summary>Owner-drawn table/tree for plugin panels, painted from the active skin like the file list.</summary>
public sealed class PluginTableCanvas : Control
{
    private const double Indent = 16;
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private IReadOnlyList<ColumnDefinition> _columns = [];
    private IReadOnlyList<VisibleRow> _rows = [];

    public PluginTableCanvas()
    {
        ClipToBounds = true;
    }

    public event Action? Activated;

    public event Action<int>? RowClicked;

    public event Action<int>? RowDoubleClicked;

    public event Action<int>? ExpanderClicked;

    public event Action<int>? HeaderClicked;

    public SkinBrushes? Brushes { get; set; }

    public bool IsActivePanel { get; set; }

    public int CursorIndex { get; set; }

    public int TopIndex { get; set; }

    public int SortColumn { get; set; } = -1;

    public bool SortDescending { get; set; }

    public HashSet<string> Marked { get; } = [];

    public int VisibleRowCount => Brushes is null ? 1 : Math.Max(1, (int)((Bounds.Height - Brushes.RowHeight) / Brushes.RowHeight));

    public void SetContent(IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<VisibleRow> rows)
    {
        _columns = columns;
        _rows = rows;
        CursorIndex = Math.Clamp(CursorIndex, 0, Math.Max(0, rows.Count - 1));
        EnsureCursorVisible();
    }

    public void EnsureCursorVisible()
    {
        var visible = VisibleRowCount;
        if (CursorIndex < TopIndex)
        {
            TopIndex = CursorIndex;
        }
        else if (CursorIndex >= TopIndex + visible)
        {
            TopIndex = CursorIndex - visible + 1;
        }

        TopIndex = Math.Clamp(TopIndex, 0, Math.Max(0, _rows.Count - visible));
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Brushes is not { } b)
        {
            return;
        }

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(b.PanelBackground, bounds);
        var widths = ColumnWidths(bounds.Width - 12);

        // Header.
        var header = new Rect(0, 0, bounds.Width, b.RowHeight);
        context.FillRectangle(b.HeaderBackground, header);
        var x = 6.0;
        for (var c = 0; c < _columns.Count; c++)
        {
            var title = _columns[c].Header + (c == SortColumn ? (SortDescending ? " ▼" : " ▲") : "");
            DrawText(context, b, title, b.HeaderForeground, x, 0, widths[c] - 8, b.BoldTypeface, Align(_columns[c]));
            x += widths[c];
        }

        context.DrawLine(b.BorderPen, header.BottomLeft, header.BottomRight);

        using (context.PushClip(new Rect(0, b.RowHeight, bounds.Width, bounds.Height - b.RowHeight)))
        {
            var visible = (int)Math.Ceiling((bounds.Height - b.RowHeight) / b.RowHeight);
            for (var i = TopIndex; i < Math.Min(_rows.Count, TopIndex + visible); i++)
            {
                DrawRow(context, b, _rows[i], i, b.RowHeight * (i - TopIndex + 1), bounds.Width, widths);
            }
        }

        if (_rows.Count > 0 && _rows.Count > VisibleRowCount)
        {
            var area = bounds.Height - b.RowHeight;
            var thumb = Math.Max(16, area * VisibleRowCount / _rows.Count);
            var y = b.RowHeight + ((area - thumb) * TopIndex / Math.Max(1, _rows.Count - VisibleRowCount));
            context.FillRectangle(b.Skeleton, new Rect(bounds.Width - 5, y, 4, thumb), 2);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Activated?.Invoke();
        if (Brushes is not { } b)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (point.Y < b.RowHeight)
        {
            var widths = ColumnWidths(Bounds.Width - 12);
            var x = 6.0;
            for (var c = 0; c < widths.Length; c++)
            {
                if (point.X < x + widths[c])
                {
                    HeaderClicked?.Invoke(c);
                    break;
                }

                x += widths[c];
            }

            return;
        }

        var index = TopIndex + (int)((point.Y - b.RowHeight) / b.RowHeight);
        if (index >= _rows.Count)
        {
            return;
        }

        var row = _rows[index];
        var expanderX = 6 + (row.Depth * Indent);
        if (row.HasChildren && point.X >= expanderX && point.X < expanderX + Indent)
        {
            ExpanderClicked?.Invoke(index);
        }
        else if (e.ClickCount == 2)
        {
            RowDoubleClicked?.Invoke(index);
        }
        else
        {
            RowClicked?.Invoke(index);
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        TopIndex = Math.Clamp(TopIndex - (int)(e.Delta.Y * 3), 0, Math.Max(0, _rows.Count - VisibleRowCount));
        InvalidateVisual();
        e.Handled = true;
    }

    private void DrawRow(DrawingContext context, SkinBrushes b, VisibleRow visible, int index, double y, double width, double[] widths)
    {
        // Modern skins: rows inset 4 px with rounded corners, warning/error rows tinted (design system severities).
        var rowRect = b.IsModern ? new Rect(4, y + 1, Math.Max(0, width - 8), b.RowHeight - 2) : new Rect(0, y, width, b.RowHeight);
        var radius = b.IsModern ? 3f : 0f;
        var isCursor = index == CursorIndex;
        var isMarked = Marked.Contains(visible.Row.Id);
        if (isCursor)
        {
            context.FillRectangle(IsActivePanel ? b.CursorBackground : b.InactiveCursorBackground, rowRect, radius);
        }
        else if (b.IsModern && visible.Row.Severity is RowSeverity.Warning or RowSeverity.Error)
        {
            context.FillRectangle(visible.Row.Severity == RowSeverity.Warning ? b.WarningBackground : b.ErrorBackground, rowRect, radius);
        }

        var foreground = isCursor && IsActivePanel && !isMarked ? b.CursorForeground
            : isMarked ? b.SelectedForeground
            : visible.Row.Severity switch
            {
                RowSeverity.Muted => b.HiddenForeground,
                RowSeverity.Highlight => b.DirectoryForeground,
                RowSeverity.Warning => b.IsModern ? b.Warning : b.ArchiveForeground,
                RowSeverity.Error => b.Error,
                _ => b.FileForeground,
            };
        var typeface = visible.Row.Severity == RowSeverity.Highlight || visible.HasChildren ? b.BoldTypeface : b.Typeface;

        var x = 6.0;
        for (var c = 0; c < _columns.Count && c < visible.Row.Cells.Count; c++)
        {
            var cellX = x;
            var cellWidth = widths[c] - 8;
            if (c == 0)
            {
                var indent = visible.Depth * Indent;
                if (visible.HasChildren)
                {
                    DrawText(context, b, visible.Expanded ? "▾" : "▸", foreground, cellX + indent, y, Indent, b.Typeface, TextAlignment.Left);
                }

                cellX += indent + Indent;
                cellWidth -= indent + Indent;
            }

            DrawText(context, b, visible.Row.Cells[c], foreground, cellX, y, cellWidth, typeface, Align(_columns[c]));
            x += widths[c];
        }
    }

    /// <summary>Default widths, scaled down to fit; the first column absorbs leftover space.</summary>
    private double[] ColumnWidths(double available)
    {
        if (_columns.Count == 0)
        {
            return [];
        }

        var widths = _columns.Select(c => c.DefaultWidth).ToArray();
        var total = widths.Sum();
        if (total > available)
        {
            var scale = available / total;
            for (var i = 0; i < widths.Length; i++)
            {
                widths[i] *= scale;
            }
        }
        else
        {
            widths[0] += available - total;
        }

        return widths;
    }

    private static TextAlignment Align(ColumnDefinition column) => column.Alignment switch
    {
        ColumnAlignment.Right => TextAlignment.Right,
        ColumnAlignment.Center => TextAlignment.Center,
        _ => TextAlignment.Left,
    };

    private static void DrawText(DrawingContext context, SkinBrushes b, string text, IBrush brush, double x, double y, double width, Typeface typeface, TextAlignment alignment)
    {
        if (width <= 4 || string.IsNullOrEmpty(text))
        {
            return;
        }

        var line = text.AsSpan().IndexOfAny('\r', '\n') is var cut and >= 0 ? text[..cut] + " …" : text;
        var formatted = new FormattedText(line, Culture, FlowDirection.LeftToRight, typeface, b.FontSize, brush)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };
        context.DrawText(formatted, new Point(x, y + ((b.RowHeight - formatted.Height) / 2)));
    }
}
