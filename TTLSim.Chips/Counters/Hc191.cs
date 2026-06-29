using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Counters;

/// <summary>
/// 74HC191 — presettable synchronous 4-bit binary UP/DOWN counter with a
/// single clock and a direction input, 16-pin DIP.
///
/// Three things differ from the '161/'163 and matter to the model:
///   • One CLK plus a direction pin D/U (LOW = count up, HIGH = count down),
///     rather than the '193's two separate up/down clocks.
///   • /LD is ASYNCHRONOUS and level-sensitive: while /LD is LOW the Q
///     outputs follow D0..D3 immediately, independent of the clock. Releasing
///     /LD HIGH freezes the loaded value and counting resumes from it. (On
///     the '161/'163, by contrast, load is synchronous to the clock edge.)
///   • Count enable is a single active-LOW /CTEN; there is NO clear pin.
///
/// Cascade outputs:
///   • MAX/MIN is HIGH at terminal count — 15 when counting up, 0 when
///     counting down.
///   • /RCO is an active-LOW ripple clock: LOW during the LOW half of CLK
///     while MAX/MIN is HIGH and /CTEN is LOW. It feeds the next stage's CLK.
///
/// Both cascade outputs are optional at the factory: a lone counter (e.g. the
/// Mini Blinky return-stack pointer) leaves pins 12 and 13 open and the
/// factory hands in throwaway stand-in nets that drive nothing.
///
/// Inputs map Unknown/HighZ to Low, matching the catalogue convention ("treat
/// weird inputs as Low and let TTL011 surface the floating pin at design
/// time").
/// </summary>
public sealed class Hc191 : IChip
{
    public const long PropagationDelayPs = 44_000;   // HC tCO, matches '161/'163

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexD0 = 0;      // D0      (pin 15)
    private const int IndexD1 = 1;      // D1      (pin 1)
    private const int IndexD2 = 2;      // D2      (pin 10)
    private const int IndexD3 = 3;      // D3      (pin 9)
    private const int IndexCten = 4;    // /CTEN   (pin 4)
    private const int IndexDu = 5;      // D/U     (pin 5)  LOW = up
    private const int IndexLd = 6;      // /LD     (pin 11) async load, active LOW
    private const int IndexClk = 7;     // CLK     (pin 14)
    private const int IndexQ0 = 8;      // Q0..Q3  (pins 3,2,6,7)
    private const int IndexMaxMin = 12; // MAX/MIN (pin 12)
    private const int IndexRco = 13;    // /RCO    (pin 13)

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[4];
    private readonly Driver maxMinDriver;
    private readonly Driver rcoDriver;
    private readonly long delayPs;

    private int count;
    private Signal prevClk = Signal.Unknown;
    private bool loadAsserted;

    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private readonly string label;

    public Hc191(
        Net d0, Net d1, Net d2, Net d3,
        Net ctenN, Net du, Net ldN, Net clkN,
        Net q0, Net q1, Net q2, Net q3,
        Net maxMinN, Net rcoN,
        string label = "191",
        Microsoft.Extensions.Logging.ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        nets = new[]
        {
            d0, d1, d2, d3,
            ctenN, du, ldN, clkN,
            q0, q1, q2, q3,
            maxMinN, rcoN
        };
        for (int i = 0; i < 4; i++)
            qDrivers[i] = new Driver(nets[IndexQ0 + i], DriveStrength.Strong);
        maxMinDriver = new Driver(nets[IndexMaxMin], DriveStrength.Strong);
        rcoDriver = new Driver(nets[IndexRco], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order: D0(15), D1(1), D2(10), D3(9),
    // /CTEN(4), D/U(5), /LD(11), CLK(14), Q0(3), Q1(2), Q2(6), Q3(7),
    // MAX/MIN(12), /RCO(13).
    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 15, 1, 10, 9, 4, 5, 11, 14, 3, 2, 6, 7, 12, 13 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        loadAsserted = nets[IndexLd].Value == Signal.Low;
        count = loadAsserted ? ReadDataInputs() : 0;
        prevClk = nets[IndexClk].Value;
        EmitOutputs(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        switch (pinIndex)
        {
            case IndexLd:
                HandleLoadChange(scheduler);
                break;

            case IndexD0:
            case IndexD1:
            case IndexD2:
            case IndexD3:
                // D inputs are don't-care except while /LD is held low, where
                // the load is transparent and the outputs track them.
                if (loadAsserted)
                {
                    int loaded = ReadDataInputs();
                    if (loaded != count)
                    {
                        count = loaded;
                        EmitOutputs(scheduler);
                    }
                }
                break;

            case IndexClk:
                HandleClockEdge(scheduler);
                break;

            case IndexCten:
            case IndexDu:
                // Neither changes the count on its own, but both feed the
                // MAX/MIN and /RCO cascade outputs, so re-emit.
                EmitOutputs(scheduler);
                break;
        }
    }

    private void HandleLoadChange(IScheduler scheduler)
    {
        Signal ld = nets[IndexLd].Value;
        if (ld == Signal.Low)
        {
            loadAsserted = true;
            count = ReadDataInputs();
            logger.LogDebug("{Label} /LD asserted -- async load {Value}", label, count);
            EmitOutputs(scheduler);
        }
        else
        {
            loadAsserted = false;
        }
    }

    private void HandleClockEdge(IScheduler scheduler)
    {
        Signal newClk = nets[IndexClk].Value;
        bool rising = prevClk == Signal.Low && newClk == Signal.High;
        prevClk = newClk;

        // The async load dominates the clock: while /LD is low the count is
        // pinned to the data inputs and a rising edge can't advance it.
        if (rising && !loadAsserted && nets[IndexCten].Value == Signal.Low)
        {
            bool down = nets[IndexDu].Value == Signal.High;   // D/U HIGH = down
            int next = down ? (count - 1) & 0xF : (count + 1) & 0xF;
            logger.LogDebug("{Label} CLK rising: {Old} -> {New} ({Dir})",
                label, count, next, down ? "down" : "up");
            count = next;
        }

        // Even a non-counting edge -- and the clock's FALLING edge -- changes
        // the /RCO gating (it tracks the low half of CLK), so always re-emit.
        EmitOutputs(scheduler);
    }

    private int ReadDataInputs()
    {
        int v = 0;
        if (nets[IndexD0].Value == Signal.High) v |= 1;
        if (nets[IndexD1].Value == Signal.High) v |= 2;
        if (nets[IndexD2].Value == Signal.High) v |= 4;
        if (nets[IndexD3].Value == Signal.High) v |= 8;
        return v;
    }

    private void EmitOutputs(IScheduler scheduler)
    {
        for (int i = 0; i < 4; i++)
        {
            Signal bit = ((count >> i) & 1) != 0 ? Signal.High : Signal.Low;
            scheduler.Schedule(delayPs, qDrivers[i], bit);
        }

        bool down = nets[IndexDu].Value == Signal.High;
        bool atTerminal = down ? count == 0 : count == 0xF;
        scheduler.Schedule(delayPs, maxMinDriver, atTerminal ? Signal.High : Signal.Low);

        // /RCO is active-LOW and pulses low during the low half of CLK while
        // the chip is at terminal count and enabled.
        bool enabled = nets[IndexCten].Value == Signal.Low;
        bool clkLow = nets[IndexClk].Value == Signal.Low;
        bool rcoLow = atTerminal && enabled && clkLow;
        scheduler.Schedule(delayPs, rcoDriver, rcoLow ? Signal.Low : Signal.High);
    }
}