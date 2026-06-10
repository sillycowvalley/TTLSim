using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc00Tests
{
    [Theory]
    [InlineData(Signal.Low, Signal.Low, Signal.High)]
    [InlineData(Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.Low)]
    public void Nand_truth_table(Signal a, Signal b, Signal expected)
    {
        Net netA = new(1), netB = new(2), netY = new(3);
        IChip drvA = a == Signal.High ? new VccDriver(netA) : new GndDriver(netA);
        IChip drvB = b == Signal.High ? new VccDriver(netB) : new GndDriver(netB);
        Hc00 gate = new(netA, netB, netY);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { drvA, drvB, (IChip)gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, netY.Value);
    }

    [Fact]
    public void Low_input_forces_high_even_with_unknown_other()
    {
        Net a = new(1), b = new(2), y = new(3);
        GndDriver va = new(a);   // a = Low, b never driven (Unknown)
        Hc00 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { va, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, y.Value);
    }

    [Fact]
    public void Floating_inputs_give_unknown_output()
    {
        Net a = new(1), b = new(2), y = new(3);
        Hc00 gate = new(a, b, y);

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
        Net a = new(1), b = new(2), y = new(3);
        VccDriver da = new(a);
        VccDriver db = new(b);   // both High -> output should fall to Low after tPD
        Hc00 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { da, db, gate });
        sim.Start();

        sim.RunUntil(Hc00.PropagationDelayPs - 1);
        Assert.NotEqual(Signal.Low, y.Value);

        sim.RunUntil(Hc00.PropagationDelayPs);
        Assert.Equal(Signal.Low, y.Value);
    }
}