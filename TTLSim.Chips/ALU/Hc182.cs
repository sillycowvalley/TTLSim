using TTLSim.Core;

namespace TTLSim.Chips.Alu;

/// <summary>
/// 74HC182 — carry lookahead generator, 16-pin DIP, fully combinational.
/// Cascades up to four '181 ALU slices into a 16-bit ALU with full
/// lookahead carry: each slice's /P (X pin) and /G (Y pin) feed this chip,
/// and Cn+x / Cn+y / Cn+z drive the Cn inputs of slices 1..3 directly.
///
/// POLARITY — anchored to the Hc181 model (this catalogue's behavioural
/// ground truth) and identical to GAL_182.pld, the GAL16V8 drop-in for the
/// out-of-production physical part:
///   - /P0../P3 and /G0../G3 inputs assert LOW (the '181's X and Y pins).
///   - Cn input asserts LOW ("carry in"), matching the '181's Cn pin,
///     which injects +1 when LOW. The external carry source drives slice
///     0's Cn and this pin in parallel.
///   - Cn+x / Cn+y / Cn+z assert LOW, so they wire DIRECTLY to the Cn pins
///     of slices 1..3 with no inverters. (The pin labels carry no overbar,
///     same as the '181's Cn — assertion level, not label, is what matters,
///     and in this catalogue's '181 convention a carry is a LOW.)
///   - Group /G and /P assert LOW; they exist for a further lookahead
///     level. Faithful to the original part, group /G excludes P0.
///
/// Lookahead, in asserted terms (c = carry in, gk / pk = slice k):
///   Cn+x:  c1 = g0 + p0.c
///   Cn+y:  c2 = g1 + p1.g0 + p1.p0.c
///   Cn+z:  c3 = g2 + p2.g1 + p2.p1.g0 + p2.p1.p0.c
///   /G:    g  = g3 + p3.g2 + p3.p2.g1 + p3.p2.p1.g0
///   /P:    p  = p3.p2.p1.p0
///
/// These compose correctly with any slice whose P and G satisfy the
/// per-slice identity "carry-out = G + P.carry-in" against its own carry
/// output — which Hc181's ADD and SUB rows do (see
/// Hc181.ComputeCarryLookahead).
///
/// Unknown/HighZ inputs read as NOT asserted, per the catalogue convention
/// (map unresolved inputs to logical 0 and let TTL011 surface the floating
/// pin at design time). Unused /P and /G inputs must be tied HIGH (not
/// asserted) in the schematic, exactly as on the real part.
///
/// Propagation delay: a single representative figure for every output,
/// matching the precedent in the rest of the chip catalogue. The factory
/// overrides it per (part, family) via TtlTiming.
/// </summary>
public sealed class Hc182 : IChip
{
    public const long PropagationDelayPs = 25_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    // Pin numbers from ChipPartDefinition.Ic74182 (pins 8 GND and 16 VCC
    // are excluded -- power pins are consumed by the build pipeline).
    private const int IndexG1 = 0;    // /G1   (pin 1)
    private const int IndexP1 = 1;    // /P1   (pin 2)
    private const int IndexG0 = 2;    // /G0   (pin 3)
    private const int IndexP0 = 3;    // /P0   (pin 4)
    private const int IndexG3 = 4;    // /G3   (pin 5)
    private const int IndexP3 = 5;    // /P3   (pin 6)
    private const int IndexPGrp = 6;  // /P    (pin 7)  group propagate, out
    private const int IndexCnZ = 7;   // Cn+z  (pin 9)  carry into slice 3, out
    private const int IndexGGrp = 8;  // /G    (pin 10) group generate, out
    private const int IndexCnY = 9;   // Cn+y  (pin 11) carry into slice 2, out
    private const int IndexCnX = 10;  // Cn+x  (pin 12) carry into slice 1, out
    private const int IndexCn = 11;   // Cn    (pin 13) carry in, asserted LOW
    private const int IndexG2 = 12;   // /G2   (pin 14)
    private const int IndexP2 = 13;   // /P2   (pin 15)

