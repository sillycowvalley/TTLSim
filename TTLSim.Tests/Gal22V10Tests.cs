using System.Collections.Generic;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Pld;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Live-simulation tests for the GAL22V10 evaluator, the '22V10 counterpart
/// to <see cref="GalModeTests"/>. The programs are BlinkyJED's exact .jed
/// output for the two 22V10 sample designs -- the same fuse maps that were
/// proven fuse-by-fuse against WinCUPL references AND burned into an
/// ATF22V10C and exercised on real hardware; parsing verifies each *C
/// checksum, so the fixtures are self-checking.
///
/// Combinational (gal22v10_combinational.pld), spanning the geometry:
///   y23 = a &amp; b                      8-term block beside the AR row
///   y21 = a # d, y21.oe = e &amp; k      per-macrocell OE (12-term block)
///   y19 = OR of all twelve inputs      16-term block
///   y18 active low                     S0 polarity, combinational
///   y16 = a $ b $ c                    XOR expansion (4 terms)
///   y15 = y16 &amp; j                    combinational PIN feedback
///   y14 = k &amp; m                      8-term block beside SP; pins 13 and 1
///   pins 22/20/17 erased               all-intact OE row -&gt; never drives
///
/// Registered (gal22v10_registered.pld): a 3-bit counter q2..q0 with an
/// active-low registered qb0, AR/SP terms, and a combinational carry decode.
/// These tests cover the static contract: the 22V10's DEFINED power-up reset
/// (registers low, so unlike the V8 an active-HIGH registered output starts
/// LOW and an active-low one HIGH -- the S0 mux sits after the register),
/// and erased cells releasing their pins. Clock-edge sequencing (counting,
/// AR level-sensitivity, SP at the edge, AR dominating SP) follows the same
/// evaluator paths and is covered by the livetest projects, as it was for
/// the V8 parts.
///
/// The truth tables are computed independently of the fuses, so a pass means
/// the evaluator agrees with the design intent, not merely with itself.
/// </summary>
public class Gal22V10Tests
{
    private const string CombinationalProgram =
@"gal22v10_combinational (BlinkyJED)
*QP24
*QF5892
*G0
*F0
*L00032 00000000000011111111111111111111
*L00064 11111111111111111111111111110111
*L00096 01111111111111111111111111111111
*L00128 11110000000000000000000000000000
*L00896 00000000000000000000000000001111
*L00928 11111111111111110111111111111111
*L00960 11111101111101111111111111111111
*L00992 11111111111111111111111111111111
*L01024 11110111111111111111111111111111
*L02144 00000000000011111111111111111111
*L02176 11111111111111111111111111110111
*L02208 11111111111111111111111111111111
*L02240 11111111111101111111111111111111
*L02272 11111111111111111111111111110111
*L02304 11111111111111111111111111111111
*L02336 11111111111101111111111111111111
*L02368 11111111111111111111111111110111
*L02400 11111111111111111111111111111111
*L02432 11111111111101111111111111111111
*L02464 11111111111111111111111111110111
*L02496 11111111111111111111111111111111
*L02528 11111111111101111111111111111111
*L02560 11111111111111111111111111110111
*L02592 11111111111111111111111111111111
*L02624 11111111111101111111111111111111
*L02656 11111111111111111111111111010111
*L02688 11111111111111111111111111111111
*L02720 11111111000000000000000000000000
*L02880 00000000000000000000000011111111
*L02912 11111111111111111111111111111111
*L02944 11111111011101111111111111111111
*L02976 11111111111111111111111111110111
*L03008 01111111111111111111111111110000
*L04288 00000000000000000000000011111111
*L04320 11111111111111111111111111111111
*L04352 11111111011110111011111111111111
*L04384 11111111111111111111101101111011
*L04416 11111111111111111111111111111111
*L04448 10111011011111111111111111111111
*L04480 11111111111101110111011111111111
*L04512 11111111111111111111000000000000
*L04864 00000000000000000000111111111111
*L04896 11111111111111111111111111111111
*L04928 11111111111111111111111111111101
*L04960 11111111011100000000000000000000
*L05344 00000000000000000000000011111111
*L05376 11111111111111111111111111111111
*L05408 11110111111111111111111111111111
*L05440 11111111111111010000000000000000
*L05792 00000000000000001100110011010011
*L05824 11111111111111111111111111111111
*L05856 11111111111111111111111111111111
*L05888 1111
*CAADF*";

