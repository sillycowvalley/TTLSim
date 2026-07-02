using System.Collections.Generic;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Pld;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Live-simulation tests for OUTPUT POLARITY (the XOR fuse), the counterpart
/// to <see cref="GalModeTests"/>. The programs are BlinkyJED's exact .jed
/// output for the two polarity-sample .pld designs, whose fuse maps are
/// WinCUPL-gold-validated (XOR bits, folded feedback literals, and all logic
/// rows); parsing verifies each *C checksum, so the fixtures are self-checking.
///
/// Simple mode (gal16v8_polarity.pld) -- four outputs share the identical
/// array term (a &amp; b) and differ ONLY in polarity notation:
///   yhi  (19): plain pin, plain equation        -&gt; active high
///   ylo  (18): !pin declaration                 -&gt; active low
///   yinv (17): !target equation                 -&gt; active low
///   ydbl (16): both notations                   -&gt; they cancel: active high
///
/// Registered mode (gal20v8_polarity.pld) -- the corners no other test covers:
///   q1b (21): ACTIVE-LOW REGISTERED cell, fed back into its own .d equation
///             and read by dec. The compiler folds its feedback literals to
///             the PIN sense, and the evaluator's register stores the
///             pin-level value -- these tests pin down that invariant.
///   dec (17): active-low combinational cell inside registered mode, reading
///             register feedback through the folded encoding.
/// These are the static contracts (power-up state, /OE gating, feedback vs
/// pins); clock-edge sequencing is covered by the livetest projects.
///
/// The truth tables are computed independently of the fuses, so a pass means
/// the evaluator agrees with the design intent, not merely with itself.
/// </summary>
public class GalPolarityTests
{
    private const string Polarity16Program =
@"gal16v8_polarity (BlinkyJED)
*QP20
*QF2194
*G0
*F0
*L00000 01110111111111111111111111111111
*L00256 01110111111111111111111111111111
*L00512 01110111111111111111111111111111
*L00768 01110111111111111111111111111111
*L02048 10010000111111111111111111111111
*L02080 11111111111111111111111111111111
*L02112 11111111000011111111111111111111
*L02144 11111111111111111111111111111111
*L02176 111111111111111110
*C2096*";

    private const string Polarity20Program =
@"gal20v8_polarity (BlinkyJED)
*QP24
*QF2706
*G0
*F0
*L00000 10111110111111111111111111111111
*L00032 11111111000000000000000000000000
*L00320 10111110111011111111111111111111
*L00352 11111111101111011101111111111111
*L00384 11111111111111110000000000000000
*L01600 11111111111111111111111111111111
*L01632 11111111111111011110111111111111
*L01664 11111111111111110000000000000000
*L02560 10000000111111111111111111111111
*L02592 11111111111111111111111111111111
*L02624 11111111001111111111111111111111
*L02656 11111111111111111111111111111111
*L02688 111111111111111101
*C283C*";

    // ---- Simple mode: the XOR fuse under all four polarity notations --------

    public static IEnumerable<object[]> PolarityInputs()
    {
        for (int v = 0; v < 4; v++)
            yield return new object[] { (v & 1) != 0, (v & 2) != 0 };
    }

    [Theory]
    [MemberData(nameof(PolarityInputs))]
    public void Polarity16_matches_truth_table(bool a, bool b)
    {
        // a=2 b=3 ; yhi=19 ylo=18 yinv=17 ydbl=16, all computing (a & b).
        Dictionary<int, Net> nets = Nets16();
        Gal gal = Load(Polarity16Program, GalDevice.Gal16V8, nets);

        Run(new List<IChip> { Drive(nets[2], a), Drive(nets[3], b), gal });

        bool and = a && b;
        Assert.Equal(ToSignal(and), nets[19].Value);    // yhi : active high
        Assert.Equal(ToSignal(!and), nets[18].Value);   // ylo : active low (!pin)
        Assert.Equal(ToSignal(!and), nets[17].Value);   // yinv: active low (!target)
        Assert.Equal(ToSignal(and), nets[16].Value);    // ydbl: both notations cancel
    }

    // ---- Registered mode: active-low registered cells and their feedback ----

    [Fact]
    public void Polarity20_powers_up_high_regardless_of_polarity()
    {
        // clk=1 oe=13 rst=2 ; q0=22 (active high), q1b=21 (active LOW), dec=17.
        // Datasheet power-up reset drives every registered OUTPUT high whatever
        // its XOR fuse says (the register resets; the pin buffer inverts).
        // dec = q0 & q1b decodes the LOGICAL values (1 and 0 here), so its
        // active-low pin also reads high -- via the folded feedback encoding.
        Dictionary<int, Net> nets = Nets20();
        Gal gal = Load(Polarity20Program, GalDevice.Gal20V8, nets);

        Run(new List<IChip>
        {
            new GndDriver(nets[1]),    // clk low, never rises
            new GndDriver(nets[2]),    // rst negated
            new GndDriver(nets[13]),   // /OE asserted
            gal,
        });

        Assert.Equal(Signal.High, nets[22].Value);   // q0
        Assert.Equal(Signal.High, nets[21].Value);   // q1b (active low, register reset)
        Assert.Equal(Signal.High, nets[17].Value);   // dec = !(1 & 0)
    }

    [Fact]
    public void Polarity20_oe_release_keeps_folded_feedback_alive()
    {
        // /OE negated releases the registered pins; pulls drag them low. dec
        // must STILL decode the internal state through the pin-sense feedback:
        // if the evaluator sampled the released pins (or fed back the raw
        // register instead of the pin-level value), this assertion flips.
        Dictionary<int, Net> nets = Nets20();
        Gal gal = Load(Polarity20Program, GalDevice.Gal20V8, nets);

        Run(new List<IChip>
        {
            new GndDriver(nets[1]),
            new GndDriver(nets[2]),
            new VccDriver(nets[13]),               // /OE negated -> q pins released
            new PullDriver(nets[22], Signal.Low),
            new PullDriver(nets[21], Signal.Low),
            gal,
        });

        Assert.Equal(Signal.Low, nets[22].Value);    // released: the pull wins
        Assert.Equal(Signal.Low, nets[21].Value);
        Assert.Equal(Signal.High, nets[17].Value);   // dec still reads feedback
    }

    // ---- helpers -------------------------------------------------------------

    private static Dictionary<int, Net> Nets16()
    {
        var nets = new Dictionary<int, Net>();
        for (int p = 1; p <= 19; p++)
            if (p != 10) nets[p] = new Net(p);
        return nets;
    }

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