using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Registers;

/// <summary>
/// 74HC377 — octal D flip-flop with clock enable, 20-pin DIP.
///
/// Rising-edge clocked with a SYNCHRONOUS enable: on each rising edge of
/// CLK the chip samples /EN — if /EN is LOW the register latches D0..D7;
/// if /EN is HIGH the register holds. /EN has no asynchronous effect and
/// never gates the outputs; it only decides, edge by edge, whether the
/// register updates. No clear, no tri-state: the Q outputs always drive
/// (the '273 has the async clear instead; the '374 has the /OE tri-state
/// instead).
///
/// Pin map (from ChipPartDefinition.Ic74377 — the '273's D/Q interleave
/// with /EN on pin 1):
///   /EN=1                                    clock enable, active LOW, sampled at the edge
///   D0=3  D1=4  D2=7  D3=8                   data, low nibble
///   D4=13 D5=14 D6=17 D7=18                  data, high nibble
///   Q0=2  Q1=5  Q2=6  Q3=9                   outputs, low nibble
///   Q4=12 Q5=15 Q6=16 Q7=19                  outputs, high nibble
///   CLK=11                                   common clock, rising edge
///   VCC=20  GND=10                           power (consumed by the build pipeline)
///
/// Inputs map Unknown/HighZ to Low at the sampling edge, matching the
/// catalogue convention ("treat weird inputs as Low and let TTL011 surface
/// the floating pin at design time"). An unresolved /EN therefore reads as
/// asserted (loading enabled).
/// </summary>
public sealed class Hc377 : IChip
{
    public const long PropagationDelayPs = 15_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexEn = 0;   // /EN    (pin 1)
    private const int IndexClk = 1;  // CLK    (pin 11)
    private const int IndexD0 = 2;   // D0..D7 (pins 3,4,7,8,13,14,17,18)
    private const int IndexQ0 = 10;  // Q0..Q7 (pins 2,5,6,9,12,15,16,19)

    private readonly Net[] nets;
    private readonly Driver[] qDrivers = new Driver[8];
    private readonly long delayPs;

    /// <summary>The eight bits currently latched in the register.</summary>
    private int latched;

    private Signal prevClk = Signal.Unknown;

    private readonly ILogger logger;
    private readonly string label;

    public Hc377(
        Net enN, Net clkN,
        Net d0, Net d1, Net d2, Net d3, Net d4, Net d5, Net d6, Net d7,
        Net q0, Net q1, Net q2, Net q3, Net q4, Net q5, Net q6, Net q7,
        string label = "377",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            enN, clkN,
            d0, d1, d2, d3, d4, d5, d6, d7,
            q0, q1, q2, q3, q4, q5, q6, q7
        };
        for (int i = 0; i < 8; i++)
            qDrivers[i] = new Driver(nets[IndexQ0 + i], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order: /EN, CLK, D0..D7, Q0..Q7.
    public IReadOnlyList<int> PinNumbers { get; } = new[]
    {
        1, 11,
        3, 4, 7, 8, 13, 14, 17, 18,
        2, 5, 6, 9, 12, 15, 16, 19
    };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        latched = 0;
        prevClk = nets[IndexClk].Value;
        EmitOutputs(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (pinIndex == IndexClk)
            HandleClockEdge(scheduler);
        // /EN and the D inputs are sampled at the clock edge -- no
        // asynchronous action. Q indices are our own drive -- ignore.
    }

    private void HandleClockEdge(IScheduler scheduler)
    {
        Signal newClk = nets[IndexClk].Value;
        bool rising = prevClk == Signal.Low && newClk == Signal.High;
        prevClk = newClk;
        if (!rising) return;

        // /EN sampled at the edge: HIGH means hold this cycle. Anything
        // else (Low, or unresolved per the catalogue convention) loads.
        if (nets[IndexEn].Value == Signal.High)
        {
            logger.LogDebug("{Label} CLK rising: /EN high -- hold 0x{Value:X2}", label, latched);
            return;
        }

        int next = ReadDataInputs();
        if (next != latched)
        {
            latched = next;
            logger.LogDebug("{Label} CLK rising: latched 0x{Value:X2}", label, latched);
            EmitOutputs(scheduler);
        }
    }

    private int ReadDataInputs()
    {
        // A pin contributes 1 only when solidly High; Low/Unknown/HighZ all
        // read as 0, per the catalogue convention.
        int v = 0;
        for (int i = 0; i < 8; i++)
            if (nets[IndexD0 + i].Value == Signal.High)
                v |= 1 << i;
        return v;
    }

    private void EmitOutputs(IScheduler scheduler)
    {
        for (int i = 0; i < 8; i++)
        {
            Signal bit = ((latched >> i) & 1) != 0 ? Signal.High : Signal.Low;
            scheduler.Schedule(delayPs, qDrivers[i], bit);
        }
    }
}