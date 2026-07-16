using TTLSim.Chips.Alu;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74181 ALU model. The chip is purely combinational, so the
/// test rig has no clock: each test builds a sim with every input wired to
/// either VccDriver or GndDriver, runs it to quiescence, and reads the
/// resulting output nets.
///
/// All test inputs are expressed in active-HIGH terms (the convention the
/// user thinks in) -- the rig inverts at the pin level before driving, and
/// inverts again when reading the /F outputs, so the assertions in each
/// test read like "A=3, B=5, ADD, expect F=8" rather than reasoning about
/// active-low encodings.
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

        // The A=B pin is open-collector; its net needs a pull-up so that
        // "released" reads as High rather than HighZ. The test rig models
        // this with a VccDriver on the same net at Weak strength.
        public Net AeqBPullup = null!;

        public Simulator Sim = null!;

        public int ReadF()
        {
            // /F is active-low; convert to active-high integer.
            int notF = 0;
            if (F0.Value == Signal.High) notF |= 1;
            if (F1.Value == Signal.High) notF |= 2;
            if (F2.Value == Signal.High) notF |= 4;
            if (F3.Value == Signal.High) notF |= 8;
            return (~notF) & 0xF;
        }

        public bool ReadCarryOut() => CnP4.Value == Signal.High;
        public bool ReadAEqualsB() => AeqB.Value == Signal.High;
        public bool ReadPropagate() => X.Value == Signal.Low;  // active-low
        public bool ReadGenerate() => Y.Value == Signal.Low;   // active-low
    }

    /// <summary>
    /// Build a '181 with every signal pin driven from a static source.
    /// Inputs are given as active-HIGH 4-bit ints for A and B; the rig
    /// converts to /A and /B at the pin level. s is the raw 4-bit S code
    /// (the same value that goes on S3..S0 as active-high). modeLogic=true
    /// drives M high; carryIn=true drives Cn low (which means "carry asserted"
    /// in the active-high-input convention).
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

        // Active-low operand pins: drive LOW when the bit is 1, HIGH when 0.
        List<IChip> chips = new() { alu };
        DriveActiveLowBit(chips, r.A0, a, 0);
        DriveActiveLowBit(chips, r.A1, a, 1);
        DriveActiveLowBit(chips, r.A2, a, 2);
        DriveActiveLowBit(chips, r.A3, a, 3);
        DriveActiveLowBit(chips, r.B0, b, 0);
        DriveActiveLowBit(chips, r.B1, b, 1);
        DriveActiveLowBit(chips, r.B2, b, 2);
        DriveActiveLowBit(chips, r.B3, b, 3);

        // S, M are active-high.
        DriveActiveHighBit(chips, r.S0, s, 0);
        DriveActiveHighBit(chips, r.S1, s, 1);
        DriveActiveHighBit(chips, r.S2, s, 2);
        DriveActiveHighBit(chips, r.S3, s, 3);
        if (modeLogic) chips.Add(new VccDriver(r.M));
        else chips.Add(new GndDriver(r.M));

        // Cn pin: LOW asserts carry-in (active-high convention).
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

    private static void DriveActiveLowBit(List<IChip> chips, Net net, int value, int bit)
    {
        bool asserted = ((value >> bit) & 1) != 0;
        // active-low: assert means drive LOW
        chips.Add(asserted ? (IChip)new GndDriver(net) : new VccDriver(net));
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

    // ---------------------------------------------------- arithmetic mode

    [Fact]
    public void Add_three_plus_five_is_eight_no_carry_out()
    {
        // S=1001 ADD; M=L; Cn=H (no carry in from active-high convention).
        var r = Build(a: 3, b: 5, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.Equal(8, r.ReadF());
        Assert.False(r.ReadCarryOut());
    }

    [Fact]
    public void Add_eight_plus_eight_overflows_with_carry_out()
    {
        var r = Build(a: 8, b: 8, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.Equal(0, r.ReadF());
        Assert.True(r.ReadCarryOut());
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
        // S=0110 is "A minus B minus 1" in active-high; Cn=L (carryIn=true)
        // adds 1, producing A minus B. 7 - 3 = 4. Carry-out high == no borrow.
        var r = Build(a: 7, b: 3, s: 0b0110, modeLogic: false, carryIn: true);
        Assert.Equal(4, r.ReadF());
        Assert.True(r.ReadCarryOut());
    }

    [Fact]
    public void Subtract_three_minus_seven_underflows_with_no_carry_out()
    {
        // 3 - 7 = -4 = 0xC in 4-bit two's complement.
        // No borrow signalled means carry-out low.
        var r = Build(a: 3, b: 7, s: 0b0110, modeLogic: false, carryIn: true);
        Assert.Equal(0xC, r.ReadF());
        Assert.False(r.ReadCarryOut());
    }

    [Fact]
    public void Subtract_equal_operands_yields_zero_and_AeqB_high()
    {
        // 5 - 5 = 0. A=B should release (and the rig's pull-up reads it High).
        var r = Build(a: 5, b: 5, s: 0b0110, modeLogic: false, carryIn: true);
        Assert.Equal(0, r.ReadF());
        Assert.True(r.ReadAEqualsB());
    }

    [Fact]
    public void Subtract_unequal_operands_pulls_AeqB_low()
    {
        var r = Build(a: 5, b: 3, s: 0b0110, modeLogic: false, carryIn: true);
        Assert.Equal(2, r.ReadF());
        Assert.False(r.ReadAEqualsB());
    }

    // -------------------------------------------------------- logic mode

    [Fact]
    public void Logic_AND_returns_bitwise_and()
    {
        // S=1011 in M=H column is "A . B" (logical AND).
        var r = Build(a: 0b1100, b: 0b1010, s: 0b1011, modeLogic: true, carryIn: false);
        Assert.Equal(0b1000, r.ReadF());
    }

    [Fact]
    public void Logic_OR_returns_bitwise_or()
    {
        // S=1110 in M=H column is "A + B" (logical OR).
        var r = Build(a: 0b1100, b: 0b1010, s: 0b1110, modeLogic: true, carryIn: false);
        Assert.Equal(0b1110, r.ReadF());
    }

    [Fact]
    public void Logic_XOR_returns_bitwise_xor()
    {
        // S=0110 in M=H column is "A xor B".
        var r = Build(a: 0b1100, b: 0b1010, s: 0b0110, modeLogic: true, carryIn: false);
        Assert.Equal(0b0110, r.ReadF());
    }

    [Fact]
    public void Logic_NOT_A_returns_complement()
    {
        // S=0000 in M=H column is /A.
        var r = Build(a: 0b1010, b: 0, s: 0b0000, modeLogic: true, carryIn: false);
        Assert.Equal(0b0101, r.ReadF());
    }

    [Fact]
    public void Logic_mode_inhibits_carry_out()
    {
        // Even with inputs that would generate carry in arithmetic mode,
        // M=H must hold Cn+4 high (carry chain disabled).
        var r = Build(a: 0xF, b: 0xF, s: 0b1110, modeLogic: true, carryIn: false);
        Assert.True(r.ReadCarryOut());
    }

    // ------------------------------------------------- carry lookahead

    [Fact]
    public void Add_propagate_asserts_when_sum_is_fifteen_or_more()
    {
        // 7 + 8 = 15. Per datasheet, P (X pin, active-low) asserts when the
        // sum is 15 or more.
        var r = Build(a: 7, b: 8, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.True(r.ReadPropagate());
        Assert.False(r.ReadGenerate());  // G asserts at 16, not 15
    }

    [Fact]
    public void Add_generate_asserts_when_sum_is_sixteen_or_more()
    {
        // 8 + 8 = 16. G asserts.
        var r = Build(a: 8, b: 8, s: 0b1001, modeLogic: false, carryIn: false);
        Assert.True(r.ReadPropagate());
        Assert.True(r.ReadGenerate());
    }

    // SUB P/G convention, in this rig's ACTIVE-HIGH terms: P asserts when
    // A >= B, G asserts when A > B. Derivation: SUB computes A + /B, and
    // with /B = 15 - B, generate (carry-out regardless of Cn) means
    // A + 15 - B >= 16, i.e. A > B; propagate means >= 15, i.e. A >= B.
    // Equivalently: SUB carry-out asserted = no borrow = A >= B, and the
    // cascade identity carry-out = G + P.carry-in only holds this way
    // round (the Hc182Tests 16-bit SUB sweep proves it exhaustively).
    //
    // The "A <= B / A < B" phrasing seen in the '181 datasheet narrative
    // and the Thumby carry notes is the ACTIVE-LOW-DATA description of the
    // same pin behaviour; asserting it against this rig's active-high
    // values inverts the polarity -- the exact trap that produced an
    // earlier, wrong version of these two tests (caught because A=3,B=5
    // is the one case in this file where the two conventions disagree;
    // the A=B case below coincidentally passes under either).

    [Fact]
    public void Sub_propagate_asserts_at_equality_but_generate_does_not()
    {
        // A=5, B=5: A >= B so P asserts; A == B so G (asserts at A > B)
        // does not. Note A=B is the one operand relation where the correct
        // (>=, >) and inverted (<=, <) conventions agree on P -- this test
        // alone cannot detect an inverted model; the two below can.
        var r = Build(a: 5, b: 5, s: 0b0110, modeLogic: false, carryIn: false);
        Assert.True(r.ReadPropagate());
        Assert.False(r.ReadGenerate());
    }

    [Fact]
    public void Sub_generate_asserts_when_A_gt_B()
    {
        // A=5, B=3: A > B so both P and G assert (A + /B = 17 >= 16 --
        // carry out with or without carry in).
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
}