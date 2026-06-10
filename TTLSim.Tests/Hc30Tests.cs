using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc30Tests
{
    [Theory]
    // All Low -> output High
    [InlineData("00000000", Signal.High)]
    // All High -> output Low
    [InlineData("11111111", Signal.High - Signal.High + Signal.Low)]  // placeholder; see [Fact] below
    public void Endpoints(string pattern, Signal expected)
    {
        // (See the dedicated facts below for the endpoints -- this Theory
        // is just here so the structure stays consistent.)
        _ = pattern; _ = expected;
    }

    [Fact]
    public void All_low_gives_high()
    {
        RunPattern("00000000", Signal.High);
    }

    [Fact]
    public void All_high_gives_low()
    {
        RunPattern("11111111", Signal.Low);
    }

    [Theory]
    // One Low among seven Highs -> output still High (Low dominates NAND).
    [InlineData("01111111")]
    [InlineData("10111111")]
    [InlineData("11011111")]
    [InlineData("11101111")]
    [InlineData("11110111")]
    [InlineData("11111011")]
    [InlineData("11111101")]
    [InlineData("11111110")]
    public void Any_single_low_forces_high(string pattern)
    {
        RunPattern(pattern, Signal.High);
    }

    [Theory]
    // One High among seven Lows -> output still High (still has Lows -> dominates).
    [InlineData("10000000")]
    [InlineData("01000000")]
    [InlineData("00100000")]
    [InlineData("00010000")]
    [InlineData("00001000")]
    [InlineData("00000100")]
    [InlineData("00000010")]
    [InlineData("00000001")]
    public void Any_single_high_among_lows_stays_high(string pattern)
    {
        RunPattern(pattern, Signal.High);
    }

    [Fact]
    public void Seven_high_one_unknown_gives_unknown_output()
    {
        Net[] nets = MakeNets();
        Net y = new(9);
        VccDriver[] drivers =
        {
            new(nets[0]), new(nets[1]), new(nets[2]), new(nets[3]),
            new(nets[4]), new(nets[5]), new(nets[6])
            // nets[7] floats -> Unknown
        };
        Hc30 gate = new(nets[0], nets[1], nets[2], nets[3],
                        nets[4], nets[5], nets[6], nets[7], y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { drivers[0], drivers[1], drivers[2], drivers[3],
                          drivers[4], drivers[5], drivers[6], gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void Low_with_rest_floating_still_forces_high()
    {
        Net[] nets = MakeNets();
        Net y = new(9);
        GndDriver lo = new(nets[0]);  // one Low, the other seven float
        Hc30 gate = new(nets[0], nets[1], nets[2], nets[3],
                        nets[4], nets[5], nets[6], nets[7], y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { lo, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, y.Value);
    }

    [Fact]
    public void Floating_inputs_give_unknown_output()
    {
        Net[] nets = MakeNets();
        Net y = new(9);
        Hc30 gate = new(nets[0], nets[1], nets[2], nets[3],
                        nets[4], nets[5], nets[6], nets[7], y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void Output_settles_after_propagation_delay()
    {
        Net[] nets = MakeNets();
        Net y = new(9);
        VccDriver[] drivers =
        {
            new(nets[0]), new(nets[1]), new(nets[2]), new(nets[3]),
            new(nets[4]), new(nets[5]), new(nets[6]), new(nets[7])
        };
        Hc30 gate = new(nets[0], nets[1], nets[2], nets[3],
                        nets[4], nets[5], nets[6], nets[7], y);

        IChip[] all =
        {
            drivers[0], drivers[1], drivers[2], drivers[3],
            drivers[4], drivers[5], drivers[6], drivers[7], gate
        };
        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            all);
        sim.Start();

        sim.RunUntil(Hc30.PropagationDelayPs - 1);
        Assert.NotEqual(Signal.Low, y.Value);

        sim.RunUntil(Hc30.PropagationDelayPs);
        Assert.Equal(Signal.Low, y.Value);
    }

    // ---- helpers ----

    private static Net[] MakeNets()
    {
        Net[] n = new Net[8];
        for (int i = 0; i < 8; i++) n[i] = new Net(i + 1);
        return n;
    }

    private static void RunPattern(string pattern, Signal expected)
    {
        Assert.Equal(8, pattern.Length);
        Net[] nets = MakeNets();
        Net y = new(9);

        var chips = new List<IChip>();
        for (int i = 0; i < 8; i++)
            chips.Add(pattern[i] == '1' ? new VccDriver(nets[i]) : new GndDriver(nets[i]));

        Hc30 gate = new(nets[0], nets[1], nets[2], nets[3],
                        nets[4], nets[5], nets[6], nets[7], y);
        chips.Add(gate);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips);
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, y.Value);
    }
}