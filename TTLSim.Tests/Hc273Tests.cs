using TTLSim.Chips.Registers;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class Hc273Tests
{
    // Build a '273 with every pin on its own net. /CLR and CLK are driven
    // by ManualClocks so tests can assert and pulse them; the eight D
    // inputs are pinned by VccDriver/GndDriver according to the dataBits
    // pattern (bit i set -> Di high). Follows the Hc163Tests harness.
    private static (Simulator sim, Hc273 reg, ManualClock clk, ManualClock clr, Net[] q)
        Build(int dataBits)
    {
        Net clrNet = new(1), clkNet = new(11);
        Net[] d =
        {
            new(3), new(4), new(7), new(8),
            new(13), new(14), new(17), new(18)
        };
        Net[] q =
        {
            new(2), new(5), new(6), new(9),
            new(12), new(15), new(16), new(19)
        };

        Hc273 reg = new(
            clrNet, clkNet,
            d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7],
            q[0], q[1], q[2], q[3], q[4], q[5], q[6], q[7]);
        ManualClock clkSrc = new(clkNet);
        ManualClock clrSrc = new(clrNet);

        var chips = new List<IChip> { reg, clkSrc, clrSrc };
        for (int i = 0; i < 8; i++)
        {
            if (((dataBits >> i) & 1) != 0) chips.Add(new VccDriver(d[i]));
            else chips.Add(new GndDriver(d[i]));
        }

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        clrSrc.SetHigh(sim);                  // /CLR inactive
        sim.RunUntilQuiescent();

        return (sim, reg, clkSrc, clrSrc, q);
    }

    private static void Pulse(Simulator sim, ManualClock clk)
    {
        clk.SetHigh(sim);
        sim.RunUntilQuiescent();
        clk.SetLow(sim);
        sim.RunUntilQuiescent();
    }

    private static int ReadByte(Net[] q)
    {
        int v = 0;
        for (int i = 0; i < 8; i++)
            if (q[i].Value == Signal.High)
                v |= 1 << i;
        return v;
    }

    [Fact]
    public void Powers_up_cleared()
    {
        var (_, _, _, _, q) = Build(0xA5);
        Assert.Equal(0x00, ReadByte(q));
    }

    [Fact]
    public void Loads_data_on_rising_edge()
    {
        var (sim, _, clk, _, q) = Build(0xA5);

        Pulse(sim, clk);
        Assert.Equal(0xA5, ReadByte(q));
    }

    [Fact]
    public void Data_change_without_edge_does_not_propagate()
    {
        // D0 gets its own manual source so the test can move it between
        // clock edges; the remaining D pins are held low.
        Net clrNet = new(1), clkNet = new(11);
        Net[] d =
        {
            new(3), new(4), new(7), new(8),
            new(13), new(14), new(17), new(18)
        };
        Net[] q =
        {
            new(2), new(5), new(6), new(9),
            new(12), new(15), new(16), new(19)
        };

        Hc273 reg = new(
            clrNet, clkNet,
            d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7],
            q[0], q[1], q[2], q[3], q[4], q[5], q[6], q[7]);
        ManualClock clkSrc = new(clkNet);
        ManualClock clrSrc = new(clrNet);
        ManualClock d0Src = new(d[0]);

        var chips = new List<IChip> { reg, clkSrc, clrSrc, d0Src };
        for (int i = 1; i < 8; i++) chips.Add(new GndDriver(d[i]));

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        clrSrc.SetHigh(sim);
        d0Src.SetLow(sim);
        sim.RunUntilQuiescent();

        Pulse(sim, clkSrc);
        Assert.Equal(0x00, ReadByte(q));

        // Raise D0 between edges: Q must hold until the next rising edge.
        d0Src.SetHigh(sim);
        sim.RunUntilQuiescent();
        Assert.Equal(0x00, ReadByte(q));

        Pulse(sim, clkSrc);
        Assert.Equal(0x01, ReadByte(q));
    }

    [Fact]
    public void Async_clear_overrides_without_a_clock_edge()
    {
        var (sim, _, clk, clr, q) = Build(0xFF);

        Pulse(sim, clk);
        Assert.Equal(0xFF, ReadByte(q));

        clr.SetLow(sim);                      // assert /CLR -- no clock pulse
        sim.RunUntilQuiescent();
        Assert.Equal(0x00, ReadByte(q));
    }

    [Fact]
    public void Clear_held_low_blocks_loading_until_released()
    {
        var (sim, _, clk, clr, q) = Build(0xFF);

        clr.SetLow(sim);
        sim.RunUntilQuiescent();

        Pulse(sim, clk);                      // edge while /CLR low: pinned at 0
        Assert.Equal(0x00, ReadByte(q));

        clr.SetHigh(sim);                     // release
        sim.RunUntilQuiescent();
        Assert.Equal(0x00, ReadByte(q));      // release alone loads nothing

        Pulse(sim, clk);                      // next edge loads normally
        Assert.Equal(0xFF, ReadByte(q));
    }
}