using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;

public class Hc20Tests
{
    [Theory]
    [InlineData(Signal.Low, Signal.Low, Signal.Low, Signal.Low, Signal.High)]
    [InlineData(Signal.Low, Signal.Low, Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.Low, Signal.Low, Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.Low, Signal.Low, Signal.High, Signal.High, Signal.High)]
    [InlineData(Signal.Low, Signal.High, Signal.Low, Signal.Low, Signal.High)]
    [InlineData(Signal.Low, Signal.High, Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.Low, Signal.High, Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.Low, Signal.High, Signal.High, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.Low, Signal.Low, Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.Low, Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.Low, Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.Low, Signal.High, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.Low, Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.Low, Signal.High, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.High, Signal.Low, Signal.High)]
    [InlineData(Signal.High, Signal.High, Signal.High, Signal.High, Signal.Low)]
    public void Nand4_truth_table(Signal a, Signal b, Signal c, Signal d, Signal expected)
    {
        Net netA = new(1), netB = new(2), netC = new(3), netD = new(4), netY = new(5);
        IChip drvA = a == Signal.High ? new VccDriver(netA) : new GndDriver(netA);
        IChip drvB = b == Signal.High ? new VccDriver(netB) : new GndDriver(netB);
        IChip drvC = c == Signal.High ? new VccDriver(netC) : new GndDriver(netC);
        IChip drvD = d == Signal.High ? new VccDriver(netD) : new GndDriver(netD);
        Hc20 gate = new(netA, netB, netC, netD, netY);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { drvA, drvB, drvC, drvD, (IChip)gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(expected, netY.Value);
    }

    [Fact]
    public void Low_input_forces_high_even_with_unknown_others()
    {
        Net a = new(1), b = new(2), c = new(3), d = new(4), y = new(5);
        GndDriver va = new(a);   // a = Low, b/c/d never driven (Unknown)
        Hc20 gate = new(a, b, c, d, y);

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
        Net a = new(1), b = new(2), c = new(3), d = new(4), y = new(5);
        Hc20 gate = new(a, b, c, d, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void Three_high_one_unknown_gives_unknown_output()
    {
        Net a = new(1), b = new(2), c = new(3), d = new(4), y = new(5);
        VccDriver da = new(a);
        VccDriver db = new(b);
        VccDriver dc = new(c);   // d floating -> can't decide between Low and High output
        Hc20 gate = new(a, b, c, d, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { da, db, dc, gate });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, y.Value);
    }

    [Fact]
    public void Output_settles_after_propagation_delay()
    {
        Net a = new(1), b = new(2), c = new(3), d = new(4), y = new(5);
        VccDriver da = new(a);
        VccDriver db = new(b);
        VccDriver dc = new(c);
        VccDriver dd = new(d);   // all High -> output should fall to Low after tPD
        Hc20 gate = new(a, b, c, d, y);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { da, db, dc, dd, gate });
        sim.Start();

        sim.RunUntil(Hc20.PropagationDelayPs - 1);
        Assert.NotEqual(Signal.Low, y.Value);

        sim.RunUntil(Hc20.PropagationDelayPs);
        Assert.Equal(Signal.Low, y.Value);
    }
}