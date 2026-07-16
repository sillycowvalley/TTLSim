using TTLSim.Chips.Buffers;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Sources;
using TTLSim.Core;

/// <summary>
/// Tests for the Hc125 single-section tri-state buffer ('125 active-LOW
/// enable, '126 active-HIGH). The release cases follow the Hc541 test
/// pattern: a weak pull on Y that only wins when the buffer has genuinely
/// let go of the bus.
/// </summary>
public class Hc125Tests
{
    private static void Run(IChip chip, params IChip[] drivers)
    {
        IChip[] all = new IChip[drivers.Length + 1];
        System.Array.Copy(drivers, all, drivers.Length);
        all[drivers.Length] = chip;

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), all);
        sim.Start();
        sim.RunUntilQuiescent();
    }

    [Theory]
    [InlineData(Signal.High)]
    [InlineData(Signal.Low)]
    public void Hc125_passes_input_when_oe_low(Signal a)
    {
        Net nOe = new(1), nA = new(2), nY = new(3);
        Hc125 buf = new(nOe, nA, nY);
        IChip oe = new GndDriver(nOe);   // /OE = Low -> enabled
        IChip da = a == Signal.High ? new VccDriver(nA) : new GndDriver(nA);
        Run(buf, oe, da);
        Assert.Equal(a, nY.Value);
    }

    [Fact]
    public void Hc125_releases_bus_when_oe_high()
    {
        Net nOe = new(1), nA = new(2), nY = new(3);
        Hc125 buf = new(nOe, nA, nY);
        IChip oe = new VccDriver(nOe);                  // /OE = High -> high-Z
        IChip da = new VccDriver(nA);                   // A high regardless
        IChip pull = new PullDriver(nY, Signal.Low);    // weak pull-down on Y
        Run(buf, oe, da, pull);
        Assert.Equal(Signal.Low, nY.Value);             // pull wins -> released
    }

    // An enable that is not solidly asserted must release the bus -- the
    // '541 convention, not the "unknown reads as Low" input convention.
    // Here /OE is left entirely undriven (Unknown).
    [Fact]
    public void Hc125_releases_bus_when_oe_undriven()
    {
        Net nOe = new(1), nA = new(2), nY = new(3);
        Hc125 buf = new(nOe, nA, nY);
        IChip da = new VccDriver(nA);
        IChip pull = new PullDriver(nY, Signal.Low);
        Run(buf, da, pull);
        Assert.Equal(Signal.Low, nY.Value);
    }

    [Theory]
    [InlineData(Signal.High)]
    [InlineData(Signal.Low)]
    public void Hc126_passes_input_when_oe_high(Signal a)
    {
        Net nOe = new(1), nA = new(2), nY = new(3);
        Hc125 buf = new(nOe, nA, nY, enableActiveLow: false);   // '126
        IChip oe = new VccDriver(nOe);   // OE = High -> enabled
        IChip da = a == Signal.High ? new VccDriver(nA) : new GndDriver(nA);
        Run(buf, oe, da);
        Assert.Equal(a, nY.Value);
    }

    [Fact]
    public void Hc126_releases_bus_when_oe_low()
    {
        Net nOe = new(1), nA = new(2), nY = new(3);
        Hc125 buf = new(nOe, nA, nY, enableActiveLow: false);   // '126
        IChip oe = new GndDriver(nOe);                  // OE = Low -> high-Z
        IChip da = new VccDriver(nA);
        IChip pull = new PullDriver(nY, Signal.High);   // weak pull-UP on Y
        Run(buf, oe, da, pull);
        Assert.Equal(Signal.High, nY.Value);            // pull wins -> released
    }

    // Two sections wired to one bus net behind exclusive enables -- the
    // Mini Blinky write-buffer shape. The enabled section owns the bus;
    // the disabled one must not disturb it.
    [Theory]
    [InlineData(Signal.High)]
    [InlineData(Signal.Low)]
    public void Two_sections_share_a_bus_behind_exclusive_enables(Signal driven)
    {
        Net bus = new(1);
        Net oeA = new(2), aA = new(3);
        Net oeB = new(4), aB = new(5);

        Hc125 secA = new(oeA, aA, bus);
        Hc125 secB = new(oeB, aB, bus);

        IChip enA = new GndDriver(oeA);   // section A enabled
        IChip disB = new VccDriver(oeB);  // section B released
        IChip dA = driven == Signal.High ? new VccDriver(aA) : new GndDriver(aA);
        IChip dB = driven == Signal.High ? new GndDriver(aB) : new VccDriver(aB);   // opposite data

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { enA, disB, dA, dB, secA, secB });
        sim.Start();
        sim.RunUntilQuiescent();

        Assert.Equal(driven, bus.Value);   // A's data, B silent
    }

    [Fact]
    public void Output_settles_after_propagation_delay()
    {
        Net nOe = new(1), nA = new(2), nY = new(3);
        GndDriver oe = new(nOe);   // enabled
        VccDriver da = new(nA);    // A high -> Y should reach High after tPD
        Hc125 buf = new(nOe, nA, nY);

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { oe, da, buf });
        sim.Start();

        sim.RunUntil(Hc125.PropagationDelayPs - 1);
        Assert.NotEqual(Signal.High, nY.Value);

        sim.RunUntil(Hc125.PropagationDelayPs);
        Assert.Equal(Signal.High, nY.Value);
    }
}
