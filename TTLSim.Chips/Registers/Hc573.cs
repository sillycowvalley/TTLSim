using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Registers;

/// <summary>
/// 74HC573 — octal D-type TRANSPARENT latch with 3-state outputs, 20-pin
/// DIP, flow-through pinout. The '373's straight-across sibling and the
/// '574's level-sensitive one: same pin frame, but pin 11 is LE (latch
/// enable, active HIGH), not a clock, and nothing here is edge-triggered.
///
/// While LE is HIGH the latch is TRANSPARENT: every Q follows its D
/// combinationally, changing whenever D changes. On the HIGH-to-LOW
/// transition of LE the latch stores whatever the D inputs held at that
/// moment; while LE is LOW, D changes are ignored. This is the same
/// level-sensitive behaviour class as the '670's write port, and it
/// carries the same design discipline: whatever is on D when the latch
/// closes is what you keep, and an open latch smears live data straight
/// through to the outputs.
///
/// /OE (pin 1) is fully independent of the latch: HIGH releases all
/// eight Qs to high-Z while the latch keeps operating underneath —
/// re-enabling reveals whatever the latch currently holds, including
/// data that flowed through or was captured while the bus was released.
///
/// Conventions: D and LE map Unknown/HighZ to Low — an unresolved LE
/// therefore reads as LATCHED (hold), the safe direction. /OE follows
/// the '374's convention for the octal-register family: unknown reads
/// as asserted, outputs driving. (Note this is the opposite polarity
/// choice from the '541/'125/'299 solid-assertion enable convention —
/// the '573 sides with its '374 sibling so the two parts behave
/// identically in the same bus socket.) Deliberately absent from
/// TotemPoleParts: tri-state outputs.
///
/// Pin map (from ChipPartDefinition.Ic74573, verified against Nexperia
/// Table 2 — the '574 flow-through frame, D(k) opposite Q(k)):
///   /OE=1                                    output enable, active LOW
///   D0..D7 = pins 2..9                       data, straight down the left
///   LE=11                                    latch enable, active HIGH, level-sensitive
///   Q0..Q7 = pins 19..12                     outputs, each opposite its D
///   VCC=20  GND=10                           power (consumed by the build pipeline)
/// </summary>
public sealed class Hc573 : IChip
{
    public const long PropagationDelayPs = 18_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexOe = 0;   // /OE    (pin 1)
    private const int IndexLe = 1;   // LE     (pin 11)
    private const int IndexD0 = 2;   // D0..D7 (pins 2..9)
    private const int IndexQ0 = 10;  // Q0..Q7 (pins 19..12)

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[8];
    private readonly long delayPs;

    /// <summary>The eight bits currently held (tracking D while transparent).</summary>
    private int latched;

    private readonly ILogger logger;
    private readonly string label;

    public Hc573(
        Net oeN, Net le,
        Net d0, Net d1, Net d2, Net d3, Net d4, Net d5, Net d6, Net d7,
        Net q0, Net q1, Net q2, Net q3, Net q4, Net q5, Net q6, Net q7,
        string label = "573",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            oeN, le,
            d0, d1, d2, d3, d4, d5, d6, d7,
            q0, q1, q2, q3, q4, q5, q6, q7
        };
        for (int i = 0; i < 8; i++)
            qDrivers[i] = new Driver(nets[IndexQ0 + i], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order: /OE(1), LE(11), D0..D7(2..9), Q0..Q7(19..12).
    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 11, 2, 3, 4, 5, 6, 7, 8, 9, 19, 18, 17, 16, 15, 14, 13, 12 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        // Power-up contents are undefined on the real part; the simulator
        // starts at 0 (house convention) -- unless the latch is already
        // OPEN at build time, in which case transparency wins immediately.
        latched = Transparent() ? ReadDataInputs() : 0;
        EmitOutputs(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        switch (pinIndex)
        {
            case IndexLe:
                // Opening the latch resyncs it to D immediately; closing it
                // needs no action -- `latched` already tracked D while open,
                // so the HIGH-to-LOW transition simply stops the tracking.
                if (Transparent()) TrackDataInputs(scheduler);
                break;

            case IndexOe:
                // The enable is combinational and independent of the latch.
                EmitOutputs(scheduler);
                break;

            default:
                // D changes flow through only while the latch is open;
                // the '191 async-load pattern. Q transitions land here too
                // and fall through the Transparent() check harmlessly only
                // if LE is low, so exclude them explicitly.
                if (pinIndex >= IndexD0 && pinIndex < IndexQ0 && Transparent())
                    TrackDataInputs(scheduler);
                break;
        }
    }

    private bool Transparent() => nets[IndexLe].Value == Signal.High;

    private void TrackDataInputs(IScheduler scheduler)
    {
        int now = ReadDataInputs();
        if (now != latched)
        {
            logger.LogDebug("{Label} transparent: {Old:X2} -> {New:X2}",
                label, latched, now);
            latched = now;
            EmitOutputs(scheduler);
        }
    }

    private int ReadDataInputs()
    {
        int v = 0;
        for (int i = 0; i < 8; i++)
            if (nets[IndexD0 + i].Value == Signal.High)
                v |= 1 << i;
        return v;
    }

    private void EmitOutputs(IScheduler scheduler)
    {
        // '374-family enable convention: released only on a solid HIGH;
        // Low/Unknown/HighZ on /OE all read as asserted (driving).
        bool released = nets[IndexOe].Value == Signal.High;
        for (int i = 0; i < 8; i++)
        {
            Signal s = released
                ? Signal.HighZ
                : (((latched >> i) & 1) != 0 ? Signal.High : Signal.Low);
            scheduler.Schedule(delayPs, qDrivers[i], s);
        }
    }
}
