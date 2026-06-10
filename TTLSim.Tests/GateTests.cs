using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class Hc32Tests
{
    [Theory]
    [InlineData(Signal.Low, Signal.Low, Signal.Low)]
    [InlineData(Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.High)]
    public void Or_truth_table(Signal a, Signal b, Signal expected)
    {
        Net netA = new(1), netB = new(2), netY = new(3);
        IChip drvA = a == Signal.High ? new VccDriver(netA) : new GndDriver(netA);
        IChip drvB = b == Signal.High ? new VccDriver(netB) : new GndDriver(netB);
        Hc32 gate = new(netA, netB, netY);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { drvA, drvB, (IChip)gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, netY.Value);
    }

    [Fact]
    public void Floating_input_with_no_high_produces_unknown()
    {
        Net a = new(1), b = new(2), y = new(3);
        GndDriver va = new(a);   // a = Low, b never driven (Unknown)
        Hc32 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { va, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void High_input_dominates_even_with_unknown_other()
    {
        Net a = new(1), b = new(2), y = new(3);
        VccDriver va = new(a);   // a = High, b Unknown
        Hc32 gate = new(a, b, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { va, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, y.Value);
    }
}

public class Hc14Tests
{
    [Theory]
    [InlineData(Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.Low)]
    public void Inverter_truth_table(Signal input, Signal expected)
    {
        Net netIn = new(1), netOut = new(2);
        IChip drv = input == Signal.High ? new VccDriver(netIn) : new GndDriver(netIn);
        Hc14 inv = new(netIn, netOut);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { drv, (IChip)inv });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, netOut.Value);
    }

    [Fact]
    public void Undriven_input_gives_unknown_output()
    {
        Net netIn = new(1), netOut = new(2);
        Hc14 inv = new(netIn, netOut);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { (IChip)inv });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, netOut.Value);
    }

    [Fact]
    public void Output_settles_after_propagation_delay()
    {
        Net netIn = new(1), netOut = new(2);
        VccDriver drv = new(netIn);
        Hc14 inv = new(netIn, netOut);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { drv, inv });
        sim.Start();

        sim.RunUntil(Hc14.PropagationDelayPs - 1);
        Assert.NotEqual(Signal.Low, netOut.Value);

        sim.RunUntil(Hc14.PropagationDelayPs);
        Assert.Equal(Signal.Low, netOut.Value);   // High in -> Low out
    }
}