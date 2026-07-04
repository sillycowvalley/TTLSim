using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Decoders;

/// <summary>
/// 74HC154 — 4-to-16 line decoder/demultiplexer, 24-pin DIP. A3:A0 (A0 the
/// LSB) select which of the sixteen active-low outputs goes LOW; the other
/// fifteen stay HIGH. Both enables /E0 and /E1 must be LOW for any output
/// to assert; either HIGH forces all sixteen outputs HIGH. Outputs always
/// drive (no tri-state). Full address decoding in one chip without
/// cascading '138s.
///
/// Pin map (from ChipPartDefinition.Ic74154):
///   /Y0=1 /Y1=2 /Y2=3 /Y3=4 /Y4=5 /Y5=6 /Y6=7 /Y7=8 /Y8=9 /Y9=10 /Y10=11
///   /Y11=13 /Y12=14 /Y13=15 /Y14=16 /Y15=17
///   /E0=18  /E1=19  A3=20  A2=21  A1=22  A0=23
///   VCC=24  GND=12   power (consumed by the build pipeline)
///
/// Inputs map Unknown/HighZ to Low (catalogue convention) — unresolved
/// enables therefore read as asserted, and unresolved selects address Y0.
/// TTL011 surfaces genuinely floating pins at design time.
/// </summary>
public sealed class Hc154 : IChip
{
    public const long PropagationDelayPs = 45_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    // Outputs first: /Y0../Y15 occupy indices 0..15 (pins 1..11, 13..17).
    private const int IndexY0 = 0;    // /Y0  (pin 1)  output
    private const int IndexY15 = 15;  // /Y15 (pin 17) output
    private const int IndexE0N = 16;  // /E0  (pin 18)
    private const int IndexE1N = 17;  // /E1  (pin 19)
    private const int IndexA3 = 18;   // A3   (pin 20)
    private const int IndexA2 = 19;   // A2   (pin 21)
    private const int IndexA1 = 20;   // A1   (pin 22)
    private const int IndexA0 = 21;   // A0   (pin 23)

    private readonly Net[] nets;

    // drivers[i] drives /Yi; output nets sit at indices IndexY0 + i.
    private readonly Driver[] drivers = new Driver[16];
    private readonly long delayPs;

    private readonly ILogger logger;
    private readonly string label;

    public Hc154(
        Net y0N, Net y1N, Net y2N, Net y3N,
        Net y4N, Net y5N, Net y6N, Net y7N,
        Net y8N, Net y9N, Net y10N, Net y11N,
        Net y12N, Net y13N, Net y14N, Net y15N,
        Net e0N, Net e1N,
        Net a3, Net a2, Net a1, Net a0,
        string label = "154",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            y0N, y1N, y2N, y3N, y4N, y5N, y6N, y7N,
            y8N, y9N, y10N, y11N, y12N, y13N, y14N, y15N,
            e0N, e1N, a3, a2, a1, a0
        };

        for (int i = 0; i < 16; i++)
            drivers[i] = new Driver(nets[IndexY0 + i], DriveStrength.Strong);

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; }
        = new[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
            13, 14, 15, 16, 17,
            18, 19, 20, 21, 22, 23
        };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Recompute(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Outputs occupy the contiguous index range IndexY0..IndexY15
        // (0..15); changes there are our own feedback, not inputs.
        if (pinIndex <= IndexY15) return;
        Recompute(scheduler);
    }

    private void Recompute(IScheduler scheduler)
    {
        int sel = (High(IndexA3) ? 8 : 0)
                | (High(IndexA2) ? 4 : 0)
                | (High(IndexA1) ? 2 : 0)
                | (High(IndexA0) ? 1 : 0);

        // Both enables must be LOW for any output to assert.
        bool enabled = !High(IndexE0N) && !High(IndexE1N);

        logger.LogDebug("{Label} en={Enabled} sel={Sel}", label, enabled, sel);

        for (int i = 0; i < 16; i++)
        {
            Signal y = enabled && i == sel ? Signal.Low : Signal.High;
            scheduler.Schedule(delayPs, drivers[i], y);
        }
    }

    private bool High(int index) => nets[index].Value == Signal.High;
}