using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc08Tests
{
    [Theory]
    [InlineData(Signal.Low, Signal.Low, Signal.Low)]
    [InlineData(Signal.Low, Signal.High, Signal.Low)]
    [InlineData(Signal.High, Signal.Low, Signal.Low)]
    [InlineData(Signal.High, Signal.High, Signal.High)]
    public void And_truth_table(Signal a, Signal b, Signal expected)
    {
        Net netA = new(1), netB = new(2), netY = new(3);
        IChip drvA = a == Signal.High ? new VccDriver(netA) : new GndDriver(netA);
        IChip drvB = b == Signal.High ? new VccDriver(netB) : new GndDriver(netB);
        Hc08 gate = new(netA, netB, netY);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { drvA, drvB, (IChip)gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, netY.Value);
    }

    [Fact]
    public void Low_input_dominates_even_with_unknown_other()
    {
        Net a = new(1), b = new(2), y = new(3);
        GndDriver va = new(a);   // a = Low, b Unknown
        Hc08 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { va, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, y.Value);
    }
}