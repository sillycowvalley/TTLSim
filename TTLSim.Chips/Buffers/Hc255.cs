using TTLSim.Core;

namespace TTLSim.Chips.Buffers;

/// <summary>
/// 74HC245 — octal bus transceiver, 3-state, non-inverting, bidirectional.
/// DIR (pin 1) chooses direction, /OE (pin 19) is the active-LOW enable:
///
///   /OE = HIGH                  → both sides high-Z (bus isolated)
///   /OE = LOW, DIR = HIGH       → A drives B (A inputs, B outputs)
///   /OE = LOW, DIR = LOW        → B drives A (B inputs, A outputs)
///
/// The chip owns a driver on every A pin and every B pin; the inactive side's
/// drivers are held high-Z so the chip never fights its own input side. If DIR
/// is unresolved while enabled, both sides stay high-Z. Nominal tPD ~9 ns.
/// </summary>
public sealed class Hc245 : IChip
{
    public const long PropagationDelayPs = 9_000;

    private const int IndexDir = 0;   // DIR  pin 1
    private const int IndexOeN = 17;  // /OE  pin 19

    private readonly Net[] nets;
    private readonly (int AIdx, int BIdx)[] pairs;
    private readonly Driver[] aDrivers;  // drive the A side (active when B→A)
    private readonly Driver[] bDrivers;  // drive the B side (active when A→B)
    private readonly long delayPs;

    public Hc245(
        Net dir,
        Net a1, Net a2, Net a3, Net a4, Net a5, Net a6, Net a7, Net a8,
        Net b1, Net b2, Net b3, Net b4, Net b5, Net b6, Net b7, Net b8,
        Net oeN,
        long delayPs = PropagationDelayPs)
    {
        // Physical pins 1..9, 11..19 (10/GND, 20/VCC excluded).
        // A1..A8 = pins 2..9; B1..B8 = pins 18,17,16,15,14,13,12,11.
        nets = new[]
        {
            dir, //  0  pin 1   DIR
            a1,  //  1  pin 2
            a2,  //  2  pin 3
            a3,  //  3  pin 4
            a4,  //  4  pin 5
            a5,  //  5  pin 6
            a6,  //  6  pin 7
            a7,  //  7  pin 8
            a8,  //  8  pin 9
            b8,  //  9  pin 11
            b7,  // 10  pin 12
            b6,  // 11  pin 13
            b5,  // 12  pin 14
            b4,  // 13  pin 15
            b3,  // 14  pin 16
            b2,  // 15  pin 17
            b1,  // 16  pin 18
            oeN  // 17  pin 19  /OE
        };

        // A(k+1) sits at index 1+k; B(k+1) sits at index 16-k.
        pairs = new (int, int)[8];
        for (int k = 0; k < 8; k++)
            pairs[k] = (1 + k, 16 - k);

        aDrivers = new Driver[8];
        bDrivers = new Driver[8];
        for (int k = 0; k < 8; k++)
        {
            aDrivers[k] = new Driver(nets[pairs[k].AIdx], DriveStrength.Strong);
            bDrivers[k] = new Driver(nets[pairs[k].BIdx], DriveStrength.Strong);
        }

        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => EmitOutputs(scheduler);

    // Either side can be the output side, so any net change may matter;
    // recomputing from the (gated) input side is self-consistent.
    public void OnInputChanged(int pinIndex, IScheduler scheduler) => EmitOutputs(scheduler);

    private void EmitOutputs(IScheduler scheduler)
    {
        bool enabled = nets[IndexOeN].Value == Signal.Low;
        Signal dir = nets[IndexDir].Value;
        bool aToB = enabled && dir == Signal.High;
        bool bToA = enabled && dir == Signal.Low;

        for (int k = 0; k < 8; k++)
        {
            (int aIdx, int bIdx) = pairs[k];
            Signal bOut = aToB ? Pass(nets[aIdx].Value) : Signal.HighZ;
            Signal aOut = bToA ? Pass(nets[bIdx].Value) : Signal.HighZ;
            scheduler.Schedule(delayPs, bDrivers[k], bOut);
            scheduler.Schedule(delayPs, aDrivers[k], aOut);
        }
    }

    private static Signal Pass(Signal input) =>
        input == Signal.High ? Signal.High :
        input == Signal.Low ? Signal.Low :
        Signal.Unknown;
}