    // Output drivers in declaration order.
    private const int DriverCnX = 0;
    private const int DriverCnY = 1;
    private const int DriverCnZ = 2;
    private const int DriverGGrp = 3;
    private const int DriverPGrp = 4;

    private readonly Net[] nets;
    private readonly Driver[] drivers = new Driver[5];
    private readonly long delayPs;

    public Hc182(
        Net g1, Net p1, Net g0, Net p0,
        Net g3, Net p3,
        Net pGrp, Net cnZ, Net gGrp, Net cnY, Net cnX,
        Net cn, Net g2, Net p2,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below.
        nets = new[]
        {
            g1, p1, g0, p0, g3, p3,
            pGrp, cnZ, gGrp, cnY, cnX,
            cn, g2, p2
        };

        drivers[DriverCnX] = new Driver(nets[IndexCnX], DriveStrength.Strong);
        drivers[DriverCnY] = new Driver(nets[IndexCnY], DriveStrength.Strong);
        drivers[DriverCnZ] = new Driver(nets[IndexCnZ], DriveStrength.Strong);
        drivers[DriverGGrp] = new Driver(nets[IndexGGrp], DriveStrength.Strong);
        drivers[DriverPGrp] = new Driver(nets[IndexPGrp], DriveStrength.Strong);

        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order. Must match the index constants above.
    public IReadOnlyList<int> PinNumbers { get; } = new[]
    {
        1, 2, 3, 4, 5, 6,
        7, 9, 10, 11, 12,
        13, 14, 15
    };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Recompute(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Outputs never feed back through OnInputChanged for combinational
        // chips. Any input change causes a full recompute.
        if (pinIndex == IndexPGrp || pinIndex == IndexGGrp) return;
        if (pinIndex == IndexCnX || pinIndex == IndexCnY || pinIndex == IndexCnZ) return;
        Recompute(scheduler);
    }

    /// <summary>
    /// Read the nine inputs, evaluate the five lookahead equations,
    /// schedule all five output drivers. Called from Initialize and from
    /// any input transition.
    /// </summary>
    private void Recompute(IScheduler scheduler)
    {
        bool g0 = Asserted(IndexG0);
        bool g1 = Asserted(IndexG1);
        bool g2 = Asserted(IndexG2);
        bool g3 = Asserted(IndexG3);
        bool p0 = Asserted(IndexP0);
        bool p1 = Asserted(IndexP1);
        bool p2 = Asserted(IndexP2);
        bool p3 = Asserted(IndexP3);
        bool c = Asserted(IndexCn);

        bool cnX = g0 || (p0 && c);
        bool cnY = g1 || (p1 && g0) || (p1 && p0 && c);
        bool cnZ = g2 || (p2 && g1) || (p2 && p1 && g0) || (p2 && p1 && p0 && c);

        // Group generate deliberately excludes P0 -- faithful to the
        // original '182 (and to GAL_182.pld).
        bool gGrp = g3 || (p3 && g2) || (p3 && p2 && g1) || (p3 && p2 && p1 && g0);
        bool pGrp = p3 && p2 && p1 && p0;

        scheduler.Schedule(delayPs, drivers[DriverCnX], AssertedToSignal(cnX));
        scheduler.Schedule(delayPs, drivers[DriverCnY], AssertedToSignal(cnY));
        scheduler.Schedule(delayPs, drivers[DriverCnZ], AssertedToSignal(cnZ));
        scheduler.Schedule(delayPs, drivers[DriverGGrp], AssertedToSignal(gGrp));
        scheduler.Schedule(delayPs, drivers[DriverPGrp], AssertedToSignal(pGrp));
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Read an active-low pin. True when the pin is LOW (asserted).
    /// Unknown/HighZ map to not asserted.
    /// </summary>
    private bool Asserted(int index) => nets[index].Value == Signal.Low;

    /// <summary>Every output asserts LOW.</summary>
    private static Signal AssertedToSignal(bool asserted)
        => asserted ? Signal.Low : Signal.High;
}