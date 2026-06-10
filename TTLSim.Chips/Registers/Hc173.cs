using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Registers;

/// <summary>
/// 74LS173 — 4-bit D-type register with 3-state outputs.
///
/// Rising-edge clocked. Data-enable inputs G1, G2 gate the load: both must
/// be LOW at the rising edge for D0..D3 to be latched. Otherwise the
/// register holds. CLR is asynchronous and **active HIGH** on this part
/// (unusual for the family) — CLR=H zeroes the register immediately,
/// overriding the clock.
///
/// Output control M, N: both must be LOW for the Qs to drive their nets.
/// If either is HIGH, all four Qs go high-Z. M/N gate only the output
/// stage; the internal register continues to update under the clock as
/// normal, so the next time M and N both go LOW the latched value
/// appears on the bus.
/// </summary>
public sealed class Hc173 : IChip
{
    public const long PropagationDelayPs = 18_000;

    // Indices into nets[] -- the order PinNumbers is declared in.
    private const int IndexM = 0;    // M    (pin 1)
    private const int IndexN = 1;    // N    (pin 2)
    private const int IndexQ0 = 2;   // 1Q..4Q (pins 3,4,5,6)
    private const int IndexClk = 6;  // CLK  (pin 7)
    private const int IndexG1 = 7;   // G1   (pin 9)
    private const int IndexG2 = 8;   // G2   (pin 10)
    private const int IndexD0 = 9;   // 4D..1D in datasheet pin order (pins 11,12,13,14)
    private const int IndexClr = 13; // CLR  (pin 15)

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[4];
    private readonly long delayPs;

    /// <summary>The four bits currently latched in the register.</summary>
    private int latched;

    private Signal prevClk = Signal.Unknown;
    private bool clearAsserted;

    private readonly ILogger logger;
    private readonly string label;

    /// <summary>
    /// Construct the chip. Q0..Q3 are the 4-bit outputs corresponding to D0..D3.
    /// </summary>
    public Hc173(
        Net m, Net n,
        Net q0, Net q1, Net q2, Net q3,
        Net clkN,
        Net g1N, Net g2N,
        Net d0, Net d1, Net d2, Net d3,
        Net clr,
        string label = "173",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            m, n,
            q0, q1, q2, q3,
            clkN,
            g1N, g2N,
            // Datasheet pin numbering runs D0,D1,D2,D3 on pins 14,13,12,11
            // but we store them in nets[] in pin-number order (11,12,13,14)
            // which means d3 first, then d2, d1, d0.
            d3, d2, d1, d0,
            clr
        };
        for (int i = 0; i < 4; i++)
            qDrivers[i] = new Driver(nets[IndexQ0 + i], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order: M(1), N(2), 1Q..4Q(3..6), CLK(7),
    // G1(9), G2(10), D pins in ascending pin order (11..14), CLR(15).
    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        latched = 0;
        prevClk = nets[IndexClk].Value;
        clearAsserted = nets[IndexClr].Value == Signal.High;
        // Drive initial outputs according to whether M and N permit it.
        EmitOutputs(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex == IndexClr)
            HandleClearChange(scheduler);
        else if (pinIndex == IndexClk)
            HandleClockEdge(scheduler);
        else if (pinIndex == IndexM || pinIndex == IndexN)
            EmitOutputs(scheduler);
        // D and G inputs are sampled at the clock edge; no async action.
    }

    private void HandleClearChange(IScheduler scheduler)
    {
        // CLR is active HIGH on the '173 -- the bare name, no leading '/'.
        Signal clr = nets[IndexClr].Value;
        if (clr == Signal.High && !clearAsserted)
        {
            clearAsserted = true;
            logger.LogDebug("{Label} CLR asserted -- async clear to 0", label);
            if (latched != 0)
            {
                latched = 0;
                EmitOutputs(scheduler);
            }
        }
        else if (clr != Signal.High)
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

        // While CLR is high the async clear pins the register at 0 --
        // the clock can't load past it.
        if (clearAsserted) return;

        // Load only when BOTH data-enable inputs are LOW. Otherwise hold.
        bool loadEnabled =
            nets[IndexG1].Value == Signal.Low &&
            nets[IndexG2].Value == Signal.Low;
        if (!loadEnabled) return;

        int next = ReadDataInputs();
        if (next != latched)
        {
            logger.LogDebug("{Label} CLK rising: load {Old:X1} -> {New:X1}", label, latched, next);
            latched = next;
            EmitOutputs(scheduler);
        }
    }

    private int ReadDataInputs()
    {
        // D0 is the LSB. nets[IndexD0..IndexD0+3] holds d3,d2,d1,d0
        // (we stored them in pin-number ascending order; D0 is on pin 14
        // which sits at the end of the D block).
        int v = 0;
        // nets[IndexD0+0]=d3 (pin 11), nets[IndexD0+1]=d2 (pin 12),
        // nets[IndexD0+2]=d1 (pin 13), nets[IndexD0+3]=d0 (pin 14).
        if (nets[IndexD0 + 3].Value == Signal.High) v |= 1 << 0;  // D0
        if (nets[IndexD0 + 2].Value == Signal.High) v |= 1 << 1;  // D1
        if (nets[IndexD0 + 1].Value == Signal.High) v |= 1 << 2;  // D2
        if (nets[IndexD0 + 0].Value == Signal.High) v |= 1 << 3;  // D3
        return v;
    }

    /// <summary>
    /// Drive Q0..Q3 according to the latched value and the output-control
    /// state. If either M or N is HIGH all four outputs go high-Z; otherwise
    /// each Q drives the corresponding bit of the latched value.
    /// </summary>
    private void EmitOutputs(IScheduler scheduler)
    {
        bool outputsEnabled =
            nets[IndexM].Value == Signal.Low &&
            nets[IndexN].Value == Signal.Low;

        for (int i = 0; i < 4; i++)
        {
            Signal s = outputsEnabled
                ? (((latched >> i) & 1) != 0 ? Signal.High : Signal.Low)
                : Signal.HighZ;
            scheduler.Schedule(delayPs, qDrivers[i], s);
        }
    }
}