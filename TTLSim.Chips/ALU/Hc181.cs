using TTLSim.Core;

namespace TTLSim.Chips.Alu;

/// <summary>
/// 74HC181 — 4-bit arithmetic/logic unit. The classic "ALU on a chip" used
/// in the CPUs of countless minicomputers (PDP-11 variants, Xerox Alto, Data
/// General Nova, etc.). 24-pin DIP, 22 signal pins, fully combinational.
///
/// DATA CONVENTION — ACTIVE HIGH (CR "181 Model Implements the Active-LOW
/// Column", 2026-07-24). The '181 datasheet publishes two function tables
/// for the same silicon: one reads the pins as active-high data, one as
/// active-low. Every schematic in this project carries TRUE data on the
/// '181 pins, so this model implements the ACTIVE-HIGH DATA table of the
/// CR's normative reference, TI SDLS136 (Dec 1972, revised March 1988).
/// A0..A3 and B0..B3 are read as true data; F0..F3 drive the true result.
///
/// Carry pins are ACTIVE LOW in the active-high-data convention:
///   Cn    pin LOW = logical carry-in of 1 (+1 injected into the selected
///         arithmetic operation). The table rows are written for Cn=H,
///         i.e. no carry.
///   Cn+4  pin LOW = logical carry-out of 1. Wires DIRECTLY to the next
///         slice's Cn with no inverter, and matches the Hc182's Cn+x/y/z
///         outputs, which also assert LOW.
///
/// HISTORY: the previous revision of this model read A/B through an extra
/// pin-level inversion, computed on the recovered values, and inverted F
/// back out — and asserted Cn+4 HIGH. At the pin level that produced the
/// De Morgan dual of every logic row and a flipped carry-out sense: the
/// exact failure signature in the CR's evidence table (AND↔OR swapped,
/// XOR→XNOR, phantom +1 through the inter-slice carry). The fix removes
/// the inversions; the internal tables were already the active-high ones.
/// One transcription error was also corrected: logic row S=0001 read
/// /A + /B (a duplicate of the S=0100 NAND row); the active-high table
/// has /(A+B) there. Verify the 32-row tables below against a paper copy
/// of TI SDLS136 before trusting them further — the hosted PDFs render
/// the tables as images, so they were checked against the CR's canary
/// rows and the two-column duality, not machine-transcribed.
///
/// 16 function select codes (S3..S0), gated by the mode bit M:
///   M=H selects one of 16 LOGIC operations (Cn ignored).
///   M=L selects one of 16 ARITHMETIC operations with carry-in Cn.
///
/// Outputs:
///   F0..F3      main 4-bit result, true data
///   Cn+4        pin 16, ACTIVE-LOW carry out (see above)
///   A=B         open-collector equality output; released (HIGH through
///               the external pull-up) when all four F pins are HIGH,
///               i.e. F = 1111 — the all-ones result that SUB (S=0110,
///               Cn=H) produces for equal operands. Driven LOW otherwise.
///               Wire-AND multiple A=B outputs through a shared pull-up
///               to compare wider operands. Pin-level behaviour is
///               column-independent (per the CR).
///   X, Y        propagate (/P, pin 15) and generate (/G, pin 17) for
///               cascading through a 74182; pin assignment per the ST
///               M74HC181 pin description table (15 = P/X, 16 = Cn+4,
///               17 = G/Y). Both active-LOW; not affected by Cn.
///               Documented only for ADD and SUBTRACT modes; computed via
///               the standard 4-bit carry-lookahead equations for all
///               other operations.
///
/// Propagation delay: the datasheet quotes a spread (20–62 ns depending on
/// path; A=B is the slowest). We use a single 22 ns figure for every
/// output, matching the precedent in the rest of the chip catalogue.
/// </summary>
public sealed class Hc181 : IChip
{
    public const long PropagationDelayPs = 22_000;