    private const string RegisteredProgram =
@"gal22v10_registered (BlinkyJED)
*QP24
*QF5892
*G0
*F0
*L00000 11110111111111111111111111111111
*L00032 11111111111111111111111111111111
*L00064 11111111111111111111111111101111
*L00096 11111011111111111111111111111111
*L00128 11111101111111110111111111111111
*L00160 11111111111111110000000000000000
*L00416 00000000000000000000000011111111
*L00448 11111111111111111111111111111111
*L00480 11111101111011111111111111111111
*L00512 11111111111111111111111011111011
*L00544 11111111111111111111111111111110
*L00576 11011111011111111111111111111111
*L00608 11111111000000000000000000000000
*L00896 00000000000000000000000000001111
*L00928 11111111111111111111111111111111
*L00960 11111111111111011110111111111111
*L00992 11111111111111111111110111111110
*L01024 11111111111111111111111111111111
*L01056 11111111111010111111111111111111
*L01088 11111111111111101110110101111111
*L01120 11111111111111111111111100000000
*L01472 00000000000000000000000011111111
*L01504 11111111111111111111111111111111
*L01536 11111110111111111111111111111111
*L01568 11111111111111110000000000000000
*L05344 00000000000000000000000011111111
*L05376 11111111111111111111111111111111
*L05408 11111110111011101111111111111111
*L05440 11111111111111110000000000000000
*L05760 00001111111101111111111111111111
*L05792 11111111111111111010100000000000
*L05824 00111111111111111111111111111111
*L05856 11111111111111111111111111111111
*L05888 1111
*C6620*";

    // ---- Combinational: logic, polarity, feedback, erased cells -------------

    // a,b,c,d,j sweep with e = k = m = 1 (so y21's OE is satisfied and y14 is
    // exercised through pins 13 and 1) and f,g,h,i held low.
    public static IEnumerable<object[]> LogicInputs()
    {
        for (int v = 0; v < 32; v++)
            yield return new object[]
            {
                (v & 1)  != 0,   // a  (pin 2)
                (v & 2)  != 0,   // b  (pin 3)
                (v & 4)  != 0,   // c  (pin 4)
                (v & 8)  != 0,   // d  (pin 5)
                (v & 16) != 0,   // j  (pin 11)
            };
    }

    [Theory]
    [MemberData(nameof(LogicInputs))]
    public void Combinational_matches_truth_table(bool a, bool b, bool c, bool d, bool j)
    {
        Dictionary<int, Net> nets = Nets22();
        Gal22V10 gal = Load(CombinationalProgram, nets);

        Run(new List<IChip>
        {
            Drive(nets[2], a), Drive(nets[3], b), Drive(nets[4], c), Drive(nets[5], d),
            Drive(nets[11], j),
            new VccDriver(nets[6]),                 // e = 1
            new VccDriver(nets[13]),                // k = 1 (plain input; no /OE pin)
            new VccDriver(nets[1]),                 // m = 1 (pin 1 as an array input)
            new GndDriver(nets[7]), new GndDriver(nets[8]),
            new GndDriver(nets[9]), new GndDriver(nets[10]),
            new PullDriver(nets[22], Signal.High),  // erased cells: the pulls win
            new PullDriver(nets[20], Signal.High),
            new PullDriver(nets[17], Signal.High),
            gal,
        });

        bool y16 = a ^ b ^ c;
        Assert.Equal(ToSignal(a && b), nets[23].Value);                    // y23
        Assert.Equal(ToSignal(a || d), nets[21].Value);                    // y21, OE = e&k = 1
        Assert.Equal(Signal.High, nets[19].Value);                         // y19: e=k=m=1
        Assert.Equal(ToSignal(!((a && b) || (c && d))), nets[18].Value);   // y18 active low
        Assert.Equal(ToSignal(y16), nets[16].Value);                       // y16 = a$b$c
        Assert.Equal(ToSignal(y16 && j), nets[15].Value);                  // y15 via pin feedback
        Assert.Equal(Signal.High, nets[14].Value);                         // y14 = k&m = 1

        Assert.Equal(Signal.High, nets[22].Value);   // erased -> released -> pull
        Assert.Equal(Signal.High, nets[20].Value);
        Assert.Equal(Signal.High, nets[17].Value);
    }

