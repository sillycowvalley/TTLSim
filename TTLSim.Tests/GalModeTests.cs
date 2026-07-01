using System.Collections.Generic;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Pld;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Live-simulation tests for the GAL COMPLEX and REGISTERED configurations,
/// the counterpart to <see cref="Gal16V8SimpleTests"/> (simple mode). The
/// programs are BlinkyJED's exact .jed output for the four mode-sample .pld
/// designs; parsing verifies each *C checksum, so the fixture strings are
/// self-checking.
///
/// Complex mode (gal16v8_complex.pld / gal20v8_complex.pld):
///   y1 = a &amp; b # c                       always driven
///   y2 = a $ b, y2.oe = ena               tri-stated whenever ena = 0
///   fb = a &amp; c                            drives its pin AND feeds back
///   g  = fb # sel                         consumes the combinational feedback
///
/// Registered mode (gal16v8_registered.pld / gal20v8_registered.pld):
///   3-bit synchronous up-counter q2 q1 q0 with sync reset, plus a
///   combinational carry = q0 &amp; q1 &amp; q2 decode. These tests cover the static
///   contract: power-up reset (datasheet: registers reset low, so every
///   registered OUTPUT starts high), the common /OE gating the registered
///   pins only, and combinational cells reading register FEEDBACK rather
///   than the (possibly released) pins. Clock-edge sequencing is covered
///   separately.
///
/// The truth tables are computed independently of the fuses, so a pass means
/// the evaluator agrees with the design intent, not merely with itself.
/// </summary>
public class GalModeTests
{
    // ---- BlinkyJED outputs for the four mode-sample designs ----------------

    private const string Complex16Program =
@"gal16v8_complex (BlinkyJED)
*QP20
*QF2194
*G0
*F0
*L00000 11111111111111111111111111111111
*L00032 01110111111111111111111111111111
*L00064 11111111011111111111111111111111
*L00256 11111111111111111111111111111101
*L00288 01111011111111111111111111111111
*L00320 10110111111111111111111111111111
*L00512 11111111111111111111111111111111
*L00544 01111111011111111111111111111111
*L00768 11111111111111111111111111111111
*L00800 11111111110111111111111111111111
*L00832 11111111111101111111111111111111
*L02048 11110000111111111111111111111111
*L02080 11111111111111111111111111111111
*L02112 11111111111111111111111111111111
*L02144 11111111111111111111111111111111
*L02176 111111111111111111
*C3C3A*";

    private const string Registered16Program =
@"gal16v8_registered (BlinkyJED)
*QP20
*QF2194
*G0
*F0
*L00000 10101111111111111111111111111111
*L00256 10101101111111111111111111111111
*L00288 10011110111111111111111111111111
*L00512 10111110110111111111111111111111
*L00544 10101111110111111111111111111111
*L00576 10011101111011111111111111111111
*L01280 11111111111111111111111111111111
*L01312 11011101110111111111111111111111
*L02048 11100100111111111111111111111111
*L02080 11111111111111111111111111111111
*L02112 11111111000111111111111111111111
*L02144 11111111111111111111111111111111
*L02176 111111111111111101
*C2EED*";

    private const string Complex20Program =
@"gal20v8_complex (BlinkyJED)
*QP24
*QF2706
*G0
*F0
*L00000 11111111111111111111111111111111
*L00032 11111111011101111111111111111111
*L00064 11111111111111111111111101111111
*L00096 11111111111111111111111100000000
*L00320 11111111111111111111111111111111
*L00352 11111101011110111111111111111111
*L00384 11111111111111111011011111111111
*L00416 11111111111111111111111100000000
*L01600 11111111111111111111111111111111
*L01632 11111111011111110111111111111111
*L01664 11111111111111110000000000000000
*L01920 11111111111111111111111111111111
*L01952 11111111111111111111111111111111
*L01984 11011111111111111111111111110111
*L02016 11111111111111111111111100000000
*L02560 11000110111111111111111111111111
*L02592 11111111111111111111111111111111
*L02624 11111111111111111111111111111111
*L02656 11111111111111111111111111111111
*L02688 111111111111111111
*C4783*";

