using TTLSim.Chips.Alu;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74181 ALU model, ACTIVE-HIGH data convention (CR "181
/// Model Implements the Active-LOW Column", 2026-07-24). The chip is purely
/// combinational, so the test rig has no clock: each test builds a sim with
/// every input wired to either VccDriver or GndDriver, runs it to
/// quiescence, and reads the resulting output nets.
///
/// The A/B/F pins carry TRUE data, so the rig drives and reads them
/// directly -- no pin-level inversion anywhere. The carry pins are active
/// LOW: Cn pin LOW = carry-in of 1, Cn+4 pin LOW = carry-out of 1.
///
/// Coverage per the CR's acceptance section:
///   1. Full sweep of all 32 S/M rows, both Cn states, several operand
///      patterns, against reference tables transcribed independently of
///      the model (Expected* methods below).
///   2. Wrong-column canary rows as named spot tests: 1011/1 -> A.B,
///      1110/1 -> A+B, 0110/1 -> A xor B, 0000/0 -> A plus Cn,
///      1111/0 -> A minus 1 plus Cn.
///   3. Carry sense: F+1 with Cn pin HIGH vs LOW, and Cn+4 asserting LOW
///      on overflow (the 4-bit unit analogue of the CR's 0F+01 / FF+01).
///   4. A=B releases only when all four F pins are HIGH (F = 1111), the
///      equality signature of SUB with no injected carry.
/// </summary>
public class Hc181Tests
{
    // ----------------------------------------------------------------- rig

    private sealed class Rig
    {
        public Net A0 = null!, A1 = null!, A2 = null!, A3 = null!;
        public Net B0 = null!, B1 = null!, B2 = null!, B3 = null!;
        public Net S0 = null!, S1 = null!, S2 = null!, S3 = null!;
        public Net M = null!, Cn = null!;
        public Net F0 = null!, F1 = null!, F2 = null!, F3 = null!;
        public Net AeqB = null!, X = null!, Y = null!, CnP4 = null!;

        public Simulator Sim = null!;

        public int ReadF()
        {
            // F pins carry true data: HIGH pin = 1.
            int f = 0;
            if (F0.Value == Signal.High) f |= 1;
            if (F1.Value == Signal.High) f |= 2;
            if (F2.Value == Signal.High) f |= 4;
            if (F3.Value == Signal.High) f |= 8;
            return f;
        }

        /// <summary>Logical carry-out. The Cn+4 pin is active LOW.</summary>
        public bool ReadCarryOut() => CnP4.Value == Signal.Low;

        public bool ReadAEqualsB() => AeqB.Value == Signal.High;
        public bool ReadPropagate() => X.Value == Signal.Low;  // active-low
        public bool ReadGenerate() => Y.Value == Signal.Low;   // active-low
    }

    /// <summary>
    /// Build a '181 with every signal pin driven from a static source.
    /// a and b are true 4-bit operand values driven straight onto the pins.
    /// s is the raw 4-bit S code on S3..S0 (active-high). modeLogic=true
    /// drives M high; carryIn=true drives the Cn pin LOW (carry asserted --
    /// the pin is active LOW).
    /// </summary>
    private static Rig Build(int a, int b, int s, bool modeLogic, bool carryIn)
    {
        Rig r = new();
        int id = 0;
        Net N() => new(id++);

        r.A0 = N(); r.A1 = N(); r.A2 = N(); r.A3 = N();
        r.B0 = N(); r.B1 = N(); r.B2 = N(); r.B3 = N();
        r.S0 = N(); r.S1 = N(); r.S2 = N(); r.S3 = N();
        r.M = N(); r.Cn = N();
        r.F0 = N(); r.F1 = N(); r.F2 = N(); r.F3 = N();
        r.AeqB = N(); r.X = N(); r.Y = N(); r.CnP4 = N();

        Hc181 alu = new(
            b0: r.B0, a0: r.A0,
            s3: r.S3, s2: r.S2, s1: r.S1, s0: r.S0,
            cn: r.Cn, m: r.M,
            f0: r.F0, f1: r.F1, f2: r.F2, f3: r.F3,
            aeqb: r.AeqB,
            y: r.Y, x: r.X,
            cnP4: r.CnP4,
            b3: r.B3, a3: r.A3, b2: r.B2, a2: r.A2, b1: r.B1, a1: r.A1);

        // Operand pins carry true data: drive HIGH when the bit is 1.
        List<IChip> chips = new() { alu };
        DriveActiveHighBit(chips, r.A0, a, 0);
        DriveActiveHighBit(chips, r.A1, a, 1);
        DriveActiveHighBit(chips, r.A2, a, 2);
        DriveActiveHighBit(chips, r.A3, a, 3);
        DriveActiveHighBit(chips, r.B0, b, 0);
        DriveActiveHighBit(chips, r.B1, b, 1);
        DriveActiveHighBit(chips, r.B2, b, 2);
        DriveActiveHighBit(chips, r.B3, b, 3);

        // S, M are active-high.
        DriveActiveHighBit(chips, r.S0, s, 0);
        DriveActiveHighBit(chips, r.S1, s, 1);
        DriveActiveHighBit(chips, r.S2, s, 2);
        DriveActiveHighBit(chips, r.S3, s, 3);
        if (modeLogic) chips.Add(new VccDriver(r.M));
        else chips.Add(new GndDriver(r.M));

        // Cn pin is active LOW: LOW asserts carry-in.
        if (carryIn) chips.Add(new GndDriver(r.Cn));
        else chips.Add(new VccDriver(r.Cn));

        // The A=B output is open-collector. Real hardware wires it through
        // a pull-up resistor to VCC; in the sim the resistor becomes a Weak
        // VccDriver on the same net so "released" reads as High.
        Driver pullup = new(r.AeqB, DriveStrength.Weak);
        chips.Add(new StaticWeakDriver(pullup, Signal.High));

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips);
        r.Sim.Start();
        r.Sim.RunUntilQuiescent();
        return r;
    }

    private static void DriveActiveHighBit(List<IChip> chips, Net net, int value, int bit)
    {
        bool asserted = ((value >> bit) & 1) != 0;
        chips.Add(asserted ? (IChip)new VccDriver(net) : new GndDriver(net));
    }

    /// <summary>
    /// Tiny inline driver that schedules a single value onto a pre-existing
    /// Driver at Initialize. Lets the rig install a weak pull-up on the A=B
    /// net without dragging in a dedicated pull-up resistor type.
    /// </summary>
    private sealed class StaticWeakDriver : IChip
    {
        private readonly Driver driver;
        private readonly Signal value;

        public StaticWeakDriver(Driver driver, Signal value)
        {
            this.driver = driver;
            this.value = value;
        }

        public IReadOnlyList<int> PinNumbers { get; } = System.Array.Empty<int>();
        public IReadOnlyList<Net> Nets { get; } = System.Array.Empty<Net>();
        public void Initialize(IScheduler scheduler) => scheduler.Schedule(0, driver, value);
        public void OnInputChanged(int pinIndex, IScheduler scheduler) { }
    }

    // -------------------------------------------- reference function tables
    //
    // Transcribed here independently of the model so the sweep is a real
    // cross-check, not a tautology. Same source: TI SDLS136, ACTIVE-HIGH
    // DATA column. If the model's table is ever "fixed" to the wrong
    // column again, every logic row except 0011/0110's twin/1100/1111
    // fails loudly here.

    private static int ExpectedLogic(int s, int a, int b)
    {
        int notA = (~a) & 0xF;
        int notB = (~b) & 0xF;
        return s switch
        {
            0b0000 => notA,                       // /A
            0b0001 => notA & notB,                // /(A+B)
            0b0010 => notA & b,                   // /A . B
            0b0011 => 0,                          // Logic 0
            0b0100 => (~(a & b)) & 0xF,           // /(A.B)
            0b0101 => notB,                       // /B
            0b0110 => a ^ b,                      // A xor B
            0b0111 => a & notB,                   // A . /B
            0b1000 => (notA | b) & 0xF,           // /A + B
            0b1001 => (~(a ^ b)) & 0xF,           // A xnor B
            0b1010 => b,                          // B
            0b1011 => a & b,                      // A . B
            0b1100 => 0xF,                        // Logic 1
            0b1101 => (a | notB) & 0xF,           // A + /B
            0b1110 => (a | b) & 0xF,              // A + B
            0b1111 => a,                          // A
            _ => 0
        };
    }

    /// <summary>Unbounded 5-bit result; bit 4 is the logical carry-out.</summary>
    private static int ExpectedArithmetic(int s, int a, int b, int cin)
    {
        int notB = (~b) & 0xF;
        int aOrB = (a | b) & 0xF;
        int aOrNotB = (a | notB) & 0xF;
        return s switch
        {
            0b0000 => a + cin,                              // A
            0b0001 => aOrB + cin,                           // A + B
            0b0010 => aOrNotB + cin,                        // A + /B
            0b0011 => 0xF + cin,                            // minus 1
            0b0100 => a + (a & notB) + cin,                 // A plus A./B
            0b0101 => aOrB + (a & notB) + cin,              // (A+B) plus A./B
            0b0110 => a + notB + cin,                       // A minus B minus 1
            0b0111 => (a & notB) + 0xF + cin,               // A./B minus 1
            0b1000 => a + (a & b) + cin,                    // A plus A.B
            0b1001 => a + b + cin,                          // A plus B
            0b1010 => aOrNotB + (a & b) + cin,              // (A+/B) plus A.B
            0b1011 => (a & b) + 0xF + cin,                  // A.B minus 1
            0b1100 => a + a + cin,                          // A plus A
            0b1101 => aOrB + a + cin,                       // (A+B) plus A
            0b1110 => aOrNotB + a + cin,                    // (A+/B) plus A
            0b1111 => a + 0xF + cin,                        // A minus 1
            _ => 0
        };
    }

    // Operand pairs chosen to exercise every bit position, equal operands,
    // complements, and asymmetric values (order-sensitivity in A./B rows).
    private static readonly (int A, int B)[] OperandPairs =
    {
        (0x0, 0x0), (0xF, 0xF), (0xC, 0xA), (0xA, 0xC),
        (0x3, 0x5), (0xF, 0x0), (0x0, 0xF), (0x9, 0x6),
        (0x5, 0x5), (0xA, 0x5), (0x1, 0xE), (0x7, 0x8)
    };

    // ------------------------------------------------------------- sweeps

    [Fact]
    public void Logic_sweep_all_16_rows()
    {
        foreach ((int a, int b) in OperandPairs)
            for (int s = 0; s < 16; s++)
                foreach (bool carryIn in new[] { false, true })
                {
                    var r = Build(a, b, s, modeLogic: true, carryIn: carryIn);
                    int expected = ExpectedLogic(s, a, b);
                    Assert.True(expected == r.ReadF(),
                        $"logic S={s:B4} A={a:X1} B={b:X1} Cn={(carryIn ? "L" : "H")}: " +
                        $"got F={r.ReadF():X1}, expected {expected:X1}");
                    // Cn must be ignored in logic mode, and the carry chain
                    // is inhibited: Cn+4 sits deasserted (pin HIGH).
                    Assert.False(r.ReadCarryOut());
                    // A=B is a pin-level fact: released iff F pins all HIGH.
                    Assert.True((expected == 0xF) == r.ReadAEqualsB(),
                        $"logic S={s:B4} A={a:X1} B={b:X1}: A=B mismatch for F={expected:X1}");
                }
    }

    [Fact]
    public void Arithmetic_sweep_all_16_rows_both_carry_states()
    {
        foreach ((int a, int b) in OperandPairs)
            for (int s = 0; s < 16; s++)
                foreach (bool carryIn in new[] { false, true })
                {
                    var r = Build(a, b, s, modeLogic: false, carryIn: carryIn);
                    int unbounded = ExpectedArithmetic(s, a, b, carryIn ? 1 : 0);
                    int expectedF = unbounded & 0xF;
                    bool expectedCarry = ((unbounded >> 4) & 1) != 0;
                    Assert.True(expectedF == r.ReadF() && expectedCarry == r.ReadCarryOut(),
                        $"arith S={s:B4} A={a:X1} B={b:X1} Cn={(carryIn ? "L(carry)" : "H")}: " +
                        $"got F={r.ReadF():X1} Cout={r.ReadCarryOut()}, " +
                        $"expected F={expectedF:X1} Cout={expectedCarry}");
                    Assert.True((expectedF == 0xF) == r.ReadAEqualsB(),
                        $"arith S={s:B4} A={a:X1} B={b:X1}: A=B mismatch for F={expectedF:X1}");
                }
    }

    // ------------------------------------- wrong-column canaries (CR §Acceptance)

    [Fact]
    public void Canary_logic_1011_is_AND_not_OR()
    {
        var r = Build(a: 0xC, b: 0xA, s: 0b1011, modeLogic: true, carryIn: false);
        Assert.Equal(0x8, r.ReadF());
    }

    [Fact]
    public void Canary_logic_1110_is_OR_not_AND()
    {
        var r = Build(a: 0xC, b: 0xA, s: 0b1110, modeLogic: true, carryIn: false);
        Assert.Equal(0xE, r.ReadF());
    }

    [Fact]
    public void Canary_logic_0110_is_XOR_not_XNOR()
    {
        var r = Build(a: 0xC, b: 0xA, s: 0b0110, modeLogic: true, carryIn: false);
        Assert.Equal(0x6, r.ReadF());
    }

    [Fact]
    public void Canary_logic_0001_is_NOR_not_NAND()
    {
        // The row the old table mis-transcribed: /(A+B), not /A + /B.
        var r = Build(a: 0xC, b: 0xA, s: 0b0001, modeLogic: true, carryIn: false);
        Assert.Equal(0x1, r.ReadF());   // ~(C|A) = ~E = 1
    }

    [Fact]
    public void Canary_arith_0000_is_A_plus_Cn_not_A_minus_1()
    {
        var r = Build(a: 0xA, b: 0x3, s: 0b0000, modeLogic: false, carryIn: true);
        Assert.Equal(0xB, r.ReadF());
    }

    [Fact]
    public void Canary_arith_1111_is_A_minus_1_plus_Cn_not_A()
    {
        var r = Build(a: 0xA, b: 0x3, s: 0b1111, modeLogic: false, carryIn: false);
        Assert.Equal(0x9, r.ReadF());
    }

    [Fact]
    public void Canary_MOV_add_with_B_killed_has_no_phantom_plus_one()
    {
        // ADD, B=0, Cn pin HIGH (no carry): F must be exactly A.
        var r = Build(a: 0xA, b: 0x0, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.Equal(0xA, r.ReadF());
    }

    // ------------------------------------------------ carry sense (CR §Acceptance 2)

    [Fact]
    public void Add_F_plus_1_carry_pin_high_gives_zero_and_asserts_carry_out()
    {
        // 4-bit unit analogue of the CR's 0F+01: Cn pin HIGH = no carry in.
        // F wraps to 0 and Cn+4 must go LOW (carry asserted).
        var r = Build(a: 0xF, b: 0x1, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.Equal(0x0, r.ReadF());
        Assert.True(r.ReadCarryOut());
        Assert.Equal(Signal.Low, r.CnP4.Value);   // pin-level: active LOW
    }

    [Fact]
    public void Add_F_plus_1_carry_pin_low_gives_one()
    {
        var r = Build(a: 0xF, b: 0x1, s: 0b1001, modeLogic: false, carryIn: true);
        Assert.Equal(0x1, r.ReadF());
        Assert.True(r.ReadCarryOut());
    }

    [Fact]
    public void Add_three_plus_five_is_eight_no_carry_out()
    {
        var r = Build(a: 3, b: 5, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.Equal(8, r.ReadF());
        Assert.False(r.ReadCarryOut());
        Assert.Equal(Signal.High, r.CnP4.Value);  // deasserted = pin HIGH
    }

    [Fact]
    public void Add_with_carry_in_adds_one()
    {
        // 3 + 5 + 1 = 9
        var r = Build(a: 3, b: 5, s: 0b1001, modeLogic: false, carryIn: true);
        Assert.Equal(9, r.ReadF());
        Assert.False(r.ReadCarryOut());
    }

    [Fact]
    public void Subtract_seven_minus_three_with_carry_in_is_four()
    {
        // S=0110 is "A minus B minus 1"; Cn asserted (pin LOW) adds 1,
        // producing A minus B. 7 - 3 = 4. Carry-out asserted == no borrow.
        var r = Build(a: 7, b: 3, s: 0b0110, modeLogic: false, carryIn: true);
        Assert.Equal(4, r.ReadF());
        Assert.True(r.ReadCarryOut());
    }

    [Fact]
    public void Subtract_three_minus_seven_underflows_with_no_carry_out()
    {
        // 3 - 7 = -4 = 0xC in 4-bit two's complement. Borrow = carry-out
        // deasserted (Cn+4 pin HIGH).
        var r = Build(a: 3, b: 7, s: 0b0110, modeLogic: false, carryIn: true);
        Assert.Equal(0xC, r.ReadF());
        Assert.False(r.ReadCarryOut());
    }

    // --------------------------------------------------------------- A=B

    [Fact]
    public void Sub_equal_operands_no_carry_in_gives_all_ones_and_releases_AeqB()
    {
        // Equality idiom: SUB with Cn pin HIGH. A minus A minus 1 = -1 =
        // all ones -- every F pin HIGH, so the open-collector A=B releases
        // and the pull-up reads it High. (CR integration row v7: F=FF,
        // AEQB=1.)
        var r = Build(a: 5, b: 5, s: 0b0110, modeLogic: false, carryIn: false);
        Assert.Equal(0xF, r.ReadF());
        Assert.True(r.ReadAEqualsB());
    }

    [Fact]
    public void Sub_unequal_operands_pull_AeqB_low()
    {
        var r = Build(a: 5, b: 3, s: 0b0110, modeLogic: false, carryIn: false);
        Assert.Equal(0x1, r.ReadF());   // 5 - 3 - 1
        Assert.False(r.ReadAEqualsB());
    }

    [Fact]
    public void Sub_equal_operands_with_carry_in_gives_zero_and_AeqB_stays_low()
    {
        // A=B is NOT a zero detect: with the injected carry the result is
        // 0, the F pins are all LOW, and A=B stays driven LOW. Zero detect
        // in the module is the '688's job.
        var r = Build(a: 5, b: 5, s: 0b0110, modeLogic: false, carryIn: true);
        Assert.Equal(0x0, r.ReadF());
        Assert.False(r.ReadAEqualsB());
    }

    // -------------------------------------------------------- logic mode

    [Fact]
    public void Logic_mode_holds_carry_out_deasserted()
    {
        // Even with inputs that would generate carry in arithmetic mode,
        // M=H must leave Cn+4 deasserted -- pin HIGH (carry chain disabled).
        var r = Build(a: 0xF, b: 0xF, s: 0b1110, modeLogic: true, carryIn: false);
        Assert.False(r.ReadCarryOut());
        Assert.Equal(Signal.High, r.CnP4.Value);
    }

    // ------------------------------------------------- carry lookahead

    [Fact]
    public void Add_propagate_asserts_when_sum_is_fifteen_or_more()
    {
        // 7 + 8 = 15: P asserts (X pin LOW), G does not.
        var r = Build(a: 7, b: 8, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.True(r.ReadPropagate());
        Assert.False(r.ReadGenerate());
    }

    [Fact]
    public void Add_generate_asserts_when_sum_is_sixteen_or_more()
    {
        // 8 + 8 = 16: G asserts.
        var r = Build(a: 8, b: 8, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.True(r.ReadGenerate());
    }

    [Fact]
    public void Sub_both_assert_when_A_gt_B()
    {
        // A=5, B=3: A + /B = 17 >= 16 -- both P and G assert.
        var r = Build(a: 5, b: 3, s: 0b0110, modeLogic: false, carryIn: false);
        Assert.True(r.ReadPropagate());
        Assert.True(r.ReadGenerate());
    }

    [Fact]
    public void Sub_neither_asserts_when_A_lt_B()
    {
        // A=3, B=5: A + /B = 13 < 15 -- no carry out even with carry in,
        // so neither P nor G asserts. This is the polarity canary: an
        // Hc181 with the inverted (<=, <) SUB convention asserts BOTH here.
        var r = Build(a: 3, b: 5, s: 0b0110, modeLogic: false, carryIn: false);
        Assert.False(r.ReadPropagate());
        Assert.False(r.ReadGenerate());
    }

    [Fact]
    public void Lookahead_invariant_holds_for_all_arithmetic_codes()
    {
        // CR "'181 P̄/Ḡ Exports Wrong for S=1100" acceptance 1: for every
        // arithmetic S code, all 256 operand pairs, both Cn states, the
        // exports must satisfy the lookahead identity against the model's
        // OWN carry out:
        //     Cout == G or (P and Cn)
        // (with the T substitution on the P̄ pin the identity still holds
        // verbatim: at Cn=1 it reads Cout == T, which is T's definition).
        // 8,192 checks. This is the property that makes a paired '182
        // agree with ripple for every code, not just ADD and SUB.
        for (int s = 0; s < 16; s++)
            for (int a = 0; a < 16; a++)
                for (int b = 0; b < 16; b++)
                    foreach (bool carryIn in new[] { false, true })
                    {
                        var r = Build(a, b, s, modeLogic: false, carryIn: carryIn);
                        bool cout = r.ReadCarryOut();
                        bool g = r.ReadGenerate();
                        bool p = r.ReadPropagate();
                        Assert.True(cout == (g || (p && carryIn)),
                            $"invariant S={s:B4} A={a:X1} B={b:X1} " +
                            $"Cn={(carryIn ? "L(carry)" : "H")}: " +
                            $"Cout={cout} G={g} P={p}");
                    }
    }

    [Fact]
    public void Discriminator_double_of_eight_exports_generate()
    {
        // The CR's failing shape at unit level: S=1100 (A plus A), A=8 --
        // 8+8 wraps, so the slice MUST export generate for a paired '182
        // to see the carry. B is ignored by this row; drive it nonzero to
        // prove that. (Module-level: RLC A=81, high slice.)
        var r = Build(a: 0x8, b: 0x3, s: 0b1100, modeLogic: false, carryIn: false);
        Assert.Equal(0x0, r.ReadF());
        Assert.True(r.ReadCarryOut());
        Assert.True(r.ReadGenerate());
        Assert.True(r.ReadPropagate());   // T = G or P; G alone suffices
    }

    [Fact]
    public void Discriminator_double_of_four_exports_nothing()
    {
        // S=1100, A=4: 4+4(+1) never reaches 16 -- no carry, and neither
        // export may assert. (Module-level: the A=41 control vector.)
        var r = Build(a: 0x4, b: 0x3, s: 0b1100, modeLogic: false, carryIn: false);
        Assert.Equal(0x8, r.ReadF());
        Assert.False(r.ReadCarryOut());
        Assert.False(r.ReadGenerate());
        Assert.False(r.ReadPropagate());
    }
}
