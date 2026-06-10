using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc86Tests
{
    [Theory]
    [InlineData(Signal.Low, Signal.Low, Signal.Low)]
    [InlineData(Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.Low)]
    public void Xor_truth_table(Signal a, Signal b, Signal expected)
    {
        Net netA = new(1), netB = new(2), netY = new(3);
        IChip drvA = a == Signal.High ? new VccDriver(netA) : new GndDriver(netA);
        IChip drvB = b == Signal.High ? new VccDriver(netB) : new GndDriver(netB);
        Hc86 gate = new(netA, netB, netY);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { drvA, drvB, (IChip)gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, netY.Value);
    }

    [Fact]
    public void Floating_inputs_give_unknown_output()
    {
        Net a = new(1), b = new(2), y = new(3);
        Hc86 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void Known_input_with_unknown_other_gives_unknown()
    {
        // XOR has no dominating value: a single Unknown leaves the result Unknown
        // regardless of the other input's value.
        Net a = new(1), b = new(2), y = new(3);
        VccDriver va = new(a);   // a = High, b floating
        Hc86 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { va, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void Low_input_with_unknown_other_also_gives_unknown()
    {
        // Same point as above, mirrored: confirms there's no "Low dominates" shortcut.
        Net a = new(1), b = new(2), y = new(3);
        GndDriver va = new(a);   // a = Low, b floating
        Hc86 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { va, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void Output_settles_after_propagation_delay()
    {
        Net a = new(1), b = new(2), y = new(3);
        VccDriver da = new(a);
        GndDriver db = new(b);   // High XOR Low -> output should rise to High after tPD
        Hc86 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { da, db, gate });
        sim.Start();

        sim.RunUntil(Hc86.PropagationDelayPs - 1);
        Assert.NotEqual(Signal.High, y.Value);

        sim.RunUntil(Hc86.PropagationDelayPs);
        Assert.Equal(Signal.High, y.Value);
    }
}