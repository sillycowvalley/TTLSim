using TTLSim.Chips.Decoders;
using TTLSim.Chips.Displays;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class Ls47Tests
{
    // Helper: build a 74HC47 driven by fixed BCD on A,B,C,D, with LT/RBI/BI
    // all tied high (normal operation), and read back segment a..g states.
    // bcd is 0..15.
    private static (Net[] segs, Simulator sim) BuildHc47(int bcd,
        Signal lt = Signal.High, Signal rbi = Signal.High, Signal bi = Signal.High)
    {
        Net a = new(1), b = new(2), c = new(3), d = new(4);
        Net ltN = new(5), rbiN = new(6), biN = new(7);
        Net sa = new(10), sb = new(11), sc = new(12), sd = new(13), se = new(14), sf = new(15), sg = new(16);

        List<IChip> chips = new();
        chips.Add(DriverFor(a, (bcd & 1) != 0));
        chips.Add(DriverFor(b, (bcd & 2) != 0));
        chips.Add(DriverFor(c, (bcd & 4) != 0));
        chips.Add(DriverFor(d, (bcd & 8) != 0));
        chips.Add(DriverForSignal(ltN, lt));
        chips.Add(DriverForSignal(rbiN, rbi));
        chips.Add(DriverForSignal(biN, bi));
        chips.Add(new Ls47(a, b, c, d, ltN, rbiN, biN, sa, sb, sc, sd, se, sf, sg));

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            chips);
        sim.Start();
        sim.RunUntilQuiescent();

        return (new[] { sa, sb, sc, sd, se, sf, sg }, sim);
    }

    private static IChip DriverFor(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);

    private static IChip DriverForSignal(Net net, Signal s) =>
        s == Signal.High ? new VccDriver(net) : new GndDriver(net);

    // Output is active-low: lit segment = Low.
    private static bool Lit(Signal s) => s == Signal.Low;

    [Fact]
    public void Digit_0_lights_a_through_f_not_g()
    {
        var (segs, _) = BuildHc47(0);
        bool[] lit = segs.Select(n => Lit(n.Value)).ToArray();

        Assert.Equal(new[] { true, true, true, true, true, true, false }, lit);
    }

    [Fact]
    public void Digit_1_lights_only_b_and_c()
    {
        var (segs, _) = BuildHc47(1);
        bool[] lit = segs.Select(n => Lit(n.Value)).ToArray();

        Assert.Equal(new[] { false, true, true, false, false, false, false }, lit);
    }

    [Fact]
    public void Digit_8_lights_all_segments()
    {
        var (segs, _) = BuildHc47(8);
        bool[] lit = segs.Select(n => Lit(n.Value)).ToArray();

        Assert.All(lit, b => Assert.True(b));
    }

    [Fact]
    public void Lamp_test_lights_all_segments_regardless_of_bcd()
    {
        var (segs, _) = BuildHc47(0, lt: Signal.Low);
        bool[] lit = segs.Select(n => Lit(n.Value)).ToArray();
        Assert.All(lit, b => Assert.True(b));
    }

    [Fact]
    public void Blanking_input_blanks_all_segments()
    {
        var (segs, _) = BuildHc47(8, bi: Signal.Low);
        bool[] lit = segs.Select(n => Lit(n.Value)).ToArray();
        Assert.All(lit, b => Assert.False(b));
    }

    [Fact]
    public void Ripple_blanking_blanks_only_zero()
    {
        var (segs0, _) = BuildHc47(0, rbi: Signal.Low);
        var (segs5, _) = BuildHc47(5, rbi: Signal.Low);

        Assert.All(segs0.Select(n => Lit(n.Value)), b => Assert.False(b));
        // Digit 5 should still display normally.
        Assert.Contains(segs5.Select(n => Lit(n.Value)), b => b);
    }
}

public class SevenSegCaTests
{
    [Fact]
    public void Common_anode_lit_when_common_high_and_segment_low()
    {
        Net[] segs = Enumerable.Range(0, 7).Select(i => new Net(i + 1)).ToArray();
        Net dp = new(8);
        Net com = new(9);

        // All segments driven Low, dp High, com High -> all 7 lit, dp dark.
        List<IChip> chips = new();
        for (int i = 0; i < 7; i++) chips.Add(new GndDriver(segs[i]));
        chips.Add(new VccDriver(dp));
        chips.Add(new VccDriver(com));

        SevenSegCa display = new(
            segs[0], segs[1], segs[2], segs[3], segs[4], segs[5], segs[6], dp, com);
        chips.Add(display);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            chips);
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.All(display.Segments, lit => Assert.True(lit));
        Assert.False(display.Dp);
    }

    [Fact]
    public void Common_anode_all_dark_when_common_is_low()
    {
        Net[] segs = Enumerable.Range(0, 7).Select(i => new Net(i + 1)).ToArray();
        Net dp = new(8);
        Net com = new(9);

        List<IChip> chips = new();
        for (int i = 0; i < 7; i++) chips.Add(new GndDriver(segs[i]));
        chips.Add(new GndDriver(dp));
        chips.Add(new GndDriver(com));   // anode at ground -> nothing lights

        SevenSegCa display = new(
            segs[0], segs[1], segs[2], segs[3], segs[4], segs[5], segs[6], dp, com);
        chips.Add(display);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            chips);
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.All(display.Segments, lit => Assert.False(lit));
    }
}