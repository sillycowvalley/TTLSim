using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class PullDriverTests
{
    [Fact]
    public void Pullup_alone_drives_net_high()
    {
        Net n = new(1);
        PullDriver pull = new(n, Signal.High);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { (IChip)pull });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, n.Value);
    }

    [Fact]
    public void Strong_low_overrides_pullup()
    {
        Net n = new(1);
        PullDriver pull = new(n, Signal.High);     // weak pull-up
        GndDriver gnd = new(n);                     // strong Low (e.g. button to ground)

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { pull, gnd });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, n.Value);
    }

    [Fact]
    public void Pulldown_alone_drives_net_low()
    {
        Net n = new(1);
        PullDriver pull = new(n, Signal.Low);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new[] { (IChip)pull });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, n.Value);
    }
}