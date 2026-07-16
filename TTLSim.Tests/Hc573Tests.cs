using TTLSim.Chips.Passives;
using TTLSim.Chips.Registers;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74HC573 transparent latch. Data rides ManualClocks so it
/// can move mid-run with no clock anywhere in the rig -- the whole point
/// of the part is that there is no edge. High-Z observation follows the
/// house pattern: weak pulls on the Q nets that only win when the chip
/// has genuinely released them.
///
/// The behaviours pinned here are the level-sensitive family's contract
/// (cf. the '670 write-port tests): Q follows D through an open latch;
/// the HIGH-to-LOW LE transition keeps exactly what D held at that
/// moment; and /OE is fully independent -- the latch keeps operating
/// under a released bus.
/// </summary>
public class Hc573Tests
{
    private sealed class Rig
    {
        public Simulator Sim = null!;
        public ManualClock Oe = null!, Le = null!;
        public ManualClock[] D = null!;                 // D0..D7
        public Net[] Q = null!;                          // Q0..Q7
    }

    private static Rig Build(bool pullQsLow = false)
    {
        Rig r = new();
        int id = 0;
        Net N() => new(id++);

        Net oe = N(), le = N();
        Net[] d = new Net[8];
        for (int i = 0; i < 8; i++) d[i] = N();
        r.Q = new Net[8];
        for (int i = 0; i < 8; i++) r.Q[i] = N();

        Hc573 latch = new(
            oeN: oe, le: le,
            d0: d[0], d1: d[1], d2: d[2], d3: d[3],
            d4: d[4], d5: d[5], d6: d[6], d7: d[7],
            q0: r.Q[0], q1: r.Q[1], q2: r.Q[2], q3: r.Q[3],
            q4: r.Q[4], q5: r.Q[5], q6: r.Q[6], q7: r.Q[7]);

        r.Oe = new ManualClock(oe);
        r.Le = new ManualClock(le);
        r.D = new ManualClock[8];
        var chips = new System.Collections.Generic.List<IChip> { latch, r.Oe, r.Le };
        for (int i = 0; i < 8; i++)
        {
            r.D[i] = new ManualClock(d[i]);
            chips.Add(r.D[i]);
        }
        if (pullQsLow)
            for (int i = 0; i < 8; i++)
                chips.Add(new PullDriver(r.Q[i], Signal.Low));

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), chips);
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
    public void Open_latch_is_transparent_with_no_clock_anywhere()
    {
        var r = Build();
        SetLe(r, true);
        SetData(r, 0x3C);
        Assert.Equal(0x3C, ReadQ(r));
        SetData(r, 0xA5);                    // just a data change -- flows through
        Assert.Equal(0xA5, ReadQ(r));
        SetData(r, 0x00);
        Assert.Equal(0x00, ReadQ(r));
    }

    [Fact]
    public void Falling_LE_keeps_exactly_what_D_held_at_that_moment()
    {
        var r = Build();
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
        var r = Build();
        SetLe(r, true);
        SetData(r, 0x11);
        SetLe(r, false);
        SetData(r, 0xEE);                    // held out while closed
        Assert.Equal(0x11, ReadQ(r));

        SetLe(r, true);                      // opening alone -- no data change --
        Assert.Equal(0xEE, ReadQ(r));        // must flush the live value through
    }

    [Fact]
    public void OE_releases_the_bus_while_the_latch_operates_underneath()
    {
        var r = Build(pullQsLow: true);
        SetLe(r, true);
        SetData(r, 0xFF);
        Assert.Equal(0xFF, ReadQ(r));        // driving all-High against the pulls

        r.Oe.SetHigh(r.Sim);                 // release the bus
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x00, ReadQ(r));        // pulls win -> genuinely high-Z

        // The latch is alive under the released bus: track new data through
        // the open latch, then CLOSE it, all while /OE is high.
        SetData(r, 0x69);
        SetLe(r, false);

        r.Oe.SetLow(r.Sim);                  // re-enable
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x69, ReadQ(r));        // reveals what happened in the dark
    }

    [Fact]
    public void Undriven_LE_reads_as_latched_not_transparent()
    {
        // LE never driven: Unknown maps to Low = hold, the safe direction.
        Rig r = new();
        int id = 500;
        Net N() => new(id++);

        Net oe = N(), le = N();               // le gets NO driver
        Net[] d = new Net[8];
        for (int i = 0; i < 8; i++) d[i] = N();
        r.Q = new Net[8];
        for (int i = 0; i < 8; i++) r.Q[i] = N();

        Hc573 latch = new(
            oeN: oe, le: le,
            d0: d[0], d1: d[1], d2: d[2], d3: d[3],
            d4: d[4], d5: d[5], d6: d[6], d7: d[7],
            q0: r.Q[0], q1: r.Q[1], q2: r.Q[2], q3: r.Q[3],
            q4: r.Q[4], q5: r.Q[5], q6: r.Q[6], q7: r.Q[7]);

        r.Oe = new ManualClock(oe);
        r.D = new ManualClock[8];
        var chips = new System.Collections.Generic.List<IChip> { latch, r.Oe };
        for (int i = 0; i < 8; i++) { r.D[i] = new ManualClock(d[i]); chips.Add(r.D[i]); }

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), chips);
        r.Sim.Start();
        r.Oe.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();

        SetData(r, 0xFF);                     // data changes must NOT flow
        Assert.Equal(0x00, ReadQ(r));         // still the power-up zero
    }
}