    // e,k sweep with every other input low: y21's per-macrocell OE term
    // tri-states the pin (observed through a pull-up) and y19's 16-term
    // block reads both pin 6 and pin 13.
    public static IEnumerable<object[]> OeInputs()
    {
        for (int v = 0; v < 4; v++)
            yield return new object[] { (v & 1) != 0, (v & 2) != 0 };
    }

    [Theory]
    [MemberData(nameof(OeInputs))]
    public void Combinational_oe_term_tristates_the_pin(bool e, bool k)
    {
        Dictionary<int, Net> nets = Nets22();
        Gal22V10 gal = Load(CombinationalProgram, nets);

        var chips = new List<IChip>
        {
            Drive(nets[6], e), Drive(nets[13], k),
            new PullDriver(nets[21], Signal.High),   // observe y21's tri-state
            gal,
        };
        foreach (int p in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11 })
            chips.Add(new GndDriver(nets[p]));
        Run(chips);

        // OE = e & k. Driven -> y21 = a # d = 0 (low); released -> pull high.
        Assert.Equal(e && k ? Signal.Low : Signal.High, nets[21].Value);
        Assert.Equal(ToSignal(e || k), nets[19].Value);   // all other y19 inputs low
    }

    // ---- Registered: the 22V10's defined power-up state ----------------------

    [Fact]
    public void Registered_powers_up_reset_with_polarity_applied()
    {
        // clk=1 rst=2 pre=3 en=4 ; q0=23 q1=22 q2=21 (active high), qb0=20
        // (active LOW), carry=14. Registers power up RESET (Q = 0): the
        // active-high q pins start LOW and qb0 starts HIGH -- the S0 polarity
        // mux sits after the register, the opposite convention to the V8's
        // inverting pin buffer (where every registered output starts high).
        Dictionary<int, Net> nets = Nets22();
        Gal22V10 gal = Load(RegisteredProgram, nets);

        Run(new List<IChip>
        {
            new GndDriver(nets[1]),    // clk low, never rises
            new GndDriver(nets[2]),    // rst negated (AR term false)
            new GndDriver(nets[3]),    // pre negated (SP term false)
            new VccDriver(nets[4]),    // en high -- irrelevant without an edge
            new PullDriver(nets[19], Signal.High),   // an erased cell releases
            gal,
        });

        Assert.Equal(Signal.Low, nets[23].Value);    // q0 (active high, Q = 0)
        Assert.Equal(Signal.Low, nets[22].Value);    // q1
        Assert.Equal(Signal.Low, nets[21].Value);    // q2
        Assert.Equal(Signal.High, nets[20].Value);   // qb0 (active low, Q = 0)
        Assert.Equal(Signal.Low, nets[14].Value);    // carry = q0 & q1 & q2 = 0
        Assert.Equal(Signal.High, nets[19].Value);   // erased -> the pull wins
    }

    // ---- helpers -------------------------------------------------------------

    // A net on every 22V10 signal pin (power pins 12/24 excluded).
    private static Dictionary<int, Net> Nets22()
    {
        var nets = new Dictionary<int, Net>();
        for (int p = 1; p <= 23; p++)
            if (p != 12) nets[p] = new Net(p);
        return nets;
    }

    private static Gal22V10 Load(string program, Dictionary<int, Net> nets)
    {
        JedecData jed = JedecFuseMap.Parse(program);   // verifies the *C checksum
        return new Gal22V10(jed.Fuses, nets);
    }

    private static void Run(List<IChip> chips)
    {
        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        sim.RunUntilQuiescent();
    }

    private static IChip Drive(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);

    private static Signal ToSignal(bool high) => high ? Signal.High : Signal.Low;
}