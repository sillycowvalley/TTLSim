using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Decoders;

/// <summary>
/// 74HC139 — dual 2-to-4 line decoder, 16-pin DIP. Two fully independent
/// decoders share only power. Each half has an active-low enable (/AE, /BE),
/// two select inputs (A0 the LSB, A1 the MSB), and four active-low outputs.
/// With the enable LOW exactly one output — the one addressed by A1:A0 —
/// goes LOW; the other three stay HIGH. With the enable HIGH all four
/// outputs are forced HIGH. Outputs always drive (no tri-state).
///
/// Pin map (from ChipPartDefinition.Ic74139):
///   /AE=1  AA0=2  AA1=3  /AY0=4  /AY1=5  /AY2=6  /AY3=7
///   /BE=15 BA0=14 BA1=13 /BY0=12 /BY1=11 /BY2=10 /BY3=9
///   VCC=16  GND=8   power (consumed by the build pipeline)
///
/// Inputs map Unknown/HighZ to Low (catalogue convention) — an unresolved
/// enable therefore reads as asserted, and unresolved selects address Y0.
/// TTL011 surfaces genuinely floating pins at design time.
/// </summary>
public sealed class Hc139 : IChip
{
    public const long PropagationDelayPs = 33_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexAEN = 0;   // /AE  (pin 1)
    private const int IndexAA0 = 1;   // AA0  (pin 2)
    private const int IndexAA1 = 2;   // AA1  (pin 3)
    private const int IndexAY0 = 3;   // /AY0 (pin 4)  output
    private const int IndexAY1 = 4;   // /AY1 (pin 5)  output
    private const int IndexAY2 = 5;   // /AY2 (pin 6)  output
    private const int IndexAY3 = 6;   // /AY3 (pin 7)  output
    private const int IndexBY3 = 7;   // /BY3 (pin 9)  output
    private const int IndexBY2 = 8;   // /BY2 (pin 10) output
    private const int IndexBY1 = 9;   // /BY1 (pin 11) output
    private const int IndexBY0 = 10;  // /BY0 (pin 12) output
    private const int IndexBA1 = 11;  // BA1  (pin 13)
    private const int IndexBA0 = 12;  // BA0  (pin 14)
    private const int IndexBEN = 13;  // /BE  (pin 15)

    // drivers[] slots: half A /Y0../Y3, then half B /Y0../Y3.
    private const int DriverAY0 = 0;
    private const int DriverBY0 = 4;

    private readonly Net[] nets;
    private readonly Driver[] drivers = new Driver[8];
    private readonly long delayPs;

    private readonly ILogger logger;
    private readonly string label;

    public Hc139(
        Net aeN, Net aa0, Net aa1,
        Net ay0N, Net ay1N, Net ay2N, Net ay3N,
        Net by3N, Net by2N, Net by1N, Net by0N,
        Net ba1, Net ba0, Net beN,
        string label = "139",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            aeN, aa0, aa1, ay0N, ay1N, ay2N, ay3N,
            by3N, by2N, by1N, by0N, ba1, ba0, beN
        };

        drivers[DriverAY0 + 0] = new Driver(nets[IndexAY0], DriveStrength.Strong);
        drivers[DriverAY0 + 1] = new Driver(nets[IndexAY1], DriveStrength.Strong);
        drivers[DriverAY0 + 2] = new Driver(nets[IndexAY2], DriveStrength.Strong);
        drivers[DriverAY0 + 3] = new Driver(nets[IndexAY3], DriveStrength.Strong);
        drivers[DriverBY0 + 0] = new Driver(nets[IndexBY0], DriveStrength.Strong);
        drivers[DriverBY0 + 1] = new Driver(nets[IndexBY1], DriveStrength.Strong);
        drivers[DriverBY0 + 2] = new Driver(nets[IndexBY2], DriveStrength.Strong);
        drivers[DriverBY0 + 3] = new Driver(nets[IndexBY3], DriveStrength.Strong);

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
        // Outputs occupy the contiguous index range IndexAY0..IndexBY0
        // (3..10); changes there are our own feedback, not inputs.
        if (pinIndex >= IndexAY0 && pinIndex <= IndexBY0) return;
        Recompute(scheduler);
    }

    private void Recompute(IScheduler scheduler)
    {
        int selA = (High(IndexAA1) ? 2 : 0) | (High(IndexAA0) ? 1 : 0);
        int selB = (High(IndexBA1) ? 2 : 0) | (High(IndexBA0) ? 1 : 0);

        // Enable LOW = decode (addressed output LOW); enable HIGH = all HIGH.
        bool enA = !High(IndexAEN);
        bool enB = !High(IndexBEN);

        logger.LogDebug("{Label} A: en={EnA} sel={SelA}  B: en={EnB} sel={SelB}",
            label, enA, selA, enB, selB);

        for (int i = 0; i < 4; i++)
        {
            Signal ay = enA && i == selA ? Signal.Low : Signal.High;
            Signal by = enB && i == selB ? Signal.Low : Signal.High;
            scheduler.Schedule(delayPs, drivers[DriverAY0 + i], ay);
            scheduler.Schedule(delayPs, drivers[DriverBY0 + i], by);
        }
    }

    private bool High(int index) => nets[index].Value == Signal.High;
}