    // Indices into nets[] -- the order PinNumbers is declared in below.
    // Pin numbers from ChipPartDefinition.Ic74181.
    private const int IndexB0 = 0;    // B0      (pin 1)
    private const int IndexA0 = 1;    // A0      (pin 2)
    private const int IndexS3 = 2;    // S3      (pin 3)
    private const int IndexS2 = 3;    // S2      (pin 4)
    private const int IndexS1 = 4;    // S1      (pin 5)
    private const int IndexS0 = 5;    // S0      (pin 6)
    private const int IndexCn = 6;    // Cn      (pin 7)  active-LOW carry-in
    private const int IndexM = 7;    // M       (pin 8)
    private const int IndexF0 = 8;    // F0      (pin 9)
    private const int IndexF1 = 9;    // F1      (pin 10)
    private const int IndexF2 = 10;   // F2      (pin 11)
    private const int IndexF3 = 11;   // F3      (pin 13) -- pin 12 = GND
    private const int IndexAeqB = 12; // A=B     (pin 14) open-collector
    private const int IndexY = 13;    // Y = /G  (pin 17) active-low generate
    private const int IndexX = 14;    // X = /P  (pin 15) active-low propagate
    private const int IndexCnP4 = 15; // Cn+4    (pin 16) active-LOW carry-out
    private const int IndexB3 = 16;   // B3      (pin 18)
    private const int IndexA3 = 17;   // A3      (pin 19)
    private const int IndexB2 = 18;   // B2      (pin 20)
    private const int IndexA2 = 19;   // A2      (pin 21)
    private const int IndexB1 = 20;   // B1      (pin 22)
    private const int IndexA1 = 21;   // A1      (pin 23)

    // Output drivers in declaration order: F0, F1, F2, F3, A=B, Y, X, Cn+4.
    private const int DriverF0 = 0;
    private const int DriverF1 = 1;
    private const int DriverF2 = 2;
    private const int DriverF3 = 3;
    private const int DriverAeqB = 4;
    private const int DriverY = 5;
    private const int DriverX = 6;
    private const int DriverCnP4 = 7;

    private readonly Net[] nets;
    private readonly Driver[] drivers = new Driver[8];
    private readonly long delayPs;

    public Hc181(
        Net b0, Net a0,
        Net s3, Net s2, Net s1, Net s0,
        Net cn, Net m,
        Net f0, Net f1, Net f2, Net f3,
        Net aeqb,
        Net y, Net x,
        Net cnP4,
        Net b3, Net a3, Net b2, Net a2, Net b1, Net a1,
        long delayPs = PropagationDelayPs)
    {
        nets = new[]
        {
            b0, a0, s3, s2, s1, s0, cn, m,
            f0, f1, f2, f3,
            aeqb, y, x, cnP4,
            b3, a3, b2, a2, b1, a1
        };

        // Output drivers. A=B is open-collector in real hardware; we model
        // it by driving Low when asserted and HighZ when released, which
        // composes correctly with an external pull-up and with other A=B
        // outputs wire-ANDed together.
        drivers[DriverF0] = new Driver(nets[IndexF0], DriveStrength.Strong);
        drivers[DriverF1] = new Driver(nets[IndexF1], DriveStrength.Strong);
        drivers[DriverF2] = new Driver(nets[IndexF2], DriveStrength.Strong);
        drivers[DriverF3] = new Driver(nets[IndexF3], DriveStrength.Strong);
        drivers[DriverAeqB] = new Driver(nets[IndexAeqB], DriveStrength.Strong);
        drivers[DriverY] = new Driver(nets[IndexY], DriveStrength.Strong);
        drivers[DriverX] = new Driver(nets[IndexX], DriveStrength.Strong);
        drivers[DriverCnP4] = new Driver(nets[IndexCnP4], DriveStrength.Strong);

        this.delayPs = delayPs;
    }

