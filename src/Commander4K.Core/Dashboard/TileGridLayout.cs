using Commander4K.Core.Folders;

namespace Commander4K.Core.Dashboard;

/// <summary>Where one tile sits on the dashboard grid (zero-based cells).</summary>
public readonly record struct TilePlacement(int Column, int Row, int ColumnSpan, int RowSpan);

/// <summary>
/// Places dashboard tiles of different sizes on a grid with a given number of columns. Tiles keep their order as far
/// as possible: each one takes the first free cell (top to bottom, left to right) at or after the previous tile's
/// row that fits it, so a small tile may fill the hole next to a tall one but never jumps above earlier rows.
/// Spans wider than the grid shrink to it, so a phone-narrow panel still shows every tile, one per row.
/// </summary>
public static class TileGridLayout
{
    /// <summary>Columns that fit <paramref name="width"/> with tiles at least <paramref name="unitWidth"/> wide.</summary>
    public static int ColumnsFor(double width, double unitWidth, double gap, int maxColumns = 8)
    {
        if (width <= 0 || unitWidth <= 0)
        {
            return 1;
        }

        return Math.Clamp((int)Math.Floor((width + gap) / (unitWidth + gap)), 1, Math.Max(1, maxColumns));
    }

    public static IReadOnlyList<TilePlacement> Place(IReadOnlyList<TileSize> sizes, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var placements = new TilePlacement[sizes.Count];
        var occupied = new List<bool[]>();
        var cursorRow = 0;
        for (var i = 0; i < sizes.Count; i++)
        {
            var (wantColumns, rows) = TileSizes.Span(sizes[i]);
            var span = wantColumns <= 0 ? columns : Math.Min(wantColumns, columns);
            var placed = false;
            for (var row = cursorRow; !placed; row++)
            {
                for (var column = 0; column + span <= columns; column++)
                {
                    if (!Fits(occupied, row, column, span, rows))
                    {
                        continue;
                    }

                    Mark(occupied, row, column, span, rows, columns);
                    placements[i] = new TilePlacement(column, row, span, rows);
                    cursorRow = row;
                    placed = true;
                    break;
                }
            }
        }

        return placements;
    }

    /// <summary>Rows the placements use.</summary>
    public static int RowCount(IEnumerable<TilePlacement> placements) =>
        placements.Select(p => p.Row + p.RowSpan).DefaultIfEmpty(0).Max();

    private static bool Fits(List<bool[]> occupied, int row, int column, int span, int rows)
    {
        for (var r = row; r < row + rows; r++)
        {
            if (r >= occupied.Count)
            {
                continue;
            }

            for (var c = column; c < column + span; c++)
            {
                if (occupied[r][c])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Mark(List<bool[]> occupied, int row, int column, int span, int rows, int columns)
    {
        while (occupied.Count < row + rows)
        {
            occupied.Add(new bool[columns]);
        }

        for (var r = row; r < row + rows; r++)
        {
            for (var c = column; c < column + span; c++)
            {
                occupied[r][c] = true;
            }
        }
    }
}
