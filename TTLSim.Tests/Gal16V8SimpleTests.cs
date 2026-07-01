using System.Collections.Generic;
using TTLSim.Chips.Pld;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Live-simulation test for the gal16v8_simple.ttlproj circuit: a GAL16V8 (U1)
/// programmed with the BlinkJED "simple mode" fuse map, powered by VCC/GND.
///
/// This is the simulator-side counterpart to the compiler-side validation (where
/// BlinkJED's .jed was diffed against WinCUPL's). Here we take the SAME program
/// that is burned into U1, load it into the real <see cref="Gal"/> evaluator with
/// the real <see cref="GalDevice.Gal16V8"/> geometry, and confirm the discrete-
/// event simulator computes the intended logic across the whole input space:
///
///   pin 19 yand = a &amp; b &amp; c &amp; d      (a=2 b=3 c=4 d=5)
///   pin 18 yor  = a # b # c # d
///   pin 17 yxor = a $ b
///   pin 14 yfg  = f &amp; g                (f=pin 1, g=pin 11 -- plain inputs in
///                                        simple mode, not CLK/OE)
///
/// The truth table is computed independently of the fuses, so a pass means the
/// evaluator agrees with the design intent, not merely with itself.
/// </summary>
public class Gal16V8SimpleTests
{
    // The exact program stored in U1 (gal16v8_simple.ttlproj), i.e. BlinkJED's
    // output for gal16v8_simple.pld. Parsing verifies the *C checksum, so this
    // string is self-checking: if a fuse were mistyped it would fail to parse.
    private const string Program =
@"gal16v8_simple (BlinkJED)
*QP20
*QF2194
*G0
*F0
*L00000 01110111011101111111111111111111
*L00256 01111111111111111111111111111111
*L00288 11110111111111111111111111111111
*L00320 11111111011111111111111111111111
*L00352 11111111111101111111111111111111
*L00512 01111011111111111111111111111111
*L00544 10110111111111111111111111111111
*L01280 11011111111111111111111111111101
*L02048 11100100111111111111111111111111
*L02080 11111111111111111111111111111111
*L02112 11111111000110111111111111111111
*L02144 11111111111111111111111111111111
*L02176 111111111111111110
*C3015*";

    // All 64 input combinations over the six pins the design uses.
    public static IEnumerable<object[]> AllInputs()
    {
        for (int v = 0; v < 64; v++)
            yield return new object[]
            {
                (v & 1)  != 0,   // a  (pin 2)
                (v & 2)  != 0,   // b  (pin 3)
                (v & 4)  != 0,   // c  (pin 4)
                (v & 8)  != 0,   // d  (pin 5)
                (v & 16) != 0,   // f  (pin 1)
                (v & 32) != 0,   // g  (pin 11)
            };
    }

    [Theory]
    [MemberData(nameof(AllInputs))]
    public void Simple_gal_matches_truth_table(bool a, bool b, bool c, bool d, bool f, bool g)
    {
        (Signal yand, Signal yor, Signal yxor, Signal yfg) = Evaluate(a, b, c, d, f, g);

        Assert.Equal(ToSignal(a && b && c && d), yand);
        Assert.Equal(ToSignal(a || b || c || d), yor);
        Assert.Equal(ToSignal(a ^ b), yxor);
        Assert.Equal(ToSignal(f && g), yfg);
    }

    // Build the programmed GAL16V8 exactly as it sits in the circuit, drive the
    // six inputs, run to quiescence, and read the four outputs.
    private static (Signal yand, Signal yor, Signal yxor, Signal yfg)
        Evaluate(bool a, bool b, bool c, bool d, bool f, bool g)
    {
        // A net on every signal pin (power pins 10/20 excluded), as on the chip.
        var nets = new Dictionary<int, Net>();
        for (int p = 1; p <= 19; p++)
            if (p != 10) nets[p] = new Net(p);

        JedecData jed = JedecFuseMap.Parse(Program);
        Gal gal = new(GalDevice.Gal16V8, jed.Fuses, nets);

        // Drive the six used inputs. Pins 6-9 are left floating on purpose: the
        // design references none of them, so the outputs must stay determinate
        // regardless -- which this test also confirms.
        var chips = new List<IChip>
        {
            Drive(nets[2],  a),
            Drive(nets[3],  b),
            Drive(nets[4],  c),
            Drive(nets[5],  d),
            Drive(nets[1],  f),
            Drive(nets[11], g),
            gal,
        };

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        sim.RunUntilQuiescent();

        return (nets[19].Value, nets[18].Value, nets[17].Value, nets[14].Value);
    }

    private static IChip Drive(Net net, bool high) =>
        high ? new VccDriver(net) : new GndDriver(net);

    private static Signal ToSignal(bool high) => high ? Signal.High : Signal.Low;
}