    private const string Registered20Program =
@"gal20v8_registered (BlinkyJED)
*QP24
*QF2706
*G0
*F0
*L00000 10111110111111111111111111111111
*L00032 11111111000000000000000000000000
*L00320 10111110110111111111111111111111
*L00352 11111111101111011110111111111111
*L00384 11111111111111110000000000000000
*L00640 10111111111011011111111111111111
*L00672 11111111101111101111110111111111
*L00704 11111111111111111011110111011110
*L00736 11111111111111111111111100000000
*L01600 11111111111111111111111111111111
*L01632 11111111111111011101110111111111
*L01664 11111111111111110000000000000000
*L02560 11100100111111111111111111111111
*L02592 11111111111111111111111111111111
*L02624 11111111000111111111111111111111
*L02656 11111111111111111111111111111111
*L02688 111111111111111101
*C3541*";

    // ---- Complex mode: per-macrocell OE + combinational feedback ------------

    // All 32 combinations of the five inputs the complex designs use.
    public static IEnumerable<object[]> ComplexInputs()
    {
        for (int v = 0; v < 32; v++)
            yield return new object[]
            {
                (v & 1)  != 0,   // a
                (v & 2)  != 0,   // b
                (v & 4)  != 0,   // c
                (v & 8)  != 0,   // sel
                (v & 16) != 0,   // ena
            };
    }

    [Theory]
    [MemberData(nameof(ComplexInputs))]
    public void Complex16_matches_truth_table(bool a, bool b, bool c, bool sel, bool ena)
    {
        // a=2 b=3 c=4 sel=5 ena=11 ; y1=19 y2=18 fb=17 g=16
        Dictionary<int, Net> nets = Nets16();
        Gal gal = Load(Complex16Program, GalDevice.Gal16V8, nets);

        Run(new List<IChip>
        {
            Drive(nets[2], a), Drive(nets[3], b), Drive(nets[4], c),
            Drive(nets[5], sel), Drive(nets[11], ena),
            new PullDriver(nets[18], Signal.Low),   // observe y2's tri-state
            gal,
        });

        bool fb = a && c;
        Assert.Equal(ToSignal((a && b) || c), nets[19].Value);             // y1
        Assert.Equal(ena ? ToSignal(a ^ b) : Signal.Low, nets[18].Value);  // y2: released -> pulled low
        Assert.Equal(ToSignal(fb), nets[17].Value);                        // fb
        Assert.Equal(ToSignal(fb || sel), nets[16].Value);                 // g, via feedback of fb
    }

    [Theory]
    [MemberData(nameof(ComplexInputs))]
    public void Complex20_matches_truth_table(bool a, bool b, bool c, bool sel, bool ena)
    {
        // a=2 b=3 c=4 sel=5 ena=13 ; y1=22 y2=21 fb=17 g=16
        Dictionary<int, Net> nets = Nets20();
        Gal gal = Load(Complex20Program, GalDevice.Gal20V8, nets);

        Run(new List<IChip>
        {
            Drive(nets[2], a), Drive(nets[3], b), Drive(nets[4], c),
            Drive(nets[5], sel), Drive(nets[13], ena),
            new PullDriver(nets[21], Signal.Low),   // observe y2's tri-state
            gal,
        });

        bool fb = a && c;
        Assert.Equal(ToSignal((a && b) || c), nets[22].Value);             // y1
        Assert.Equal(ena ? ToSignal(a ^ b) : Signal.Low, nets[21].Value);  // y2: released -> pulled low
        Assert.Equal(ToSignal(fb), nets[17].Value);                        // fb
        Assert.Equal(ToSignal(fb || sel), nets[16].Value);                 // g, via feedback of fb
    }

    // ---- Registered mode: power-up reset and /OE gating ---------------------

