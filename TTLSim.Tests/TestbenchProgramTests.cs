using System;
using System.Linq;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class TestbenchProgramTests
{
    private const string Simple =
        "CLK,/RESET,D0\n" +
        "L,L,L\n" +
        "H,L,H\n" +
        "L,H,Z\n";

    [Fact]
    public void Parses_columns_rows_and_cells()
    {
        var p = TestbenchProgram.Parse(Simple);

        Assert.Equal(new[] { "CLK", "/RESET", "D0" }, p.ColumnNames);
        Assert.Equal(3, p.ColumnCount);
        Assert.Equal(3, p.RowCount);

        Assert.Equal(Signal.Low, p[0, 0]);
        Assert.Equal(Signal.Low, p[0, 1]);
        Assert.Equal(Signal.Low, p[0, 2]);

        Assert.Equal(Signal.High, p[1, 0]);
        Assert.Equal(Signal.Low, p[1, 1]);
        Assert.Equal(Signal.High, p[1, 2]);

        Assert.Equal(Signal.Low, p[2, 0]);
        Assert.Equal(Signal.High, p[2, 1]);
        Assert.Equal(Signal.HighZ, p[2, 2]);
    }

    [Theory]
    [InlineData("h", Signal.High)]
    [InlineData("H", Signal.High)]
    [InlineData("l", Signal.Low)]
    [InlineData("L", Signal.Low)]
    [InlineData("z", Signal.HighZ)]
    [InlineData("Z", Signal.HighZ)]
    public void Cell_values_are_case_insensitive(string cell, Signal expected)
    {
        var p = TestbenchProgram.Parse("A\n" + cell + "\n");
        Assert.Equal(expected, p[0, 0]);
    }

    [Fact]
    public void Whitespace_around_names_and_cells_is_ignored()
    {
        var p = TestbenchProgram.Parse("  CLK , D0  \n  H ,  L \n");

        Assert.Equal(new[] { "CLK", "D0" }, p.ColumnNames);
        Assert.Equal(Signal.High, p[0, 0]);
        Assert.Equal(Signal.Low, p[0, 1]);
    }

    [Fact]
    public void Blank_lines_are_ignored_anywhere()
    {
        var p = TestbenchProgram.Parse("\n\nA,B\n\nH,L\n\n\nL,H\n\n");

        Assert.Equal(2, p.ColumnCount);
        Assert.Equal(2, p.RowCount);
        Assert.Equal(Signal.High, p[0, 0]);
        Assert.Equal(Signal.High, p[1, 1]);
    }

    [Fact]
    public void Crlf_and_lf_are_both_accepted()
    {
        var lf = TestbenchProgram.Parse("A,B\nH,L\n");
        var crlf = TestbenchProgram.Parse("A,B\r\nH,L\r\n");

        Assert.Equal(lf.RowCount, crlf.RowCount);
        Assert.Equal(lf[0, 0], crlf[0, 0]);
        Assert.Equal(lf[0, 1], crlf[0, 1]);
    }

    [Fact]
    public void Duplicate_column_names_are_rejected()
    {
        var ex = Assert.Throws<FormatException>(
            () => TestbenchProgram.Parse("CLK,D0,CLK\nL,L,L\n"));
        Assert.Contains("CLK", ex.Message);
    }

    [Fact]
    public void Names_differing_only_in_case_are_distinct_columns()
    {
        // Ordinal comparison, the same rule net labels use.
        var p = TestbenchProgram.Parse("we,WE\nH,L\n");
        Assert.Equal(2, p.ColumnCount);
    }

    [Fact]
    public void Empty_column_name_is_rejected()
    {
        Assert.Throws<FormatException>(() => TestbenchProgram.Parse("CLK,,D0\nL,L,L\n"));
    }

    [Fact]
    public void Expected_value_column_prefix_is_reserved()
    {
        // '?' is held for a later assertion feature. It must be an error now,
        // so no file written today can silently change meaning when it lands.
        var ex = Assert.Throws<FormatException>(
            () => TestbenchProgram.Parse("CLK,?PE\nL,L\n"));
        Assert.Contains("reserved", ex.Message);
    }

    [Fact]
    public void Row_with_wrong_cell_count_is_rejected()
    {
        var ex = Assert.Throws<FormatException>(
            () => TestbenchProgram.Parse("A,B,C\nH,L,H\nH,L\n"));
        // Third physical line is the short row.
        Assert.Contains("Line 3", ex.Message);
    }

    [Fact]
    public void Unknown_cell_value_is_rejected_and_names_the_column()
    {
        var ex = Assert.Throws<FormatException>(
            () => TestbenchProgram.Parse("CLK,D0\nH,X\n"));
        Assert.Contains("D0", ex.Message);
    }

    [Fact]
    public void Header_without_rows_is_rejected()
    {
        Assert.Throws<FormatException>(() => TestbenchProgram.Parse("A,B,C\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Empty_input_is_rejected(string? csv)
    {
        Assert.Throws<FormatException>(() => TestbenchProgram.Parse(csv));
    }

    [Fact]
    public void Column_count_is_capped()
    {
        string header = string.Join(",",
            Enumerable.Range(0, TestbenchProgram.MaxColumns + 1).Select(i => "C" + i));
        string row = string.Join(",",
            Enumerable.Range(0, TestbenchProgram.MaxColumns + 1).Select(_ => "L"));

        Assert.Throws<FormatException>(() => TestbenchProgram.Parse(header + "\n" + row + "\n"));
    }

    [Fact]
    public void Maximum_column_count_is_accepted()
    {
        string header = string.Join(",",
            Enumerable.Range(0, TestbenchProgram.MaxColumns).Select(i => "C" + i));
        string row = string.Join(",",
            Enumerable.Range(0, TestbenchProgram.MaxColumns).Select(_ => "H"));

        var p = TestbenchProgram.Parse(header + "\n" + row + "\n");
        Assert.Equal(TestbenchProgram.MaxColumns, p.ColumnCount);
    }
}
