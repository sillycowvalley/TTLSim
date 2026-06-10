using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc04Tests
{
    [Theory]
    [InlineData(Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.Low)]
    public void Inverter_truth_table(Signal a, Signal expected)
    {
        Net netA = new(1), netY = new(2);
        IChip drvA = a == Signal.High ? new VccDriver(netA) : new GndDriver(netA);
        Hc04 gate = new(netA, netY);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { drvA, (IChip)gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, netY.Value);
    }

    [Fact]
    public void Floating_input_gives_unknown_output()
    {
        Net a = new(1), y = new(2);
        Hc04 gate = new(a, y);

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
        Net a = new(1), y = new(2);
        GndDriver da = new(a);   // Low in -> output should rise to High after tPD
        Hc04 gate = new(a, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { da, gate });
        sim.Start();

        sim.RunUntil(Hc04.PropagationDelayPs - 1);
        Assert.NotEqual(Signal.High, y.Value);

        sim.RunUntil(Hc04.PropagationDelayPs);
        Assert.Equal(Signal.High, y.Value);
    }
}