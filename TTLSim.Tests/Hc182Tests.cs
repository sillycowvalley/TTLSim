using System.Collections.Generic;
using TTLSim.Chips.Alu;
using TTLSim.Chips.Sources;
using TTLSim.Core;

/// <summary>
/// Two layers of proof for the '182:
///
/// 1. Exhaustive truth table -- all 512 input combinations of the nine
///    inputs against the reference lookahead equations (the same asserted
///    forms documented in GAL_182.pld).
///
/// 2. The 16-bit cascade -- four Hc181 slices carried by one Hc182, swept
///    over ADD (S=1001) and SUB (S=0110) with both carry-in states, checked
///    against straight arithmetic. Arithmetic IS the ripple-cascade ground
///    truth (each slice computes a 4-bit add with a chained carry), so this
///    is the sim-side half of the ripple-vs-GAL diff testbench from the
///    Thumby carry-architecture notes. The SUB sweep is the one that
///    catches inverted P/G polarity on the '181 -- it fails against an
///    Hc181 whose SUB row returns (a &lt;= b, a &lt; b) and passes with
///    (a &gt;= b, a &gt; b).
///
/// The cascade rig follows the '181's ACTIVE-HIGH data convention (CR
/// 2026-07-24): A/B/F pins carry true data, and BOTH carry pins are active
/// LOW -- the '181's Cn+4 now asserts LOW, matching the '182's Cn+x/y/z,
/// so the whole carry web is one polarity with no inverters anywhere.
/// </summary>
public class Hc182Tests
{
    // ================================================== exhaustive truth

    [Fact]
    public void Exhaustive_truth_table_matches_lookahead_equations()
    {
        for (int v = 0; v < 512; v++)
        {
            bool g0 = (v & 1) != 0;
            bool g1 = (v & 2) != 0;
            bool g2 = (v & 4) != 0;
            bool g3 = (v & 8) != 0;
            bool p0 = (v & 16) != 0;
            bool p1 = (v & 32) != 0;
            bool p2 = (v & 64) != 0;
            bool p3 = (v & 128) != 0;
            bool c = (v & 256) != 0;

            (bool cnX, bool cnY, bool cnZ, bool gGrp, bool pGrp) =
                Evaluate(g0, g1, g2, g3, p0, p1, p2, p3, c);

            bool expCnX = g0 || (p0 && c);
            bool expCnY = g1 || (p1 && g0) || (p1 && p0 && c);
            bool expCnZ = g2 || (p2 && g1) || (p2 && p1 && g0) || (p2 && p1 && p0 && c);
            bool expGGrp = g3 || (p3 && g2) || (p3 && p2 && g1) || (p3 && p2 && p1 && g0);
            bool expPGrp = p3 && p2 && p1 && p0;

            Assert.True(
                cnX == expCnX && cnY == expCnY && cnZ == expCnZ
                    && gGrp == expGGrp && pGrp == expPGrp,
                $"v={v}: got Cn+x={cnX} Cn+y={cnY} Cn+z={cnZ} /G={gGrp} /P={pGrp}, " +
                $"expected {expCnX} {expCnY} {expCnZ} {expGGrp} {expPGrp}");
        }
    }

