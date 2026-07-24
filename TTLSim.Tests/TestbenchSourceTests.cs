using System;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class TestbenchSourceTests
{
    // One microsecond per row.
    private const long RowPeriodPs = 1_000_000;

    private const string Program =
        "A,B\n" +
        "L,L\n" +   // row 0 @ 0 us
        "H,L\n" +   // row 1 @ 1 us
        "H,H\n" +   // row 2 @ 2 us
        "L,L\n";    // row 3 @ 3 us

    [Fact]
    public void Rows_are_applied_one_per_period()
    {
        var (sim, a, b) = Build(Program);

        sim.RunUntil(0);
        Assert.Equal(Signal.Low, a.Value);
        Assert.Equal(Signal.Low, b.Value);

        sim.RunUntil(RowPeriodPs);
        Assert.Equal(Signal.High, a.Value);
        Assert.Equal(Signal.Low, b.Value);

        sim.RunUntil(2 * RowPeriodPs);
        Assert.Equal(Signal.High, a.Value);
        Assert.Equal(Signal.High, b.Value);

        sim.RunUntil(3 * RowPeriodPs);
        Assert.Equal(Signal.Low, a.Value);
        Assert.Equal(Signal.Low, b.Value);
    }

    [Fact]
    public void Value_persists_across_the_whole_row()
    {
        var (sim, a, _) = Build(Program);

        // Row 1 drives A high at 1 us; it must still be high just before the
        // row-2 boundary, not glitch back between scheduled events.
        sim.RunUntil(RowPeriodPs);
        Assert.Equal(Signal.High, a.Value);

        sim.RunUntil(2 * RowPeriodPs - 1);
        Assert.Equal(Signal.High, a.Value);
    }

    [Fact]
    public void Final_row_holds_after_the_program_ends()
    {
        // Last row is L,L. Long after the program is exhausted the pins must
        // still be driven at those values -- hold, not release, not repeat.
        var (sim, a, b) = Build("A,B\nH,H\nL,H\n");

        sim.RunUntil(500 * RowPeriodPs);
        Assert.Equal(Signal.Low, a.Value);
        Assert.Equal(Signal.High, b.Value);
    }

    [Fact]
    public void Single_row_program_drives_from_tick_zero_and_holds()
    {
        var (sim, a, b) = Build("A,B\nH,L\n");

        sim.RunUntil(0);
        Assert.Equal(Signal.High, a.Value);
        Assert.Equal(Signal.Low, b.Value);

        sim.RunUntil(100 * RowPeriodPs);
        Assert.Equal(Signal.High, a.Value);
        Assert.Equal(Signal.Low, b.Value);
    }

    [Fact]
    public void Z_releases_the_net_so_another_driver_wins()
    {
        // The point of Z: a testbench column can share a net with something the
        // circuit drives. Here a VccDriver holds net A while the program moves
        // A from a driven Low to Z. While the testbench drives Low the net is
        // contended; once it releases, the other driver must own the net.
        var program = TestbenchProgram.Parse("A\nL\nZ\n");

        Net a = new(1);
        var bench = new TestbenchSource(
            program, new[] { 1 }, new[] { a }, RowPeriodPs);

        var chips = new IChip[] { bench, new VccDriver(a) };
        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), chips);
        sim.Start();

        sim.RunUntil(RowPeriodPs);
        Assert.Equal(Signal.High, a.Value);
    }

    [Fact]
    public void Pin_and_net_counts_must_match_the_column_count()
    {
        var program = TestbenchProgram.Parse("A,B\nH,L\n");
        Net only = new(1);

        Assert.Throws<ArgumentException>(() =>
            new TestbenchSource(program, new[] { 1 }, new[] { only }, RowPeriodPs));
    }

    [Fact]
    public void Pin_numbers_are_reported_in_column_order()
    {
        // Column order is the contract between the item and the simulator:
        // the item hands over pin numbers in program column order, whatever
        // order they were allocated in.
        var program = TestbenchProgram.Parse("A,B\nH,L\n");
        Net a = new(1), b = new(2);

        var bench = new TestbenchSource(program, new[] { 7, 3 }, new[] { a, b }, RowPeriodPs);

        Assert.Equal(new[] { 7, 3 }, bench.PinNumbers);
        Assert.Same(a, bench.Nets[0]);
        Assert.Same(b, bench.Nets[1]);
    }

    // Build the source on two fresh nets and start the simulation.
    private static (Simulator Sim, Net A, Net B) Build(string csv)
    {
        var program = TestbenchProgram.Parse(csv);

        Net a = new(1), b = new(2);
        var bench = new TestbenchSource(
            program, new[] { 1, 2 }, new[] { a, b }, RowPeriodPs);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { bench });
        sim.Start();

        return (sim, a, b);
    }
}
