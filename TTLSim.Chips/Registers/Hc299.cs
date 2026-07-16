using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Registers;

/// <summary>
/// 74HC299 — 8-bit universal shift register with 3-state multiplexed I/O,
/// 20-pin DIP. The UART TX/RX shift core: eight bits of shift/hold/load in
/// one package, with the parallel bits sharing pins as bus-driver outputs
/// OR load inputs, plus dedicated always-driving serial taps Q0 (pin 8)
/// and Q7 (pin 17) for cascading.
///
/// Register operations are sampled on the rising CP edge, '194-style
/// (identical mode encoding, verified against the Nexperia function
/// table):
///   S1 S0 = 00  hold
///   S1 S0 = 01  shift toward Q7: Q0 &lt;- DSR, Qn &lt;- Qn-1
///   S1 S0 = 10  shift toward Q0: Q7 &lt;- DSL, Qn &lt;- Qn+1
///   S1 S0 = 11  parallel load from the I/O pins
///
/// THE PART'S DEFINING BEHAVIOUR is that the I/O buffer enable is
/// COMBINATIONAL while the register is edge-triggered. The eight I/O
/// drivers are on only when /OE1 = /OE2 = LOW **and** the mode is not
/// 11 — driving S1 = S0 = HIGH releases the bus immediately, level-
/// sensitive, "in preparation for a parallel load" (datasheet wording),
/// before any clock edge arrives. At the load edge the chip then reads
/// its own I/O nets, which external bus drivers own at that moment.
/// Shift, hold, load, and reset all still operate with the buffers
/// disabled; the Q0/Q7 taps drive at all times.
///
/// /CLR (the datasheet's MR, pin 9) is ASYNCHRONOUS and active LOW:
/// forces the register to 0 immediately and pins it there against the
/// clock — the '161 shape.
///
/// Conventions: register-path inputs (S, DSR, DSL, and the I/O nets when
/// read for a load) map Unknown/HighZ to Low; the OE enables follow the
/// '541/'125 tri-state convention instead (the buffers drive only on a
/// solid double-LOW — an indeterminate enable must not fight the bus),
/// and the S1·S0 disable term fires only on a solid double-HIGH, so an
/// unknown mode reads as hold for the buffers exactly as it does for the
/// register. Deliberately absent from TotemPoleParts: the I/O pins are
/// tri-state bus drivers.
///
/// PIN-MAP NOTE (2026-07): DSR is pin 11 and CP is pin 12 — the part
/// definition originally had them transposed; verified against Nexperia
/// Table 2 and fixed in the same change that added this model.
/// </summary>
public sealed class Hc299 : IChip
{
    public const long PropagationDelayPs = 60_000;   // HC CP->Q max, full temp range

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexS0 = 0;    // S0    (pin 1)
    private const int IndexOe1 = 1;   // /OE1  (pin 2)
    private const int IndexOe2 = 2;   // /OE2  (pin 3)
    private const int IndexClr = 3;   // /CLR  (pin 9, datasheet MR)
    private const int IndexDsr = 4;   // DSR   (pin 11)
    private const int IndexClk = 5;   // CP    (pin 12)
    private const int IndexDsl = 6;   // DSL   (pin 18)
    private const int IndexS1 = 7;    // S1    (pin 19)
    private const int IndexIo0 = 8;   // I/O0..I/O7 (pins 7,13,6,14,5,15,4,16)
    private const int IndexQ0Tap = 16;  // Q0 serial tap (pin 8)
    private const int IndexQ7Tap = 17;  // Q7 serial tap (pin 17)

    private readonly Net[] nets;
    private readonly Driver[] ioDrivers = new Driver[8];
    private readonly Driver q0TapDriver;
    private readonly Driver q7TapDriver;
    private readonly long delayPs;

    private int q;
    private Signal prevClk = Signal.Unknown;
    private bool clearAsserted;

    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private readonly string label;

