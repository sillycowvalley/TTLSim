using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Multiplexers;

/// <summary>
/// 74HC153 — dual 4-to-1 multiplexer, 16-pin DIP. Two independent muxes share
/// the select inputs S0, S1; each has its own active-low enable (/1E, /2E) and
/// its own output (1Y, 2Y). Select picks the input: (S1,S0) = 00→I0, 01→I1,
/// 10→I2, 11→I3. With an enable HIGH the corresponding output is forced LOW
/// (this part is non-inverting and has no high-Z state — the enable does not
/// tri-state).
///
/// Pin map (from ChipPartDefinition.Ic74153):
///   /1E=1  S1=2  1I3=3  1I2=4  1I1=5  1I0=6  1Y=7
///   /2E=15 S0=14 2I3=13 2I2=12 2I1=11 2I0=10 2Y=9
///   VCC=16  GND=8   power (consumed by the build pipeline)
///
/// Four of these give an 8-bit 4-way mux (each chip covers two bit positions),
/// which is exactly the Blinky NEXTPC selector: I0=PC+1, I1=branch immediate,
/// I2=return-stack top, I3=PC (hold).
///
/// Inputs map Unknown/HighZ to 0 (catalogue convention). The selected input is
/// reflected to the output as High/Low; outputs always drive.
/// </summary>
public sealed class Hc153 : IChip
{
    public const long PropagationDelayPs = 14_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexE1N = 0;   // /1E  (pin 1)
    private const int IndexS1 = 1;    // S1   (pin 2)
    private const int Index1I3 = 2;   // 1I3  (pin 3)
    private const int Index1I2 = 3;   // 1I2  (pin 4)
    private const int Index1I1 = 4;   // 1I1  (pin 5)
    private const int Index1I0 = 5;   // 1I0  (pin 6)
    private const int Index1Y = 6;    // 1Y   (pin 7)  output
    private const int Index2Y = 7;    // 2Y   (pin 9)  output
    private const int Index2I0 = 8;   // 2I0  (pin 10)
    private const int Index2I1 = 9;   // 2I1  (pin 11)
    private const int Index2I2 = 10;  // 2I2  (pin 12)
    private const int Index2I3 = 11;  // 2I3  (pin 13)
    private const int IndexS0 = 12;   // S0   (pin 14)
    private const int IndexE2N = 13;  // /2E  (pin 15)

    private const int Driver1Y = 0;
    private const int Driver2Y = 1;

    private readonly Net[] nets;
    private readonly Driver[] drivers = new Driver[2];
    private readonly long delayPs;

    private readonly ILogger logger;
    private readonly string label;

    public Hc153(
        Net e1N, Net s1,
        Net i1_3, Net i1_2, Net i1_1, Net i1_0, Net y1,
        Net y2, Net i2_0, Net i2_1, Net i2_2, Net i2_3,
        Net s0, Net e2N,
        string label = "153",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            e1N, s1, i1_3, i1_2, i1_1, i1_0, y1,
            y2, i2_0, i2_1, i2_2, i2_3, s0, e2N
        };

        drivers[Driver1Y] = new Driver(nets[Index1Y], DriveStrength.Strong);
        drivers[Driver2Y] = new Driver(nets[Index2Y], DriveStrength.Strong);

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
        if (pinIndex == Index1Y || pinIndex == Index2Y) return;
        Recompute(scheduler);
    }

    private void Recompute(IScheduler scheduler)
    {
        // sel = S1:S0, S1 the MSB.
        int sel = (High(IndexS1) ? 2 : 0) | (High(IndexS0) ? 1 : 0);

        int in1 = sel switch
        {
            0 => Index1I0,
            1 => Index1I1,
            2 => Index1I2,
            _ => Index1I3
        };
        int in2 = sel switch
        {
            0 => Index2I0,
            1 => Index2I1,
            2 => Index2I2,
            _ => Index2I3
        };

        // Enable LOW = pass the selected input; enable HIGH = output forced LOW.
        Signal y1 = High(IndexE1N) ? Signal.Low
            : (High(in1) ? Signal.High : Signal.Low);
        Signal y2 = High(IndexE2N) ? Signal.Low
            : (High(in2) ? Signal.High : Signal.Low);

        logger.LogDebug("{Label} sel={Sel} 1Y={Y1} 2Y={Y2}", label, sel, y1, y2);

        scheduler.Schedule(delayPs, drivers[Driver1Y], y1);
        scheduler.Schedule(delayPs, drivers[Driver2Y], y2);
    }

    private bool High(int index) => nets[index].Value == Signal.High;
}