    // Pin numbers in nets[] order. Must match the index constants above.
    public IReadOnlyList<int> PinNumbers { get; } = new[]
    {
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 13,
        14, 17, 15, 16,   // A=B, Y = /G, X = /P, Cn+4 -- see index constants
        18, 19, 20, 21, 22, 23
    };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Recompute(scheduler);

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Outputs never feed back through OnInputChanged for combinational chips.
        // Any input change causes a full recompute -- the chip has no per-bit
        // shortcut, so this is the same amount of work as a targeted update.
        if (pinIndex >= IndexF0 && pinIndex <= IndexF3) return;
        if (pinIndex == IndexAeqB || pinIndex == IndexX || pinIndex == IndexY) return;
        if (pinIndex == IndexCnP4) return;
        Recompute(scheduler);
    }

    /// <summary>
    /// Read inputs, evaluate the selected operation, schedule all eight
    /// output drivers. Called from Initialize and from any input transition.
    /// </summary>
    private void Recompute(IScheduler scheduler)
    {
        // Step 1: read the A and B pins as TRUE data -- HIGH pin = 1.
        // Unknown/HighZ -> 0; a real chip would produce undefined results,
        // but our convention across the catalogue is "treat weird inputs as
        // Low and rely on TTL011 to surface the floating pin at design time."
        int a = BitFromActiveHighPin(IndexA0)
              | (BitFromActiveHighPin(IndexA1) << 1)
              | (BitFromActiveHighPin(IndexA2) << 2)
              | (BitFromActiveHighPin(IndexA3) << 3);
        int b = BitFromActiveHighPin(IndexB0)
              | (BitFromActiveHighPin(IndexB1) << 1)
              | (BitFromActiveHighPin(IndexB2) << 2)
              | (BitFromActiveHighPin(IndexB3) << 3);

        // Step 2: read S, M from their (active-high) pins.
        int s = BitFromActiveHighPin(IndexS0)
              | (BitFromActiveHighPin(IndexS1) << 1)
              | (BitFromActiveHighPin(IndexS2) << 2)
              | (BitFromActiveHighPin(IndexS3) << 3);
        bool modeLogic = nets[IndexM].Value == Signal.High;

        // Cn is ACTIVE LOW in the active-high-data convention: pin LOW
        // injects +1 into the arithmetic operation. The table rows are
        // written for Cn=H ("no carry"), so carryIn is 1 when the pin is
        // LOW, 0 otherwise.
        int carryIn = nets[IndexCn].Value == Signal.Low ? 1 : 0;

        // Step 3: compute the result. For logic mode the unbounded value is
        // just the 4-bit logic output (no carry chain); for arithmetic mode
        // it's a 5-bit unbounded value whose high bit is the carry-out.
        int unbounded = modeLogic
            ? ComputeLogic(s, a, b)
            : ComputeArithmetic(s, a, b, carryIn);

        int f = unbounded & 0xF;             // true 4-bit result
        int cnP4 = modeLogic ? 0 : (unbounded >> 4) & 1;

        // Step 4: F0..F3 carry the true result directly.
        scheduler.Schedule(delayPs, drivers[DriverF0], BitToSignal((f >> 0) & 1));
        scheduler.Schedule(delayPs, drivers[DriverF1], BitToSignal((f >> 1) & 1));
        scheduler.Schedule(delayPs, drivers[DriverF2], BitToSignal((f >> 2) & 1));
        scheduler.Schedule(delayPs, drivers[DriverF3], BitToSignal((f >> 3) & 1));

        // Cn+4 is ACTIVE LOW: drive LOW when the operation carries out of
        // bit 3. In logic mode the internal carry chain is inhibited and
        // the pin sits deasserted (HIGH).
        scheduler.Schedule(delayPs, drivers[DriverCnP4],
            cnP4 != 0 ? Signal.Low : Signal.High);

        // Step 5: A=B is open-collector. Released (HighZ, pulled high by
        // the external pull-up) when all four F pins are HIGH -- with true
        // data on the pins that is F = 1111, the all-ones result SUB gives
        // for equal operands with no injected carry. Pulled LOW otherwise.
        scheduler.Schedule(delayPs, drivers[DriverAeqB],
            f == 0xF ? Signal.HighZ : Signal.Low);

        // Step 6: P and G (X and Y pins). Active-low, not affected by Cn.
        // Datasheet defines their behavioural meaning only for ADD and SUB;
        // for other ops we still emit the standard carry-lookahead values
        // (computed from the true pin data under the currently-selected
        // operation's arithmetic transformation), which is what real
        // cascade hardware would see -- it just isn't documented as useful.
        (bool pAsserted, bool gAsserted) = ComputeCarryLookahead(s, a, b, modeLogic);
        scheduler.Schedule(delayPs, drivers[DriverX], pAsserted ? Signal.Low : Signal.High);
        scheduler.Schedule(delayPs, drivers[DriverY], gAsserted ? Signal.Low : Signal.High);
    }


    // -------------------------------------------------------- function table

    /// <summary>
    /// All 16 logic operations from the ACTIVE-HIGH DATA column of the
    /// function table, TI SDLS136 (M=H column). Indexed by the 4-bit S code
    /// S3..S0 read as S3*8 + S2*4 + S1*2 + S0. Operands and result are true
    /// data, matching the pins directly.
    /// </summary>
    private static int ComputeLogic(int s, int a, int b)
    {
        // Helpers to keep the table readable.
        int notA = (~a) & 0xF;
        int notB = (~b) & 0xF;

        return s switch
        {
            0b0000 => notA,                       // /A
            0b0001 => notA & notB,                // /(A+B)  -- NOR
            0b0010 => notA & b,                   // /A . B
            0b0011 => 0,                          // Logic 0
            0b0100 => (~(a & b)) & 0xF,           // /(A.B)  -- NAND
            0b0101 => notB,                       // /B
            0b0110 => (a ^ b),                    // A xor B
            0b0111 => (a & notB) & 0xF,           // A . /B
            0b1000 => (notA | b) & 0xF,           // /A + B
            0b1001 => (~(a ^ b)) & 0xF,           // A xnor B
            0b1010 => b,                          // B
            0b1011 => a & b,                      // A . B
            0b1100 => 0xF,                        // Logic 1 (all ones)
            0b1101 => (a | notB) & 0xF,           // A + /B
            0b1110 => (a | b) & 0xF,              // A + B
            0b1111 => a,                          // A
            _ => 0
        };
    }

    /// <summary>
    /// All 16 arithmetic operations from the ACTIVE-HIGH DATA column of the
    /// function table, TI SDLS136 (M=L, Cn=H header). Each row is the
    /// result with NO carry in; the caller supplies a carryIn of 1 to inject
    /// +1 (corresponding to Cn pin LOW). Returns the unbounded 5-bit result
    /// so the caller can split out the carry.
    /// </summary>
    private static int ComputeArithmetic(int s, int a, int b, int carryIn)
    {
        // Per-row operands for the datasheet's active-high arithmetic table.
        // Computed at full precision (potentially negative or > 15); the
        // caller masks to 4 bits and reads bit 4 as the carry-out.
        int notB = (~b) & 0xF;
        int aOrB = (a | b) & 0xF;
        int aOrNotB = (a | notB) & 0xF;

        // The expressions below must keep the unbounded result non-negative
        // so the caller can read bit 4 as the carry directly. Real silicon
        // does subtraction as one's-complement addition: "A minus B minus 1"
        // is computed as A + ~B (in 4-bit space, which is 0..15), and the
        // carry-out from that 5-bit sum is the chip's carry-out.
        //
        // Equivalence note: `a - b - 1` and `a + notB` produce the same 4-bit
        // result modulo 16, but only the second form correctly reflects the
        // chip's carry-out on bit 4 (the first form sign-extends when the
        // result is negative, giving the wrong carry). Use the second form
        // everywhere a "minus" operation appears, including the "minus 1"
        // rows where the implicit "minus operand" is 0 (so we add ~0 = 0xF).
        //
        // "An incoming carry adds a one to each operation" -- carryIn is
        // added on top of the silicon-equivalent sum.
        return s switch
        {
            0b0000 => a + carryIn,                          // A
            0b0001 => aOrB + carryIn,                       // A + B (logical OR)
            0b0010 => aOrNotB + carryIn,                    // A + /B
            0b0011 => 0xF + carryIn,                        // minus 1  (0 + ~0 + Cn)
            0b0100 => a + (a & notB) + carryIn,             // A plus A./B
            0b0101 => aOrB + (a & notB) + carryIn,          // (A+B) plus A./B
            0b0110 => a + notB + carryIn,                   // A minus B minus 1  (A + ~B + Cn)
            0b0111 => (a & notB) + 0xF + carryIn,           // A./B minus 1
            0b1000 => a + (a & b) + carryIn,                // A plus A.B
            0b1001 => a + b + carryIn,                      // A plus B
            0b1010 => aOrNotB + (a & b) + carryIn,          // (A+/B) plus A.B
            0b1011 => (a & b) + 0xF + carryIn,              // A.B minus 1
            0b1100 => a + a + carryIn,                      // A plus A (left shift)
            0b1101 => aOrB + a + carryIn,                   // (A+B) plus A
            0b1110 => aOrNotB + a + carryIn,                // (A+/B) plus A
            0b1111 => a + 0xF + carryIn,                    // A minus 1
            _ => 0
        };
    }

    // -------------------------------------------------- carry lookahead (P, G)

    /// <summary>
    /// Compute P (propagate, X pin) and G (generate, Y pin). Returns whether
    /// each is asserted (i.e., whether the active-low pin should be driven LOW).
    /// Per the datasheet these are unaffected by Cn.
    ///
    /// The datasheet documents P and G's behavioural meaning only for two
    /// rows: ADD (S=1001, "A plus B") and SUBTRACT (S=0110, "A minus B
    /// minus 1"). We implement those two correctly and provide defined-but-
    /// not-externally-meaningful values for the other 14 arithmetic rows
    /// (and all 16 logic rows, where the chip's internal carry chain is
    /// disabled anyway).
    ///
    /// This is enough for the CPU built on this simulator -- cascading
    /// through a '182 is only documented for ADD and SUB, which is exactly
    /// the set we have correct.
    /// </summary>
    private static (bool propagate, bool generate) ComputeCarryLookahead(
        int s, int a, int b, bool modeLogic)
    {
        // ADD: P=LOW when A+B >= 15, G=LOW when A+B >= 16.
        if (!modeLogic && s == 0b1001)
        {
            int sum = a + b;
            return (sum >= 15, sum >= 16);
        }

        // SUBTRACT: F = A + /B + Cn (A minus B minus 1, plus injected carry).
        // P=LOW when A >= B (A + /B >= 15); G=LOW when A > B (A + /B >= 16).
        // These satisfy the cascade identity "carry-out = G + P.carry-in"
        // against this model's carry-out (asserted iff A + /B + Cn >= 16) --
        // the identity a '182 (or the GAL-182) relies on. "A <= B / A < B"
        // is the ACTIVE-LOW-data description of the same pin condition;
        // applying it to the true pin data inverts the polarity and breaks
        // '182-carried subtraction.
        if (!modeLogic && s == 0b0110)
        {
            return (a >= b, a > b);
        }

        // Every other operation: derive a defined value from the per-bit
        // propagate/generate equations that the chip's internal gates
        // produce. The standard 4-bit lookahead at Cn=0 reduces to:
        //   p_i = a_i | b_i,   g_i = a_i & b_i
        //   P = p3 & p2 & p1 & p0
        //   G = g3 | (p3 & g2) | (p3 & p2 & g1) | (p3 & p2 & p1 & g0)
        // This is what real silicon presents for these unsupported rows.
        // External hardware that cascades non-ADD/SUB operations through a
        // '182 is non-standard; this value is here so the chip model is
        // deterministic, not because anyone is expected to rely on it.
        int p = 0, g = 0;
        for (int i = 0; i < 4; i++)
        {
            int ai = (a >> i) & 1;
            int bi = (b >> i) & 1;
            p |= (ai | bi) << i;
            g |= (ai & bi) << i;
        }
        bool P = ((p & 0xF) == 0xF);
        bool G = ((g >> 3) & 1) != 0
              || (((p >> 3) & 1) != 0 && ((g >> 2) & 1) != 0)
              || (((p >> 3) & 1) != 0 && ((p >> 2) & 1) != 0 && ((g >> 1) & 1) != 0)
              || (((p >> 3) & 1) != 0 && ((p >> 2) & 1) != 0 && ((p >> 1) & 1) != 0 && ((g >> 0) & 1) != 0);
        return (P, G);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Read a pin as true data. Returns 1 when the pin is HIGH, 0 otherwise.
    /// Unknown/HighZ map to 0.
    /// </summary>
    private int BitFromActiveHighPin(int index)
        => nets[index].Value == Signal.High ? 1 : 0;

    private static Signal BitToSignal(int bit) => bit != 0 ? Signal.High : Signal.Low;
}
