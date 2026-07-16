using TTLSim.Core;

namespace TTLSim.Chips.Buffers;

/// <summary>
/// One buffer section from a 74HC125 (or 74HC126) quad 3-state buffer.
/// Each of the four sections in the package is fully independent -- its own
/// A input, its own Y output, its own output enable -- so the factory
/// instantiates one of these per wired section, exactly as the gate ICs
/// instantiate one core per gate (see ChipFactory.CreateGateChip).
///
/// Enable polarity distinguishes the two parts: the '125 drives while its
/// /OE is LOW (enableActiveLow = true, the default); the '126 drives while
/// its OE is HIGH (enableActiveLow = false). In both cases the enable must
/// be SOLIDLY asserted for the output to drive -- an Unknown or HighZ
/// enable releases the output, the same convention as the '541's enables.
/// That is deliberately NOT the "unknown reads as Low" input convention:
/// a bus driver whose enable is indeterminate must not fight the bus, and
/// TTL011 surfaces the genuinely floating enable pin at design time. (TI
/// recommends a pull-up on a '125 /OE for exactly this power-up reason.)
///
/// When driving, Y follows A (High/Low pass through; Unknown or HighZ on A
/// drives Unknown). When released, Y is HighZ so the section composes with
/// pulls and with other tri-state drivers on a shared bus -- the whole
/// point of the part, and why it is deliberately absent from
/// TotemPoleParts. tPD ~14 ns (HC, typ).
/// </summary>
public sealed class Hc125 : IChip
{
    public const long PropagationDelayPs = 14_000;

    private readonly Net oe;
    private readonly Net a;
    private readonly Net y;
    private readonly Driver driver;
    private readonly bool enableActiveLow;
    private readonly long delayPs;

    public Hc125(Net oe, Net a, Net y,
        bool enableActiveLow = true,
        long delayPs = PropagationDelayPs)
    {
        this.oe = oe;
        this.a = a;
        this.y = y;
        driver = new Driver(y, DriveStrength.Strong);
        this.enableActiveLow = enableActiveLow;
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 0, 1, 2 };

    public IReadOnlyList<Net> Nets => new[] { oe, a, y };

    public void Initialize(IScheduler scheduler)
    {
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex == 2) return;   // the output's own transition
        scheduler.Schedule(delayPs, driver, ComputeOutput());
    }

    private Signal ComputeOutput()
    {
        // Enabled only on a solid assertion of the enable pin; High, Low
        // in the wrong polarity, Unknown, and HighZ all release the bus.
        Signal wanted = enableActiveLow ? Signal.Low : Signal.High;
        if (oe.Value != wanted) return Signal.HighZ;

        return a.Value switch
        {
            Signal.High => Signal.High,
            Signal.Low => Signal.Low,
            _ => Signal.Unknown,
        };
    }
}
