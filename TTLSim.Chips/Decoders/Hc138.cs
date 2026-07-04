using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Decoders;

/// <summary>
/// 74HC138 — 3-to-8 line decoder/demultiplexer, 16-pin DIP. A2:A0 (A0 the
/// LSB) select which of the eight active-low outputs goes LOW; the other
/// seven stay HIGH. Decoding requires all three enables satisfied at once:
/// /E1 LOW, /E2 LOW, and E3 HIGH. Any enable de-asserted forces all eight
/// outputs HIGH. Outputs always drive (no tri-state).
///
/// Pin map (from ChipPartDefinition.Ic74138):
///   A0=1  A1=2  A2=3  /E1=4  /E2=5  E3=6
///   /Y7=7  /Y6=9  /Y5=10  /Y4=11  /Y3=12  /Y2=13  /Y1=14  /Y0=15
///   VCC=16  GND=8   power (consumed by the build pipeline)
///
/// Inputs map Unknown/HighZ to Low (catalogue convention) — unresolved /E1
/// and /E2 therefore read as asserted, an unresolved E3 reads as
/// de-asserted (chip disabled, all outputs HIGH), and unresolved selects
/// address Y0. TTL011 surfaces genuinely floating pins at design time.
/// </summary>
public sealed class Hc138 : IChip
{
    public const long PropagationDelayPs = 38_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexA0 = 0;    // A0  (pin 1)
    private const int IndexA1 = 1;    // A1  (pin 2)
    private const int IndexA2 = 2;    // A2  (pin 3)
    private const int IndexE1N = 3;   // /E1 (pin 4)
    private const int IndexE2N = 4;   // /E2 (pin 5)
    private const int IndexE3 = 5;    // E3  (pin 6)
    private const int IndexY7 = 6;    // /Y7 (pin 7)  output
    private const int IndexY6 = 7;    // /Y6 (pin 9)  output
    private const int IndexY5 = 8;    // /Y5 (pin 10) output
    private const int IndexY4 = 9;    // /Y4 (pin 11) output
    private const int IndexY3 = 10;   // /Y3 (pin 12) output
    private const int IndexY2 = 11;   // /Y2 (pin 13) output
    private const int IndexY1 = 12;   // /Y1 (pin 14) output
    private const int IndexY0 = 13;   // /Y0 (pin 15) output

    private readonly Net[] nets;

    // drivers[i] drives /Yi. Output nets sit at indices IndexY0 - i
    // (Y0 highest index, Y7 lowest) because the pins run 15 down to 7.
    private readonly Driver[] drivers = new Driver[8];
    private readonly long delayPs;

    private readonly ILogger logger;
    private readonly string label;

    public Hc138(
        Net a0, Net a1, Net a2,
        Net e1N, Net e2N, Net e3,
        Net y7N, Net y6N, Net y5N, Net y4N,
        Net y3N, Net y2N, Net y1N, Net y0N,
        string label = "138",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            a0, a1, a2, e1N, e2N, e3,
            y7N, y6N, y5N, y4N, y3N, y2N, y1N, y0N
        };

        for (int i = 0; i < 8; i++)
            drivers[i] = new Driver(nets[IndexY0 - i], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Recompute(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Outputs occupy the contiguous index range IndexY7..IndexY0
        // (6..13); changes there are our own feedback, not inputs.
        if (pinIndex >= IndexY7) return;
        Recompute(scheduler);
    }

    private void Recompute(IScheduler scheduler)
    {
        int sel = (High(IndexA2) ? 4 : 0)
                | (High(IndexA1) ? 2 : 0)
                | (High(IndexA0) ? 1 : 0);

        // All three enables must be satisfied: /E1 LOW, /E2 LOW, E3 HIGH.
        bool enabled = !High(IndexE1N) && !High(IndexE2N) && High(IndexE3);

        logger.LogDebug("{Label} en={Enabled} sel={Sel}", label, enabled, sel);

        for (int i = 0; i < 8; i++)
        {
            Signal y = enabled && i == sel ? Signal.Low : Signal.High;
            scheduler.Schedule(delayPs, drivers[i], y);
        }
    }

    private bool High(int index) => nets[index].Value == Signal.High;
}