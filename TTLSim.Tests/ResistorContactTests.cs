using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class ResistorContactTests
{
    [Fact]
    public void Strong_high_on_one_end_passes_to_the_other()
    {
        Net a = new(1), b = new(2);
        ResistorContact r = new(a, b);
        VccDriver vcc = new(a);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { r, vcc });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, a.Value);
        Assert.Equal(Signal.High, b.Value);
    }

    [Fact]
    public void Strong_low_passes_through()
    {
        Net a = new(1), b = new(2);
        ResistorContact r = new(a, b);
        GndDriver gnd = new(b);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { r, gnd });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Low, a.Value);
        Assert.Equal(Signal.Low, b.Value);
    }

    [Fact]
    public void Both_ends_floating_stays_unknown()
    {
        // With nothing strongly or weakly driving either net, the resistor
        // contributes HighZ on both sides -- and HighZ-to-HighZ on a driver
        // is a no-op in ApplyEvent, so the engine never re-resolves the nets
        // away from their initial Unknown state. This is honest: the sim
        // hasn't been told what value either net should take.
        Net a = new(1), b = new(2);
        ResistorContact r = new(a, b);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { r });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.Unknown, a.Value);
        Assert.Equal(Signal.Unknown, b.Value);
    }

    [Fact]
    public void Weak_pull_on_one_end_passes_as_weak_to_the_other()
    {
        // A weak pull-up on side A should propagate through the resistor as
        // weak too, so any strong driver on side B still wins. This mirrors
        // SwitchInput's strength-passing behaviour.
        Net a = new(1), b = new(2);
        ResistorContact r = new(a, b);
        PullDriver pullup = new(a, Signal.High);
        GndDriver gnd = new(b);   // strong low on the far side

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { r, pullup, gnd });
        sim.Start();
        sim.RunUntilQuiescent();

        // Strong low on B should beat the weak high propagated through R.
        Assert.Equal(Signal.Low, a.Value);
        Assert.Equal(Signal.Low, b.Value);
    }

    [Fact]
    public void Diode_through_resistor_to_clock_pulls_clean_levels()
    {
        // The motivating case for this class: an LED-equivalent net sits
        // between a diode (anode-pumped weak high) and a series resistor
        // whose far end is a strongly-driven signal. Confirm the middle node
        // gets the strong driver's value, not HighZ.
        Net diodeAnode = new(1), middle = new(2), farEnd = new(3);

        VccDriver vcc = new(diodeAnode);
        DiodeContact diode = new(diodeAnode, middle);
        ResistorContact r = new(middle, farEnd);
        GndDriver gnd = new(farEnd);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { vcc, diode, r, gnd });
        sim.Start();
        sim.RunUntilQuiescent();

        // Diode pushes weak high on middle; resistor passes strong low from
        // farEnd back to middle; strong wins. So middle resolves Low.
        Assert.Equal(Signal.Low, middle.Value);
        Assert.Equal(Signal.Low, farEnd.Value);
    }
}