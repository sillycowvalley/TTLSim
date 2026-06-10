using TTLSim.Core;

namespace TTLSim.Chips.Buffers;

/// <summary>
/// 74HC244 — octal buffer / line driver, 3-state, non-inverting, arranged as
/// two independent 4-bit banks. Each bank has its own active-LOW output
/// enable: /1OE (pin 1) gates 1Y1..1Y4, /2OE (pin 19) gates 2Y1..2Y4. While a
/// bank's enable is LOW its four Y outputs follow the matching A inputs; while
/// HIGH (or not driven low) those four outputs go high-Z. The two banks are
/// otherwise unrelated. Nominal HC buffer tPD ~9 ns.
/// </summary>
public sealed class Hc244 : IChip
{
    public const long PropagationDelayPs = 9_000;

    // nets[] index == position in PinNumbers (physical pin order, the
    // power pins 10/GND and 20/VCC excluded).
    private const int IndexOe1 = 0;   // /1OE  pin 1
    private const int IndexOe2 = 17;  // /2OE  pin 19

    private readonly Net[] nets;

    // One buffered bit: output net index, source input net index, enable index.
    private readonly (int OutIdx, int InIdx, int EnIdx)[] bits;
    private readonly Driver[] drivers;
    private readonly bool[] isOutput;
    private readonly long delayPs;

    /// <summary>
    /// Bank 1 = a1..a4 → y1..y4 gated by oe1N; bank 2 = a5..a8 → y5..y8 gated
    /// by oe2N. Arguments are named by logical bank/bit; physical pin order is
    /// resolved internally.
    /// </summary>
    public Hc244(
        Net oe1N,
        Net a1, Net a2, Net a3, Net a4,
        Net y1, Net y2, Net y3, Net y4,
        Net oe2N,
        Net a5, Net a6, Net a7, Net a8,
        Net y5, Net y6, Net y7, Net y8,
        long delayPs = PropagationDelayPs)
    {
        // Physical pins: 1,2,3,4,5,6,7,8,9,11,12,13,14,15,16,17,18,19.
        // Bank 1 in 1A1..1A4 = pins 2,4,6,8; out 1Y1..1Y4 = pins 18,16,14,12.
        // Bank 2 in 2A1..2A4 = pins 11,13,15,17; out 2Y1..2Y4 = pins 9,7,5,3.
        nets = new[]
        {
            oe1N, //  0  pin 1   /1OE
            a1,   //  1  pin 2   1A1
            y8,   //  2  pin 3   2Y4
            a2,   //  3  pin 4   1A2
            y7,   //  4  pin 5   2Y3
            a3,   //  5  pin 6   1A3
            y6,   //  6  pin 7   2Y2
            a4,   //  7  pin 8   1A4
            y5,   //  8  pin 9   2Y1
            a5,   //  9  pin 11  2A1
            y4,   // 10  pin 12  1Y4
            a6,   // 11  pin 13  2A2
            y3,   // 12  pin 14  1Y3
            a7,   // 13  pin 15  2A3
            y2,   // 14  pin 16  1Y2
            a8,   // 15  pin 17  2A4
            y1,   // 16  pin 18  1Y1
            oe2N  // 17  pin 19  /2OE
        };

        bits = new[]
        {
            (16,  1, IndexOe1),  // 1Y1 <- 1A1
            (14,  3, IndexOe1),  // 1Y2 <- 1A2
            (12,  5, IndexOe1),  // 1Y3 <- 1A3
            (10,  7, IndexOe1),  // 1Y4 <- 1A4
            ( 8,  9, IndexOe2),  // 2Y1 <- 2A1
            ( 6, 11, IndexOe2),  // 2Y2 <- 2A2
            ( 4, 13, IndexOe2),  // 2Y3 <- 2A3
            ( 2, 15, IndexOe2),  // 2Y4 <- 2A4
        };

        drivers = new Driver[bits.Length];
        isOutput = new bool[nets.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            drivers[i] = new Driver(nets[bits[i].OutIdx], DriveStrength.Strong);
            isOutput[bits[i].OutIdx] = true;
        }

        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => EmitOutputs(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Reacting to our own output change would just recompute the same
        // value; inputs and the two enables are the only meaningful triggers.
        if (isOutput[pinIndex]) return;
        EmitOutputs(scheduler);
    }

    private void EmitOutputs(IScheduler scheduler)
    {
        for (int i = 0; i < bits.Length; i++)
        {
            (int outIdx, int inIdx, int enIdx) = bits[i];
            bool enabled = nets[enIdx].Value == Signal.Low;
            Signal s = enabled ? Pass(nets[inIdx].Value) : Signal.HighZ;
            scheduler.Schedule(delayPs, drivers[i], s);
        }
    }

    // Non-inverting pass with the family's input convention: a floating or
    // conflicting input yields an indeterminate output.
    private static Signal Pass(Signal input) =>
        input == Signal.High ? Signal.High :
        input == Signal.Low ? Signal.Low :
        Signal.Unknown;
}