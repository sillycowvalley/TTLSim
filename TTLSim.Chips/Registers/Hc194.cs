using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Registers;

/// <summary>
/// 74HC194 — 4-bit bidirectional universal shift register, 16-pin DIP.
/// The Mini Blinky TOS register: SHL/SHR/ASR/ROL/ROR in one package with
/// the ALU Rev 2 '153 serial-fill mux feeding DSR/DSL.
///
/// The mode inputs S1/S0 are sampled on each rising CLK edge (they are
/// pure edge-samples -- changing them between edges does nothing):
///   S1 S0 = 00  hold (existing data retained)
///   S1 S0 = 01  shift toward Q3: Q0 &lt;- DSR, Qn &lt;- Qn-1
///   S1 S0 = 10  shift toward Q0: Q3 &lt;- DSL, Qn &lt;- Qn+1
///   S1 S0 = 11  parallel load D0..D3
///
/// DIRECTION NAMING TRAP: the datasheets call mode 01 "shift right" and
/// mode 10 "shift left" -- names taken from the QA-first pinout drawing,
/// not from arithmetic. With Q3 as the MSB, the datasheet's "shift right"
/// (mode 01, toward Q3) DOUBLES the value, and its "shift left" (mode 10,
/// toward Q0) HALVES it. This model and its doc avoid the words and state
/// the data movement instead. (Nexperia's general-description paragraph
/// even contradicts its own function table on which serial pin pairs with
/// which mode; the function table and logic diagram -- DSR into FF1/Q0,
/// DSL into FF4/Q3 -- are the authority, and TI agrees.)
///
/// /CLR (the datasheet's MR, pin 1) is ASYNCHRONOUS and active LOW: it
/// forces Q0..Q3 LOW immediately, overriding the clock, and while held
/// low the clock cannot load or shift -- the '161's async-clear shape.
///
/// Inputs map Unknown/HighZ to Low, matching the catalogue convention
/// ("treat weird inputs as Low and let TTL011 surface the floating pin at
/// design time") -- so an undriven serial input shifts in 0 and an
/// undriven mode pin reads as 0.
/// </summary>
public sealed class Hc194 : IChip
{
    public const long PropagationDelayPs = 44_000;   // HC tCO, matches '161/'163/'191

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexClr = 0;   // /CLR (MR)  (pin 1)
    private const int IndexDsr = 1;   // DSR        (pin 2)   enters at Q0, mode 01
    private const int IndexD0 = 2;    // D0..D3     (pins 3..6)
    private const int IndexDsl = 6;   // DSL        (pin 7)   enters at Q3, mode 10
    private const int IndexS0 = 7;    // S0         (pin 9)
    private const int IndexS1 = 8;    // S1         (pin 10)
    private const int IndexClk = 9;   // CLK        (pin 11)
    private const int IndexQ0 = 10;   // Q0..Q3     (pins 15,14,13,12)

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[4];
    private readonly long delayPs;

    private int q;
    private Signal prevClk = Signal.Unknown;
    private bool clearAsserted;

    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private readonly string label;

    public Hc194(
        Net clrN, Net dsr,
        Net d0, Net d1, Net d2, Net d3,
        Net dsl, Net s0, Net s1, Net clkN,
        Net q0, Net q1, Net q2, Net q3,
        string label = "194",
        Microsoft.Extensions.Logging.ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        nets = new[]
        {
            clrN, dsr,
            d0, d1, d2, d3,
            dsl, s0, s1, clkN,
            q0, q1, q2, q3
        };
        for (int i = 0; i < 4; i++)
            qDrivers[i] = new Driver(nets[IndexQ0 + i], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order: /CLR(1), DSR(2), D0..D3(3..6), DSL(7),
    // S0(9), S1(10), CLK(11), Q0(15), Q1(14), Q2(13), Q3(12).
    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 15, 14, 13, 12 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        // Power-up contents are undefined on the real part; the simulator
        // starts at 0 (house convention, cf. the '161 and '670).
        q = 0;
        clearAsserted = nets[IndexClr].Value == Signal.Low;
        prevClk = nets[IndexClk].Value;
        EmitOutputs(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Mode, data, and serial inputs are pure edge-samples: a change on
        // any of them between clock edges has no effect, so only /CLR and
        // CLK transitions do anything here.
        if (pinIndex == IndexClr)
            HandleClearChange(scheduler);
        else if (pinIndex == IndexClk)
            HandleClockEdge(scheduler);
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
                EmitOutputs(scheduler);
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

        // While /CLR is held low, the async clear pins the register at 0 --
        // the clock can't load or shift past it.
        if (clearAsserted) return;

        int mode = (Bit(IndexS1) << 1) | Bit(IndexS0);
        int next = mode switch
        {
            0b00 => q,                                        // hold
            0b01 => ((q << 1) & 0xF) | Bit(IndexDsr),         // toward Q3, DSR in at Q0
            0b10 => (q >> 1) | (Bit(IndexDsl) << 3),          // toward Q0, DSL in at Q3
            _ => ReadDataInputs()                             // 0b11 parallel load
        };

        if (next != q)
        {
            logger.LogDebug("{Label} CLK rising (mode {Mode}): {Old:X1} -> {New:X1}",
                label, mode, q, next);
            q = next;
            EmitOutputs(scheduler);
        }
    }

    private int ReadDataInputs()
    {
        int v = 0;
        for (int i = 0; i < 4; i++)
            if (nets[IndexD0 + i].Value == Signal.High)
                v |= 1 << i;
        return v;
    }

    private int Bit(int index) => nets[index].Value == Signal.High ? 1 : 0;

    private void EmitOutputs(IScheduler scheduler)
    {
        for (int i = 0; i < 4; i++)
        {
            Signal bit = ((q >> i) & 1) != 0 ? Signal.High : Signal.Low;
            scheduler.Schedule(delayPs, qDrivers[i], bit);
        }
    }
}
