using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Multiplexers;

/// <summary>
/// 74HC151 — 8-to-1 multiplexer with complementary outputs, 16-pin DIP.
/// S2:S1:S0 select which of I0..I7 is routed to Y (true) and /Y (complement).
/// /E HIGH disables the chip: Y forced LOW, /Y forced HIGH regardless of the
/// data and select inputs (active drive, no high-Z state — the enable does
/// not tri-state).
///
/// Pin map (from ChipPartDefinition.Ic74151):
///   I3=1  I2=2  I1=3  I0=4  Y=5  /Y=6  /E=7
///   S2=9  S1=10  S0=11  I7=12  I6=13  I5=14  I4=15
///   VCC=16  GND=8   power (consumed by the build pipeline)
///
/// Four of these give the Mini Blinky TOS source mux (8:1 × 4 bits, select
/// TOS_SRC0..2 from GAL 2), one bit position per chip. Also the classic
/// 3-variable Boolean function generator: wire the function's truth table
/// onto I0..I7 and the variables onto the selects.
///
/// Inputs map Unknown/HighZ to 0 (catalogue convention, same as the '153).
/// The selected input is reflected to Y as High/Low with its complement on
/// /Y; both outputs always drive.
/// </summary>
public sealed class Hc151 : IChip
{
    public const long PropagationDelayPs = 15_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexI3 = 0;    // I3  (pin 1)
    private const int IndexI2 = 1;    // I2  (pin 2)
    private const int IndexI1 = 2;    // I1  (pin 3)
    private const int IndexI0 = 3;    // I0  (pin 4)
    private const int IndexY = 4;     // Y   (pin 5)  output
    private const int IndexYN = 5;    // /Y  (pin 6)  output
    private const int IndexEN = 6;    // /E  (pin 7)
    private const int IndexS2 = 7;    // S2  (pin 9)
    private const int IndexS1 = 8;    // S1  (pin 10)
    private const int IndexS0 = 9;    // S0  (pin 11)
    private const int IndexI7 = 10;   // I7  (pin 12)
    private const int IndexI6 = 11;   // I6  (pin 13)
    private const int IndexI5 = 12;   // I5  (pin 14)
    private const int IndexI4 = 13;   // I4  (pin 15)

    private const int DriverY = 0;
    private const int DriverYN = 1;

    private readonly Net[] nets;
    private readonly Driver[] drivers = new Driver[2];
    private readonly long delayPs;

    private readonly ILogger logger;
    private readonly string label;

    public Hc151(
        Net i3, Net i2, Net i1, Net i0,
        Net y, Net yN, Net eN,
        Net s2, Net s1, Net s0,
        Net i7, Net i6, Net i5, Net i4,
        string label = "151",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            i3, i2, i1, i0,
            y, yN, eN,
            s2, s1, s0,
            i7, i6, i5, i4
        };

        drivers[DriverY] = new Driver(nets[IndexY], DriveStrength.Strong);
        drivers[DriverYN] = new Driver(nets[IndexYN], DriveStrength.Strong);

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
        if (pinIndex == IndexY || pinIndex == IndexYN) return;
        Recompute(scheduler);
    }

    private void Recompute(IScheduler scheduler)
    {
        // sel = S2:S1:S0, S2 the MSB.
        int sel = (High(IndexS2) ? 4 : 0)
                | (High(IndexS1) ? 2 : 0)
                | (High(IndexS0) ? 1 : 0);

        int input = sel switch
        {
            0 => IndexI0,
            1 => IndexI1,
            2 => IndexI2,
            3 => IndexI3,
            4 => IndexI4,
            5 => IndexI5,
            6 => IndexI6,
            _ => IndexI7
        };

        // Enable LOW = pass the selected input to Y with its complement on
        // /Y; enable HIGH = Y forced LOW, /Y forced HIGH.
        Signal y, yN;
        if (High(IndexEN))
        {
            y = Signal.Low;
            yN = Signal.High;
        }
        else
        {
            bool bit = High(input);
            y = bit ? Signal.High : Signal.Low;
            yN = bit ? Signal.Low : Signal.High;
        }

        logger.LogDebug("{Label} sel={Sel} Y={Y} /Y={YN}", label, sel, y, yN);

        scheduler.Schedule(delayPs, drivers[DriverY], y);
        scheduler.Schedule(delayPs, drivers[DriverYN], yN);
    }

    private bool High(int index) => nets[index].Value == Signal.High;
}