    public Hc299(
        Net s0, Net oe1N, Net oe2N, Net clrN,
        Net dsr, Net clkN, Net dsl, Net s1,
        Net io0, Net io1, Net io2, Net io3,
        Net io4, Net io5, Net io6, Net io7,
        Net q0Tap, Net q7Tap,
        string label = "299",
        Microsoft.Extensions.Logging.ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        nets = new[]
        {
            s0, oe1N, oe2N, clrN,
            dsr, clkN, dsl, s1,
            io0, io1, io2, io3, io4, io5, io6, io7,
            q0Tap, q7Tap
        };
        for (int i = 0; i < 8; i++)
            ioDrivers[i] = new Driver(nets[IndexIo0 + i], DriveStrength.Strong);
        q0TapDriver = new Driver(nets[IndexQ0Tap], DriveStrength.Strong);
        q7TapDriver = new Driver(nets[IndexQ7Tap], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order: S0(1), /OE1(2), /OE2(3), /CLR(9),
    // DSR(11), CP(12), DSL(18), S1(19), I/O0..I/O7 (7,13,6,14,5,15,4,16),
    // Q0(8), Q7(17).
    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 9, 11, 12, 18, 19, 7, 13, 6, 14, 5, 15, 4, 16, 8, 17 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        // Power-up contents are undefined on the real part; the simulator
        // starts at 0 (house convention, cf. the '161/'194/'670).
        q = 0;
        clearAsserted = nets[IndexClr].Value == Signal.Low;
        prevClk = nets[IndexClk].Value;
        EmitAll(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        switch (pinIndex)
        {
            case IndexClr:
                HandleClearChange(scheduler);
                break;

            case IndexClk:
                HandleClockEdge(scheduler);
                break;

            case IndexS0:
            case IndexS1:
            case IndexOe1:
            case IndexOe2:
                // The buffer enable is COMBINATIONAL: mode or OE changes
                // re-resolve the I/O drivers immediately, no clock needed.
                EmitIo(scheduler);
                break;

            // I/O net changes (external bus activity, or our own drivers
            // settling) and the taps' own transitions are ignored here --
            // the I/O nets are only READ at a load edge.
        }
    }

    private void HandleClearChange(IScheduler scheduler)
    {
        Signal clr = nets[IndexClr].Value;
        if (clr == Signal.Low && !clearAsserted)
        {
            clearAsserted = true;
            logger.LogDebug("{Label} /CLR asserted -- async clear to 0", label);
            if (q != 0)
            {
                q = 0;
                EmitAll(scheduler);
            }
        }
        else if (clr != Signal.Low)
        {
            clearAsserted = false;
        }
    }

    private void HandleClockEdge(IScheduler scheduler)
    {
        Signal newClk = nets[IndexClk].Value;
        bool rising = prevClk == Signal.Low && newClk == Signal.High;
        prevClk = newClk;
        if (!rising) return;

        // While /CLR is held low, the async clear pins the register at 0.
        if (clearAsserted) return;

        int mode = (Bit(IndexS1) << 1) | Bit(IndexS0);
        int next = mode switch
        {
            0b00 => q,                                        // hold
            0b01 => ((q << 1) & 0xFF) | Bit(IndexDsr),        // toward Q7, DSR in at Q0
            0b10 => (q >> 1) | (Bit(IndexDsl) << 7),          // toward Q0, DSL in at Q7
            _ => ReadIoNets()                                 // 0b11 parallel load
        };

        if (next != q)
        {
            logger.LogDebug("{Label} CP rising (mode {Mode}): {Old:X2} -> {New:X2}",
                label, mode, q, next);
            q = next;
            EmitAll(scheduler);
        }
    }

    /// <summary>
    /// Read the eight I/O nets as load data. The chip's own drivers are
    /// high-Z at this moment (S1=S0=HIGH disabled them combinationally),
    /// so the values seen are whatever the external bus drives; an
    /// undriven pin reads as 0 per the catalogue input convention.
    /// </summary>
    private int ReadIoNets()
    {
        int v = 0;
        for (int i = 0; i < 8; i++)
            if (nets[IndexIo0 + i].Value == Signal.High)
                v |= 1 << i;
        return v;
    }

    private int Bit(int index) => nets[index].Value == Signal.High ? 1 : 0;

    /// <summary>
    /// The combinational buffer-enable term: both /OEs solidly LOW and the
    /// mode not solidly 11. Anything unresolved on an /OE releases the bus
    /// ('541/'125 convention); anything unresolved on S counts as not-11.
    /// </summary>
    private bool IoBuffersEnabled() =>
        nets[IndexOe1].Value == Signal.Low &&
        nets[IndexOe2].Value == Signal.Low &&
        !(nets[IndexS1].Value == Signal.High && nets[IndexS0].Value == Signal.High);

    private void EmitIo(IScheduler scheduler)
    {
        bool enabled = IoBuffersEnabled();
        for (int i = 0; i < 8; i++)
        {
            Signal s = enabled
                ? (((q >> i) & 1) != 0 ? Signal.High : Signal.Low)
                : Signal.HighZ;
            scheduler.Schedule(delayPs, ioDrivers[i], s);
        }
    }

    private void EmitAll(IScheduler scheduler)
    {
        EmitIo(scheduler);
        scheduler.Schedule(delayPs, q0TapDriver,
            (q & 0x01) != 0 ? Signal.High : Signal.Low);
        scheduler.Schedule(delayPs, q7TapDriver,
            (q & 0x80) != 0 ? Signal.High : Signal.Low);
    }
}
