using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class SourceTests
{
    [Fact]
    public void VccDriver_drives_high_at_tick_zero()
    {
        Net n = new(1);
        VccDriver vcc = new(n);
        Simulator sim = new(NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), new[] { (IChip)vcc });

        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, n.Value);
    }

    [Fact]
    public void GndDriver_drives_low_at_tick_zero()
    {
        Net n = new(1);
        GndDriver gnd = new(n);
        Simulator sim = new(NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), new[] { (IChip)gnd });

        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, n.Value);
    }

    [Fact]
    public void ClockSource_toggles_every_half_period()
    {
        Net n = new(1);
        // 1 MHz -> period 1,000,000 ps, half-period 500,000 ps.
        ClockSource clk = new(n, periodPicoseconds: 1_000_000);
        Simulator sim = new(NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), new[] { (IChip)clk });

        sim.Start();

        // After tick 0, net is Low.
        sim.RunUntil(0);
        Assert.Equal(Signal.Low, n.Value);

        // After first half-period, net flips High.
        sim.RunUntil(500_000);
        Assert.Equal(Signal.High, n.Value);

        // After full period, back to Low.
        sim.RunUntil(1_000_000);
        Assert.Equal(Signal.Low, n.Value);

        // Three full periods covered: ten transitions Low->High->Low->...
        sim.RunUntil(3_500_000);
        Assert.Equal(Signal.High, n.Value);
    }
}