using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Comparators;

/// <summary>
/// 74HC688 — 8-bit identity comparator, 20-pin DIP. Fully combinational.
/// Compares two 8-bit words P and Q and asserts the single active-LOW
/// output /P=Q (pin 19) when every bit matches AND the active-LOW enable
/// /G (pin 1) is LOW. When /G is HIGH the output is forced HIGH regardless
/// of the data. There is no magnitude path (that's the '682/'685 family) —
/// identity only.
///
/// Pin map (from ChipPartDefinition.Ic74688):
///   /G=1                                    enable (LOW = compare)
///   P0=2  P1=4  P2=6  P3=8                  word P, low nibble
///   P4=11 P5=13 P6=15 P7=17                 word P, high nibble
///   Q0=3  Q1=5  Q2=7  Q3=9                  word Q, low nibble
///   Q4=12 Q5=14 Q6=16 Q7=18                 word Q, high nibble
///   /P=Q=19                                 output, LOW on match
///   VCC=20  GND=10                          power (consumed by the build pipeline)
///
/// PIN-MAP NOTE (2026-07): the P/Q interleave is P-first on BOTH sides of
/// the package -- pin 11 is P4, not Q4. The original model (and part
/// definition, and ChipFactory map) had the high-nibble pairs transposed;
/// verified against Nexperia 74HC688 Table 2 (Rev. 5, Aug 2024) and fixed
/// in the same change, cf. the identical Hc299 DSR/CP precedent. Identity
/// compare is symmetric within a pin pair, so behaviour never differed --
/// this is a labelling/consistency fix.
///
/// Inputs map Unknown/HighZ to Low, matching the catalogue convention
/// ("treat weird inputs as Low and let TTL011 surface the floating pin at
/// design time"). For /G that means a floating enable reads as enabled;
/// for data pins it means an unwired P/Q bit pair compares as 0 == 0.
/// The output always drives (no high-Z state on this part).
/// </summary>
public sealed class Hc688 : IChip
{
    public const long PropagationDelayPs = 20_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    private const int IndexGN = 0;      // /G    (pin 1)
    private const int IndexP0 = 1;      // P0..P7 at indices 1..8
    private const int IndexQ0 = 9;      // Q0..Q7 at indices 9..16
    private const int IndexPeqQN = 17;  // /P=Q  (pin 19) output

    private readonly Net[] nets;
    private readonly Driver driver;
    private readonly long delayPs;

    private readonly ILogger? logger;
    private readonly string label;

    public Hc688(
        Net gN,
        Net p0, Net p1, Net p2, Net p3, Net p4, Net p5, Net p6, Net p7,
        Net q0, Net q1, Net q2, Net q3, Net q4, Net q5, Net q6, Net q7,
        Net pEqQN,
        string label = "688",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            gN,
            p0, p1, p2, p3, p4, p5, p6, p7,
            q0, q1, q2, q3, q4, q5, q6, q7,
            pEqQN
        };

        driver = new Driver(nets[IndexPeqQN], DriveStrength.Strong);
        this.delayPs = delayPs;
        this.label = label;
        this.logger = logger;
    }

    // Pin numbers in nets[] order: /G, P0..P7, Q0..Q7, /P=Q.
    public IReadOnlyList<int> PinNumbers { get; } = new[]
    {
        1,
        2, 4, 6, 8, 11, 13, 15, 17,
        3, 5, 7, 9, 12, 14, 16, 18,
        19
    };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Recompute(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // The output's own transition never triggers a recompute.
        if (pinIndex == IndexPeqQN) return;
        Recompute(scheduler);
    }

    /// <summary>
    /// Read /G and both words, schedule the single output driver.
    /// Called from Initialize and from any input transition.
    /// </summary>
    private void Recompute(IScheduler scheduler)
    {
        // /G HIGH disables the compare and forces the output HIGH. Anything
        // other than a solid High (Low, Unknown, HighZ) reads as Low =
        // enabled, per the catalogue convention.
        if (nets[IndexGN].Value == Signal.High)
        {
            scheduler.Schedule(delayPs, driver, Signal.High);
            return;
        }

        // Bit-wise identity: a pin contributes 1 only when solidly High;
        // Low/Unknown/HighZ all read as 0, so an unwired bit pair matches.
        int p = 0, q = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            if (nets[IndexP0 + bit].Value == Signal.High) p |= 1 << bit;
            if (nets[IndexQ0 + bit].Value == Signal.High) q |= 1 << bit;
        }

        scheduler.Schedule(delayPs, driver, p == q ? Signal.Low : Signal.High);
    }
}