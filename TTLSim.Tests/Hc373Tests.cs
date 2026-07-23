using System.Collections.Generic;
using TTLSim.Chips;
using TTLSim.Chips.Passives;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74HC373 transparent latch.
///
/// The '373 has no chip class of its own: it is the '573 core wired to the
/// INTERLEAVED pinout, exactly as the '574 is the '374 core wired to the
/// flow-through one. The latch behaviour is therefore already pinned by
/// <see cref="Hc573Tests"/>, and what these tests exist to catch is the one
/// thing that is genuinely new -- the pin map in
/// <c>ChipFactory.TryCreateHc373</c>. So every rig here is built THROUGH the
/// factory from a BuildDevice, never by constructing a chip class directly:
/// a transposed D or Q pin would sail past a direct-construction test and
/// fail silently on a real schematic.
///
/// The interleave under test (identical to the '374 frame, LE on pin 11
/// where the '374 has CLK):
///   /OE = 1                                   output enable, active LOW
///   LE  = 11                                  latch enable, active HIGH
///   D0..D7 = pins 3, 4, 7, 8, 13, 14, 17, 18
///   Q0..Q7 = pins 2, 5, 6, 9, 12, 15, 16, 19
/// </summary>
public class Hc373Tests
{
    // Bit k's data pin and output pin. Index = bit number.
    private static readonly int[] DPins = { 3, 4, 7, 8, 13, 14, 17, 18 };
    private static readonly int[] QPins = { 2, 5, 6, 9, 12, 15, 16, 19 };

    private const int OePin = 1;
    private const int LePin = 11;

    private sealed class Rig
    {
        public Simulator Sim = null!;
        public ManualClock Oe = null!, Le = null!;
        public ManualClock[] D = null!;                  // by bit, not by pin
        public Net[] Q = null!;                          // by bit, not by pin
    }

