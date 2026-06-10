using TTLSim.Core;

namespace TTLSim.Chips.Buffers;

/// <summary>
/// 74HC541 — octal buffer / line driver, 3-state, non-inverting, as a single
/// 8-bit bank with a flow-through pinout (all A inputs down one side, all Y
/// outputs down the other). Two active-LOW enables /OE1 (pin 1) and /OE2
/// (pin 19) are ANDed: both must be LOW for Y1..Y8 to follow A1..A8; if either
/// is HIGH (or not driven low) all eight outputs go high-Z. Nominal tPD ~9 ns.
/// </summary>
public sealed class Hc541 : IChip
{
    public const long PropagationDelayPs = 9_000;

    private const int IndexOe1 = 0;   // /OE1  pin 1
    private const int IndexOe2 = 17;  // /OE2  pin 19

    private readonly Net[] nets;
    private readonly (int OutIdx, int InIdx)[] bits;
    private readonly Driver[] drivers;
    private readonly bool[] isOutput;
    private readonly long delayPs;

    public Hc541(
        Net oe1N,
        Net a1, Net a2, Net a3, Net a4, Net a5, Net a6, Net a7, Net a8,
        Net y1, Net y2, Net y3, Net y4, Net y5, Net y6, Net y7, Net y8,
        Net oe2N,
        long delayPs = PropagationDelayPs)
    {
        // Physical pins 1..9, 11..19. A1..A8 = pins 2..9; Y1..Y8 = pins
        // 18,17,16,15,14,13,12,11.
        nets = new[]
        {
            oe1N, //  0  pin 1   /OE1
            a1,   //  1  pin 2
            a2,   //  2  pin 3
            a3,   //  3  pin 4
            a4,   //  4  pin 5
            a5,   //  5  pin 6
            a6,   //  6  pin 7
            a7,   //  7  pin 8
            a8,   //  8  pin 9
            y8,   //  9  pin 11
            y7,   // 10  pin 12
            y6,   // 11  pin 13
            y5,   // 12  pin 14
            y4,   // 13  pin 15
            y3,   // 14  pin 16
            y2,   // 15  pin 17
            y1,   // 16  pin 18
            oe2N  // 17  pin 19  /OE2
        };

        // Y(k+1) at index 16-k, A(k+1) at index 1+k.
        bits = new (int, int)[8];
        for (int k = 0; k < 8; k++)
            bits[k] = (16 - k, 1 + k);

        drivers = new Driver[8];
        isOutput = new bool[nets.Length];
        for (int k = 0; k < 8; k++)
        {
            drivers[k] = new Driver(nets[bits[k].OutIdx], DriveStrength.Strong);
            isOutput[bits[k].OutIdx] = true;
        }

        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => EmitOutputs(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (isOutput[pinIndex]) return;
        EmitOutputs(scheduler);
    }

    private void EmitOutputs(IScheduler scheduler)
    {
        bool enabled =
            nets[IndexOe1].Value == Signal.Low &&
            nets[IndexOe2].Value == Signal.Low;

        for (int k = 0; k < 8; k++)
        {
            (int outIdx, int inIdx) = bits[k];
            Signal s = enabled ? Pass(nets[inIdx].Value) : Signal.HighZ;
            scheduler.Schedule(delayPs, drivers[k], s);
        }
    }

    private static Signal Pass(Signal input) =>
        input == Signal.High ? Signal.High :
        input == Signal.Low ? Signal.Low :
        Signal.Unknown;
}