    // Build one Hc182, drive the nine inputs (asserted = pin LOW), run to
    // quiescence, read the five outputs (asserted = pin LOW).
    private static (bool cnX, bool cnY, bool cnZ, bool gGrp, bool pGrp) Evaluate(
        bool g0, bool g1, bool g2, bool g3,
        bool p0, bool p1, bool p2, bool p3, bool c)
    {
        // A net on every signal pin (power pins 8/16 excluded).
        var nets = new Dictionary<int, Net>();
        for (int p = 1; p <= 15; p++)
            if (p != 8) nets[p] = new Net(p);

        Hc182 chip = new(
            g1: nets[1], p1: nets[2], g0: nets[3], p0: nets[4],
            g3: nets[5], p3: nets[6],
            pGrp: nets[7], cnZ: nets[9], gGrp: nets[10],
            cnY: nets[11], cnX: nets[12],
            cn: nets[13], g2: nets[14], p2: nets[15]);

        var chips = new List<IChip>
        {
            DriveAsserted(nets[3], g0),   // /G0
            DriveAsserted(nets[1], g1),   // /G1
            DriveAsserted(nets[14], g2),  // /G2
            DriveAsserted(nets[5], g3),   // /G3
            DriveAsserted(nets[4], p0),   // /P0
            DriveAsserted(nets[2], p1),   // /P1
            DriveAsserted(nets[15], p2),  // /P2
            DriveAsserted(nets[6], p3),   // /P3
            DriveAsserted(nets[13], c),   // Cn
            chip,
        };

        RunToQuiescence(chips);

        return (
            nets[12].Value == Signal.Low,   // Cn+x
            nets[11].Value == Signal.Low,   // Cn+y
            nets[9].Value == Signal.Low,    // Cn+z
            nets[10].Value == Signal.Low,   // /G
            nets[7].Value == Signal.Low);   // /P
    }

    // ================================================ 16-bit '181 cascade

    // Values chosen to exercise per-slice boundaries: all-zero, all-one,
    // single-bit, sign bit, nibble patterns, and equal / near-equal pairs
    // (equality per slice is where SUB's P-vs-G distinction bites).
    private static readonly int[] Operands =
    {
        0x0000, 0x0001, 0x00FF, 0x0F0F, 0x1234,
        0x5A5A, 0x7FFF, 0x8000, 0xAAAA, 0xFFFF
    };

    [Fact]
    public void Sixteen_bit_add_matches_arithmetic()
    {
        foreach (int a in Operands)
            foreach (int b in Operands)
                foreach (bool carry in new[] { false, true })
                {
                    int cin = carry ? 1 : 0;
                    int total = a + b + cin;
                    AssertCascade(0b1001, a, b, carry,
                        expectedF: total & 0xFFFF,
                        expectedCarry: (total >> 16) & 1,
                        op: "ADD");
                }
    }

    [Fact]
    public void Sixteen_bit_subtract_matches_arithmetic()
    {
        foreach (int a in Operands)
            foreach (int b in Operands)
                foreach (bool carry in new[] { false, true })
                {
                    // SUB row: F = A + ~B + carry -- A minus B minus 1, plus
                    // the injected carry. Identical to the ripple cascade.
                    int cin = carry ? 1 : 0;
                    int total = a + (~b & 0xFFFF) + cin;
                    AssertCascade(0b0110, a, b, carry,
                        expectedF: total & 0xFFFF,
                        expectedCarry: (total >> 16) & 1,
                        op: "SUB");
                }
    }

    [Fact]
    public void Sixteen_bit_double_matches_arithmetic()
    {
        // S=1100 (A plus A) -- the code the derived-exports CR was raised
        // on. Each slice doubles its own nibble; the '182 must chain the
        // inter-slice carries so the cascade equals 2*A + Cn. B is ignored
        // by this row; drive it with a busy pattern to prove that. The
        // 0x8000 / 0xAAAA operands are the discriminators: their high
        // nibbles wrap, which only reaches the next slice via the exports.
        foreach (int a in Operands)
            foreach (bool carry in new[] { false, true })
            {
                int cin = carry ? 1 : 0;
                int total = a + a + cin;
                AssertCascade(0b1100, a, 0x5A5A, carry,
                    expectedF: total & 0xFFFF,
                    expectedCarry: (total >> 16) & 1,
                    op: "2xA");
            }
    }

    private static void AssertCascade(
        int s, int a, int b, bool carryAsserted,
        int expectedF, int expectedCarry, string op)
    {
        (int f, int carryOut) = RunCascade(s, a, b, carryAsserted);
        Assert.True(f == expectedF && carryOut == expectedCarry,
            $"{op} A={a:X4} B={b:X4} Cn={(carryAsserted ? "L(carry)" : "H")}: " +
            $"got F={f:X4} Cout={carryOut}, expected F={expectedF:X4} Cout={expectedCarry}");
    }