    /// <summary>
    /// Build a '373 the way the simulator does: a BuildDevice through
    /// ChipFactory, with one net per signal pin. Power pins (10, 20) are
    /// consumed by the build pipeline and never reach the chip model.
    /// </summary>
    private static Rig Build(bool pullQsLow = false, bool wireLe = true)
    {
        Rig r = new();

        // One net per signal pin, keyed by PIN NUMBER -- the factory's view.
        Dictionary<int, Net> pinToNet = new();
        int id = 0;
        pinToNet[OePin] = new Net(id++);
        if (wireLe) pinToNet[LePin] = new Net(id++);
        foreach (int pin in DPins) pinToNet[pin] = new Net(id++);
        foreach (int pin in QPins) pinToNet[pin] = new Net(id++);

        List<int> inputPins = new() { OePin };
        if (wireLe) inputPins.Add(LePin);
        inputPins.AddRange(DPins);

        BuildUnit unit = new("u373", '\0', inputPins, null, OutputPinNumbers: QPins);
        BuildDevice device = new(
            "dev373", "U1", "373", "HC",
            PowerPinNumber: 20, GroundPinNumber: 10,
            Units: new[] { unit });

        Dictionary<string, IReadOnlyDictionary<int, Net>> unitPinMaps = new()
        {
            ["u373"] = pinToNet
        };

        List<IChip> chips = new(new ChipFactory().CreateForUnits(
            device, unitPinMaps, new Dictionary<int, Signal>()));

        if (!wireLe)
        {
            // Required-pin policy: no latch enable, no chip.
            Assert.Empty(chips);
            return r;
        }

        Assert.Single(chips);

        // Drive the control and data pins; observe the Q pins.
        r.Oe = new ManualClock(pinToNet[OePin]);
        r.Le = new ManualClock(pinToNet[LePin]);
        r.D = new ManualClock[8];
        r.Q = new Net[8];

        List<IChip> all = new(chips) { r.Oe, r.Le };
        for (int i = 0; i < 8; i++)
        {
            r.D[i] = new ManualClock(pinToNet[DPins[i]]);
            all.Add(r.D[i]);
            r.Q[i] = pinToNet[QPins[i]];
        }
        if (pullQsLow)
            for (int i = 0; i < 8; i++)
                all.Add(new PullDriver(r.Q[i], Signal.Low));

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), all);
        r.Sim.Start();
        r.Oe.SetLow(r.Sim);            // outputs enabled
        r.Le.SetLow(r.Sim);            // latch closed
        r.Sim.RunUntilQuiescent();
        return r;
    }

    private static void SetData(Rig r, int b)
    {
        for (int i = 0; i < 8; i++)
        {
            if (((b >> i) & 1) != 0) r.D[i].SetHigh(r.Sim);
            else r.D[i].SetLow(r.Sim);
        }
        r.Sim.RunUntilQuiescent();
    }

    private static void SetLe(Rig r, bool high)
    {
        if (high) r.Le.SetHigh(r.Sim); else r.Le.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();
    }

    private static int ReadQ(Rig r)
    {
        int v = 0;
        for (int i = 0; i < 8; i++)
            if (r.Q[i].Value == Signal.High) v |= 1 << i;
        return v;
    }

    [Fact]
    public void Each_D_pin_drives_exactly_its_own_Q_pin()
    {
        // THE POINT OF THIS FILE. A walking-ones pattern through the
        // interleave: any transposed pair in the factory's map -- swapped
        // nibbles, a D/Q crossover, a reversed Q order -- shows up here as
        // the wrong single bit, not as a subtle timing oddity three boards
        // later. (Bit-reversed Q labels are exactly what bit the '574 before
        // the 2026-07 fix.)
        Rig r = Build();
        SetLe(r, true);

        for (int bit = 0; bit < 8; bit++)
        {
            SetData(r, 1 << bit);
            Assert.Equal(1 << bit, ReadQ(r));
        }
    }

    [Fact]
    public void Open_latch_is_transparent_with_no_clock_anywhere()
    {
        // Level-sensitive, not edge-triggered: data moves with nothing
        // pulsing pin 11 (which on the '374 in this same frame would be CLK).
        Rig r = Build();
        SetLe(r, true);
        SetData(r, 0x3C);
        Assert.Equal(0x3C, ReadQ(r));
        SetData(r, 0xA5);
        Assert.Equal(0xA5, ReadQ(r));
        SetData(r, 0x00);
        Assert.Equal(0x00, ReadQ(r));
    }

    [Fact]
    public void Falling_LE_keeps_exactly_what_D_held_at_that_moment()
    {
        Rig r = Build();
        SetLe(r, true);
        SetData(r, 0xA5);
        SetLe(r, false);                     // latch closes on 0xA5
        SetData(r, 0x5A);                    // too late -- ignored
        Assert.Equal(0xA5, ReadQ(r));
        SetData(r, 0xFF);
        Assert.Equal(0xA5, ReadQ(r));
    }

    [Fact]
    public void Reopening_the_latch_resyncs_to_the_live_data_immediately()
    {
        Rig r = Build();
        SetLe(r, true);
        SetData(r, 0x11);
        SetLe(r, false);
        SetData(r, 0xEE);                    // held out while closed
        Assert.Equal(0x11, ReadQ(r));

        SetLe(r, true);                      // opening alone -- no data change
        Assert.Equal(0xEE, ReadQ(r));        // must flush the live value through
    }

    [Fact]
    public void OE_releases_the_bus_while_the_latch_operates_underneath()
    {
        Rig r = Build(pullQsLow: true);
        SetLe(r, true);
        SetData(r, 0xFF);
        Assert.Equal(0xFF, ReadQ(r));        // driving all-High against the pulls

        r.Oe.SetHigh(r.Sim);                 // release the bus
        r.Sim.RunUntilQuiescent();
        for (int i = 0; i < 8; i++)
            Assert.Equal(Signal.Low, r.Q[i].Value);   // pulls win -> genuinely high-Z

        // The latch is alive under the released bus.
        SetData(r, 0x69);
        SetLe(r, false);

        r.Oe.SetLow(r.Sim);                  // re-enable
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x69, ReadQ(r));        // reveals what happened in the dark
    }

    [Fact]
    public void A_373_with_no_latch_enable_produces_no_chip()
    {
        // Required-pin policy, same as the '573: an unwired LE is a real
        // fault, not a part to be simulated with a guessed enable. The
        // builder turns the empty result into its "device produced no chip"
        // diagnostic rather than letting the latch vanish silently.
        Build(wireLe: false);
    }
}
