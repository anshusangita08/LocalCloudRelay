using System.ComponentModel;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// The model grid puts a published price or a blank in the same column, and every
/// column sorts. That combination crashed the app: DataGridView compares boxed cell
/// values with Comparer&lt;object&gt;.Default, which calls Decimal.CompareTo(object),
/// and that throws "Object must be of type Decimal" when the other side is DBNull.
/// </summary>
public sealed class SortableColumnTests
{
    /// <summary>Mirrors how the grid fills a row: real prices are decimals, blanks are null.</summary>
    private static void AddPricedRow(DataGridView grid, decimal? price) =>
        grid.Rows.Add(new object?[] { MainForm.Cell(price) }!);

    private static DataGridView PriceGrid()
    {
        var grid = new DataGridView { AllowUserToAddRows = false };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "In $/1M",
            SortMode = DataGridViewColumnSortMode.Automatic
        });
        return grid;
    }

    [Fact]
    public void ABlankPriceSortsInsteadOfThrowing()
    {
        using var grid = PriceGrid();
        AddPricedRow(grid, 1.5m);
        AddPricedRow(grid, null);
        AddPricedRow(grid, 0.25m);

        var failure = Record.Exception(() => grid.Sort(grid.Columns[0], ListSortDirection.Ascending));

        Assert.Null(failure);
        // A blank sorts before every price, which puts the unpriced models together at
        // one end rather than scattering them through the list.
        Assert.Null(grid.Rows[0].Cells[0].Value);
        Assert.Equal(0.25m, grid.Rows[1].Cells[0].Value);
        Assert.Equal(1.5m, grid.Rows[2].Cells[0].Value);
    }

    [Fact]
    public void DescendingOrderAlsoSurvivesABlank()
    {
        using var grid = PriceGrid();
        AddPricedRow(grid, null);
        AddPricedRow(grid, 2m);

        Assert.Null(Record.Exception(() => grid.Sort(grid.Columns[0], ListSortDirection.Descending)));
        Assert.Equal(2m, grid.Rows[0].Cells[0].Value);
        Assert.Null(grid.Rows[1].Cells[0].Value);
    }

    [Fact]
    public void BlankIsNullAndNeverDBNull()
    {
        // The invariant both tests above depend on. DBNull is boxed as its own type, so
        // it is the one value that cannot be compared against a decimal.
        Assert.Null(MainForm.Cell((decimal?)null));
        Assert.NotEqual(DBNull.Value, MainForm.Cell((decimal?)null));

        // A real price is boxed as a decimal, not left as a nullable wrapper.
        Assert.IsType<decimal>(MainForm.Cell(0.14m));

        // Counts are always present, so they never carry a blank.
        Assert.IsType<long>(MainForm.Cell(0L));
    }
}