    /// <summary>
    /// Four Hc181 slices, one Hc182. The external carry drives slice 0's Cn
    /// and the '182's Cn in parallel; Cn+x/y/z drive slices 1..3's Cn pins
    /// directly, no inverters -- the wiring from the Thumby carry notes.
    /// </summary>
    private static (int f, int carryOut) RunCascade(
        int s, int a, int b, bool carryAsserted)
    {
        int nextId = 1;
        Net N() => new(nextId++);

        var chips = new List<IChip>();

        // Shared select and mode nets. M = L (arithmetic).
        Net[] sNets = { N(), N(), N(), N() };   // S0..S3
        for (int i = 0; i < 4; i++)
            chips.Add(DriveHigh(sNets[i], ((s >> i) & 1) != 0));
        Net mNet = N();
        chips.Add(new GndDriver(mNet));

        // Carry-in: asserted = pin LOW. Slice 0 and the '182 share it.
        Net cn0 = N();
        chips.Add(DriveAsserted(cn0, carryAsserted));

        // Carries into slices 1..3, driven only by the '182.
        Net cn1 = N(), cn2 = N(), cn3 = N();
        Net[] cnIn = { cn0, cn1, cn2, cn3 };

        Net[] xNets = new Net[4];      // /P per slice ('181 X pin)
        Net[] yNets = new Net[4];      // /G per slice ('181 Y pin)
        Net[][] fNets = new Net[4][];
        Net[] cn4Nets = new Net[4];

        for (int k = 0; k < 4; k++)
        {
            int aNib = (a >> (4 * k)) & 0xF;
            int bNib = (b >> (4 * k)) & 0xF;

            Net[] aN = { N(), N(), N(), N() };
            Net[] bN = { N(), N(), N(), N() };
            for (int i = 0; i < 4; i++)
            {
                // Operand pins carry TRUE data: bit = 1 -> pin HIGH.
                chips.Add(DriveHigh(aN[i], ((aNib >> i) & 1) != 0));
                chips.Add(DriveHigh(bN[i], ((bNib >> i) & 1) != 0));
            }

            fNets[k] = new[] { N(), N(), N(), N() };
            Net aeqb = N();                     // open-collector, left open
            yNets[k] = N();
            xNets[k] = N();
            cn4Nets[k] = N();

            chips.Add(new Hc181(
                bN[0], aN[0],
                sNets[3], sNets[2], sNets[1], sNets[0],
                cnIn[k], mNet,
                fNets[k][0], fNets[k][1], fNets[k][2], fNets[k][3],
                aeqb,
                yNets[k], xNets[k],
                cn4Nets[k],
                bN[3], aN[3], bN[2], aN[2], bN[1], aN[1]));
        }

        // Group outputs -- wired but unread, as on a single-level cascade.
        Net pGrp = N(), gGrp = N();

        chips.Add(new Hc182(
            g1: yNets[1], p1: xNets[1], g0: yNets[0], p0: xNets[0],
            g3: yNets[3], p3: xNets[3],
            pGrp: pGrp, cnZ: cn3, gGrp: gGrp, cnY: cn2, cnX: cn1,
            cn: cn0, g2: yNets[2], p2: xNets[2]));

        RunToQuiescence(chips);

        int f = 0;
        for (int k = 0; k < 4; k++)
            for (int i = 0; i < 4; i++)
                if (fNets[k][i].Value == Signal.High)   // F pins carry true data
                    f |= 1 << (4 * k + i);

        // Slice 3's Cn+4 is the 16-bit carry out, active LOW.
        int carryOut = cn4Nets[3].Value == Signal.Low ? 1 : 0;
        return (f, carryOut);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Drive an active-low pin: asserted -> LOW.</summary>
    private static IChip DriveAsserted(Net net, bool asserted) =>
        asserted ? new GndDriver(net) : new VccDriver(net);

    /// <summary>Drive an active-high pin: true -> HIGH.</summary>
    private static IChip DriveHigh(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);

    private static void RunToQuiescence(List<IChip> chips)
    {
        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        sim.RunUntilQuiescent();
    }
}
