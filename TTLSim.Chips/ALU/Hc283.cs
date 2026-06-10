using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Alu;

/// <summary>
/// 74HC283 — 4-bit binary full adder with fast (look-ahead) carry, 16-pin DIP.
/// Fully combinational, active-HIGH throughout: S = A + B + C0, where A and B
/// are the 4-bit operands (A1/B1 = LSB, A4/B4 = MSB), C0 is the carry-in, and
/// C4 is the carry-out. Cascade two for 8 bits by feeding the low adder's C4
/// into the high adder's C0.
///
/// Pin map (from ChipPartDefinition.Ic74283):
///   A1=5  A2=3  A3=14 A4=12   operand A (A1 = LSB)
///   B1=6  B2=2  B3=15 B4=11   operand B (B1 = LSB)
///   C0=7                       carry in
///   S1=4  S2=1  S3=13 S4=10   sum     (S1 = LSB)
///   C4=9                       carry out
///   VCC=16  GND=8              power (consumed by the build pipeline)
///
/// Inputs map Unknown/HighZ to 0, matching the catalogue convention ("treat
/// weird inputs as Low and let TTL011 surface the floating pin at design
/// time"). Outputs always drive (the part has no enable / no high-Z state).
/// </summary>
public sealed class Hc283 : IChip
{
    public const long PropagationDelayPs = 17_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexS2 = 0;   // S2  (pin 1)  output
    private const int IndexB2 = 1;   // B2  (pin 2)
    private const int IndexA2 = 2;   // A2  (pin 3)
    private const int IndexS1 = 3;   // S1  (pin 4)  output
    private const int IndexA1 = 4;   // A1  (pin 5)
    private const int IndexB1 = 5;   // B1  (pin 6)
    private const int IndexC0 = 6;   // C0  (pin 7)
    private const int IndexC4 = 7;   // C4  (pin 9)  output
    private const int IndexS4 = 8;   // S4  (pin 10) output
    private const int IndexB4 = 9;   // B4  (pin 11)
    private const int IndexA4 = 10;  // A4  (pin 12)
    private const int IndexS3 = 11;  // S3  (pin 13) output
    private const int IndexA3 = 12;  // A3  (pin 14)
    private const int IndexB3 = 13;  // B3  (pin 15)

    // Output drivers in declaration order: S1, S2, S3, S4, C4.
    private const int DriverS1 = 0;
    private const int DriverS2 = 1;
    private const int DriverS3 = 2;
    private const int DriverS4 = 3;
    private const int DriverC4 = 4;

    private readonly Net[] nets;
    private readonly Driver[] drivers = new Driver[5];
    private readonly long delayPs;

    private readonly ILogger logger;
    private readonly string label;

    public Hc283(
        Net a1, Net a2, Net a3, Net a4,
        Net b1, Net b2, Net b3, Net b4,
        Net c0,
        Net s1, Net s2, Net s3, Net s4,
        Net c4,
        string label = "283",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            s2, b2, a2, s1, a1, b1, c0,
            c4, s4, b4, a4, s3, a3, b3
        };

        drivers[DriverS1] = new Driver(nets[IndexS1], DriveStrength.Strong);
        drivers[DriverS2] = new Driver(nets[IndexS2], DriveStrength.Strong);
        drivers[DriverS3] = new Driver(nets[IndexS3], DriveStrength.Strong);
        drivers[DriverS4] = new Driver(nets[IndexS4], DriveStrength.Strong);
        drivers[DriverC4] = new Driver(nets[IndexC4], DriveStrength.Strong);

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
        // Outputs never feed back. Any input change is a full recompute.
        if (pinIndex == IndexS1 || pinIndex == IndexS2 || pinIndex == IndexS3
            || pinIndex == IndexS4 || pinIndex == IndexC4) return;
        Recompute(scheduler);
    }

    private void Recompute(IScheduler scheduler)
    {
        int a = Bit(IndexA1)
              | (Bit(IndexA2) << 1)
              | (Bit(IndexA3) << 2)
              | (Bit(IndexA4) << 3);
        int b = Bit(IndexB1)
              | (Bit(IndexB2) << 1)
              | (Bit(IndexB3) << 2)
              | (Bit(IndexB4) << 3);
        int cin = Bit(IndexC0);

        int sum = a + b + cin;   // 0..31
        logger.LogDebug("{Label} {A:X1}+{B:X1}+{Cin} = {Sum:X2}", label, a, b, cin, sum);

        scheduler.Schedule(delayPs, drivers[DriverS1], SignalOf(sum, 0));
        scheduler.Schedule(delayPs, drivers[DriverS2], SignalOf(sum, 1));
        scheduler.Schedule(delayPs, drivers[DriverS3], SignalOf(sum, 2));
        scheduler.Schedule(delayPs, drivers[DriverS4], SignalOf(sum, 3));
        scheduler.Schedule(delayPs, drivers[DriverC4], SignalOf(sum, 4));
    }

    private int Bit(int index) => nets[index].Value == Signal.High ? 1 : 0;

    private static Signal SignalOf(int value, int bit)
        => ((value >> bit) & 1) != 0 ? Signal.High : Signal.Low;
}