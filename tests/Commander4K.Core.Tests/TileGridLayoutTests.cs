using Commander4K.Core.Dashboard;
using Commander4K.Core.Folders;

namespace Commander4K.Core.Tests;

public sealed class TileGridLayoutTests
{
    [Fact]
    public void Small_tiles_fill_rows_left_to_right()
    {
        var placements = TileGridLayout.Place([TileSize.Small, TileSize.Small, TileSize.Small, TileSize.Small], 3);

        Assert.Equal(
            [new TilePlacement(0, 0, 1, 1), new TilePlacement(1, 0, 1, 1), new TilePlacement(2, 0, 1, 1), new TilePlacement(0, 1, 1, 1)],
            placements);
    }

    [Fact]
    public void Small_tiles_fill_the_hole_beside_a_large_one()
    {
        var placements = TileGridLayout.Place([TileSize.Large, TileSize.Small, TileSize.Small, TileSize.Small], 3);

        Assert.Equal(new TilePlacement(0, 0, 2, 2), placements[0]);
        Assert.Equal(new TilePlacement(2, 0, 1, 1), placements[1]);
        Assert.Equal(new TilePlacement(2, 1, 1, 1), placements[2]);
        Assert.Equal(new TilePlacement(0, 2, 1, 1), placements[3]);
        Assert.Equal(3, TileGridLayout.RowCount(placements));
    }

    [Fact]
    public void Tiles_never_move_above_an_earlier_row()
    {
        // The wide tile cannot fit beside the first small one... it goes to row 1; the next small one stays at row 1 or later.
        var placements = TileGridLayout.Place([TileSize.Small, TileSize.Small, TileSize.Wide, TileSize.Small], 3);

        Assert.Equal(new TilePlacement(0, 1, 2, 1), placements[2]);
        Assert.True(placements[3].Row >= 1);
    }

    [Fact]
    public void Full_and_oversized_tiles_shrink_to_the_grid()
    {
        var placements = TileGridLayout.Place([TileSize.Full, TileSize.Huge, TileSize.Wide], 1);

        Assert.All(placements, p => Assert.Equal(1, p.ColumnSpan));
        Assert.Equal([0, 1, 3], placements.Select(p => p.Row));
    }

    [Fact]
    public void Full_takes_every_column()
    {
        var placements = TileGridLayout.Place([TileSize.Small, TileSize.Full], 4);

        Assert.Equal(new TilePlacement(0, 1, 4, 1), placements[1]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(200, 1)]
    [InlineData(532, 2)]
    [InlineData(1100, 4)]
    [InlineData(99999, 8)]
    public void Columns_follow_the_width(double width, int expected) =>
        Assert.Equal(expected, TileGridLayout.ColumnsFor(width, 260, 12));

    [Fact]
    public void Size_names_round_trip()
    {
        foreach (var size in TileSizes.Cycle)
        {
            Assert.Equal(size, TileSizes.Parse(TileSizes.Name(size)));
        }

        Assert.Null(TileSizes.Parse("giant"));
        Assert.Equal(TileSize.Small, TileSizes.Next(TileSize.Full));
    }
}
