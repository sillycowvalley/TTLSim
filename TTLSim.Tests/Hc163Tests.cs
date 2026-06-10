using TTLSim.Chips.Counters;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class Hc163Tests
{
    // Build a '163 with every pin on its own net. Control nets (/CLR, /LD,
    // CEP, CET, D0..D3) get GndDriver/VccDriver to hold them; CLK is driven
    // by a ManualClock so the test can pulse rising edges.
    private static (Hc163 cnt, ManualClock clk,
                    Net q0, Net q1, Net q2, Net q3, Net rco)
        BuildCounting()
    {
        Net clr = new(1), clk = new(2);
        Net d0 = new(3), d1 = new(4), d2 = new(5), d3 = new(6);
        Net cep = new(7), ld = new(9), cet = new(10);
        Net q0 = new(14), q1 = new(13), q2 = new(12), q3 = new(11);
        Net rco = new(15);

        Hc163 cnt = new(clr, clk, d0, d1, d2, d3, cep, ld, cet,
                        q0, q1, q2, q3, rco);
        ManualClock clkSrc = new(clk);

        // /CLR high (inactive), /LD high (inactive), CEP & CET high (count).
        VccDriver clrHigh = new(clr);
        VccDriver ldHigh = new(ld);
        VccDriver cepHigh = new(cep);
        VccDriver cetHigh = new(cet);
        // D0..D3 low -- unused while counting.
        GndDriver d0Low = new(d0), d1Low = new(d1), d2Low = new(d2), d3Low = new(d3);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, clkSrc, clrHigh, ldHigh, cepHigh, cetHigh,
                          d0Low, d1Low, d2Low, d3Low });
        sim.Start();
        sim.RunUntilQuiescent();

        StashSim(clkSrc, sim);
        return (cnt, clkSrc, q0, q1, q2, q3, rco);
    }

    // ManualClock.SetHigh/SetLow take the Simulator; keep a handle to it.
    private static Simulator currentSim = null!;
    private static void StashSim(ManualClock _, Simulator sim) => currentSim = sim;

    private static void Pulse(ManualClock clk)
    {
        clk.SetLow(currentSim);
        currentSim.RunUntil(currentSim.CurrentTick + 100_000);
        clk.SetHigh(currentSim);          // rising edge -- this is the active edge
        currentSim.RunUntil(currentSim.CurrentTick + 100_000);
    }

    private static int ReadCount(Net q0, Net q1, Net q2, Net q3)
    {
        int v = 0;
        if (q0.Value == Signal.High) v |= 1;
        if (q1.Value == Signal.High) v |= 2;
        if (q2.Value == Signal.High) v |= 4;
        if (q3.Value == Signal.High) v |= 8;
        return v;
    }

    [Fact]
    public void Counts_up_on_rising_edge()
    {
        var (cnt, clk, q0, q1, q2, q3, rco) = BuildCounting();
        Assert.Equal(0, ReadCount(q0, q1, q2, q3));

        Pulse(clk);
        Assert.Equal(1, ReadCount(q0, q1, q2, q3));

        Pulse(clk);
        Assert.Equal(2, ReadCount(q0, q1, q2, q3));
    }

    [Fact]
    public void Rolls_over_and_asserts_rco_at_fifteen()
    {
        var (cnt, clk, q0, q1, q2, q3, rco) = BuildCounting();

        for (int i = 1; i <= 15; i++)
        {
            Pulse(clk);
            Assert.Equal(i, ReadCount(q0, q1, q2, q3));
        }
        // Count is 15: RCO asserted (CET is high).
        Assert.Equal(Signal.High, rco.Value);

        // One more pulse: rolls to 0, RCO drops.
        Pulse(clk);
        Assert.Equal(0, ReadCount(q0, q1, q2, q3));
        Assert.Equal(Signal.Low, rco.Value);
    }

    [Fact]
    public void Synchronous_clear_takes_effect_on_the_edge()
    {
        Net clr = new(1), clk = new(2);
        Net d0 = new(3), d1 = new(4), d2 = new(5), d3 = new(6);
        Net cep = new(7), ld = new(9), cet = new(10);
        Net q0 = new(14), q1 = new(13), q2 = new(12), q3 = new(11);
        Net rco = new(15);

        Hc163 cnt = new(clr, clk, d0, d1, d2, d3, cep, ld, cet,
                        q0, q1, q2, q3, rco);
        ManualClock clkSrc = new(clk);
        ManualClock clrSrc = new(clr);
        VccDriver ldHigh = new(ld);
        VccDriver cepHigh = new(cep);
        VccDriver cetHigh = new(cet);
        GndDriver d0Low = new(d0), d1Low = new(d1), d2Low = new(d2), d3Low = new(d3);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, clkSrc, clrSrc, ldHigh, cepHigh, cetHigh,
                          d0Low, d1Low, d2Low, d3Low });
        sim.Start();
        clrSrc.SetHigh(sim);                  // /CLR inactive before we count
        sim.RunUntilQuiescent();
        currentSim = sim;

        for (int i = 0; i < 3; i++) Pulse(clkSrc);
        Assert.Equal(3, ReadCount(q0, q1, q2, q3));

        clrSrc.SetLow(sim);                   // assert synchronous clear
        Pulse(clkSrc);                        // edge samples /CLR low -> zero
        Assert.Equal(0, ReadCount(q0, q1, q2, q3));
    }

}