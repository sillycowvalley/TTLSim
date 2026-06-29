using TTLSim.Core;

namespace TTLSim.Chips.Multiplexers;

/// <summary>
/// 74HC257 — quad 2-to-1 data selector / multiplexer with 3-state outputs.
///
/// Four independent 2-input muxes share a common select line S and a common
/// active-low output enable /OE:
///
///   nY = (/OE HIGH) ? High-Z
///      : (S   LOW ) ? nI0
///                   : nI1            for n = 1..4
///
/// This is the tri-state variant of the '157: when /OE is HIGH all four Y
/// outputs are released to High-Z (the '157 instead drives them actively
/// LOW). When /OE is LOW the chip passes data: S LOW selects the I0 inputs,
/// S HIGH selects the I1 inputs.
///
/// Pin mapping (16-pin DIP): S=1, 1I0=2, 1I1=3, 1Y=4, 2I0=5, 2I1=6, 2Y=7,
/// GND=8, 3Y=9, 3I1=10, 3I0=11, 4Y=12, 4I1=13, 4I0=14, /OE=15, VCC=16.
/// </summary>
public sealed class Hc257 : IChip
{
    public const long PropagationDelayPs = 12_000;

    // Indices into nets[] -- the order PinNumbers is declared in.
    // Order: S, /OE, then four (I0, I1, Y) groups.
    private const int IndexS = 0;
    private const int IndexOE = 1;
    // Per-channel block of 3 nets starting at IndexCh0:
    //   [ch*3 + 0] = nI0   (input 0)
    //   [ch*3 + 1] = nI1   (input 1)
    //   [ch*3 + 2] = nY    (output)
    private const int IndexCh0 = 2;

    private readonly Net[] nets;
    private readonly Driver[] yDrivers = new Driver[4];
    private readonly long delayPs;

    public Hc257(
        Net s, Net oeN,
        Net i1_0, Net i1_1, Net y1,
        Net i2_0, Net i2_1, Net y2,
        Net i3_0, Net i3_1, Net y3,
        Net i4_0, Net i4_1, Net y4,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            s, oeN,
            i1_0, i1_1, y1,
            i2_0, i2_1, y2,
            i3_0, i3_1, y3,
            i4_0, i4_1, y4
        };
        for (int ch = 0; ch < 4; ch++)
            yDrivers[ch] = new Driver(nets[IndexCh0 + ch * 3 + 2], DriveStrength.Strong);

        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order. S=1, /OE=15, then channels 1..4 with their
    // (I0, I1, Y) triples in pin-number-roughly-ascending order.
    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 15,
                   2,  3,  4,    // 1I0, 1I1, 1Y
                   5,  6,  7,    // 2I0, 2I1, 2Y
                  11, 10,  9,    // 3I0, 3I1, 3Y  (note 3Y is on pin 9)
                  14, 13, 12 };  // 4I0, 4I1, 4Y

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => RecomputeAndSchedule(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Y outputs are at indices 4, 7, 10, 13. Anything else is an input.
        if (pinIndex == 4 || pinIndex == 7 || pinIndex == 10 || pinIndex == 13) return;
        RecomputeAndSchedule(scheduler);
    }

    private void RecomputeAndSchedule(IScheduler scheduler)
    {
        Signal oe = nets[IndexOE].Value;     // active-low: LOW = outputs enabled
        Signal sel = nets[IndexS].Value;

        for (int ch = 0; ch < 4; ch++)
        {
            int baseIdx = IndexCh0 + ch * 3;
            Signal i0 = nets[baseIdx + 0].Value;
            Signal i1 = nets[baseIdx + 1].Value;

            Signal y;
            if (oe == Signal.High)
            {
                // Output enable de-asserted: outputs are released to High-Z.
                y = Signal.HighZ;
            }
            else if (oe == Signal.Low)
            {
                // Output enable asserted: pass through the selected input.
                if (sel == Signal.Low) y = i0;
                else if (sel == Signal.High) y = i1;
                else y = Signal.Unknown;   // select line floating / contested
            }
            else
            {
                // /OE line itself is Unknown -- we can't be sure whether the
                // outputs are gated; propagate Unknown rather than guessing.
                y = Signal.Unknown;
            }

            scheduler.Schedule(delayPs, yDrivers[ch], y);
        }
    }
}