    [Fact]
    public void Registered16_powers_up_high_with_oe_low()
    {
        // clk=1 oe=11 rst=2 ; q0=19 q1=18 q2=17 carry=14. Datasheet power-up
        // reset: registers reset low, registered outputs (and their feedback)
        // start HIGH. The clock is held low, so no edge ever fires.
        Dictionary<int, Net> nets = Nets16();
        Gal gal = Load(Registered16Program, GalDevice.Gal16V8, nets);

        Run(new List<IChip>
        {
            new GndDriver(nets[1]),    // clk low, never rises
            new GndDriver(nets[2]),    // rst negated
            new GndDriver(nets[11]),   // /OE asserted
            gal,
        });

        Assert.Equal(Signal.High, nets[19].Value);   // q0
        Assert.Equal(Signal.High, nets[18].Value);   // q1
        Assert.Equal(Signal.High, nets[17].Value);   // q2
        Assert.Equal(Signal.High, nets[14].Value);   // carry = q0 & q1 & q2 (feedback all high)
    }

    [Fact]
    public void Registered16_oe_high_releases_registered_pins_only()
    {
        Dictionary<int, Net> nets = Nets16();
        Gal gal = Load(Registered16Program, GalDevice.Gal16V8, nets);

        Run(new List<IChip>
        {
            new GndDriver(nets[1]),
            new GndDriver(nets[2]),
            new VccDriver(nets[11]),               // /OE negated -> registered pins released
            new PullDriver(nets[19], Signal.Low),
            new PullDriver(nets[18], Signal.Low),
            new PullDriver(nets[17], Signal.Low),
            gal,
        });

        // Registered pins released: the pulls win.
        Assert.Equal(Signal.Low, nets[19].Value);
        Assert.Equal(Signal.Low, nets[18].Value);
        Assert.Equal(Signal.Low, nets[17].Value);

        // The combinational cell reads register FEEDBACK, not the pins: even
        // with all three q pins pulled low, carry still decodes the internal
        // (power-up high) state.
        Assert.Equal(Signal.High, nets[14].Value);
    }

    [Fact]
    public void Registered20_powers_up_high_with_oe_low()
    {
        // clk=1 oe=13 rst=2 ; q0=22 q1=21 q2=20 carry=17
        Dictionary<int, Net> nets = Nets20();
        Gal gal = Load(Registered20Program, GalDevice.Gal20V8, nets);

        Run(new List<IChip>
        {
            new GndDriver(nets[1]),
            new GndDriver(nets[2]),
            new GndDriver(nets[13]),   // /OE asserted
            gal,
        });

        Assert.Equal(Signal.High, nets[22].Value);   // q0
        Assert.Equal(Signal.High, nets[21].Value);   // q1
        Assert.Equal(Signal.High, nets[20].Value);   // q2
        Assert.Equal(Signal.High, nets[17].Value);   // carry
    }

    [Fact]
    public void Registered20_oe_high_releases_registered_pins_only()
    {
        Dictionary<int, Net> nets = Nets20();
        Gal gal = Load(Registered20Program, GalDevice.Gal20V8, nets);

        Run(new List<IChip>
        {
            new GndDriver(nets[1]),
            new GndDriver(nets[2]),
            new VccDriver(nets[13]),               // /OE negated
            new PullDriver(nets[22], Signal.Low),
            new PullDriver(nets[21], Signal.Low),
            new PullDriver(nets[20], Signal.Low),
            gal,
        });

        Assert.Equal(Signal.Low, nets[22].Value);
        Assert.Equal(Signal.Low, nets[21].Value);
        Assert.Equal(Signal.Low, nets[20].Value);
        Assert.Equal(Signal.High, nets[17].Value);   // carry via register feedback
    }

    // ---- helpers -------------------------------------------------------------

    // A net on every 16V8 signal pin (power pins 10/20 excluded).
    private static Dictionary<int, Net> Nets16()
    {
        var nets = new Dictionary<int, Net>();
        for (int p = 1; p <= 19; p++)
            if (p != 10) nets[p] = new Net(p);
        return nets;
    }

    // A net on every 20V8 signal pin (power pins 12/24 excluded).
    private static Dictionary<int, Net> Nets20()
    {
        var nets = new Dictionary<int, Net>();
        for (int p = 1; p <= 23; p++)
            if (p != 12) nets[p] = new Net(p);
        return nets;
    }

    private static Gal Load(string program, GalDevice device, Dictionary<int, Net> nets)
    {
        JedecData jed = JedecFuseMap.Parse(program);   // verifies the *C checksum
        return new Gal(device, jed.Fuses, nets);
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