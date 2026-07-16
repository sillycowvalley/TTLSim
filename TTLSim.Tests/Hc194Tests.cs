using TTLSim.Chips.Multiplexers;
using TTLSim.Chips.Registers;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74HC194 bidirectional universal shift register. Every
/// input rides a ManualClock ('670-test style) so modes, data, and serial
/// inputs can move independently mid-run. Direction language follows the
/// model, not the datasheet: mode 01 moves data toward Q3 (DSR in at Q0),
/// mode 10 moves data toward Q0 (DSL in at Q3).
///
/// The last test closes the loop the chip exists for: a '153 serial-fill
/// mux selecting 0 / Q3 / Q0 into DSL turns a toward-Q0 shift into
/// SHR / ASR / ROR respectively -- the ALU Rev 2 TOS shape, with the
/// Q-to-mux feedback settling between edges exactly as on the board.
/// </summary>
public class Hc194Tests
{
    private sealed class Rig
    {
        public Simulator Sim = null!;
        public ManualClock Clr = null!, Dsr = null!, Dsl = null!;
        public ManualClock[] D = null!;                     // D0..D3
        public ManualClock S0 = null!, S1 = null!, Clk = null!;
        public Net[] Q = null!;                             // Q0..Q3
    }

    private static Rig Build()
    {
        Rig r = new();
        int id = 0;
        Net N() => new(id++);

        Net clr = N(), dsr = N();
        Net d0 = N(), d1 = N(), d2 = N(), d3 = N();
        Net dsl = N(), s0 = N(), s1 = N(), clk = N();
        r.Q = new[] { N(), N(), N(), N() };

        Hc194 sr = new(
            clrN: clr, dsr: dsr,
            d0: d0, d1: d1, d2: d2, d3: d3,
            dsl: dsl, s0: s0, s1: s1, clkN: clk,
            q0: r.Q[0], q1: r.Q[1], q2: r.Q[2], q3: r.Q[3]);

        r.Clr = new ManualClock(clr); r.Dsr = new ManualClock(dsr);
        r.D = new[] { new ManualClock(d0), new ManualClock(d1),
                      new ManualClock(d2), new ManualClock(d3) };
        r.Dsl = new ManualClock(dsl);
        r.S0 = new ManualClock(s0); r.S1 = new ManualClock(s1);
        r.Clk = new ManualClock(clk);

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { sr, r.Clr, r.Dsr, r.D[0], r.D[1], r.D[2], r.D[3],
                          r.Dsl, r.S0, r.S1, r.Clk });
        r.Sim.Start();
        r.Clr.SetHigh(r.Sim);          // /CLR inactive
        r.Clk.SetLow(r.Sim);           // clock parked low
        r.Sim.RunUntilQuiescent();
        return r;
    }

    private static void Set(Rig r, ManualClock src, bool high)
    {
        if (high) src.SetHigh(r.Sim); else src.SetLow(r.Sim);
    }

    private static void SetMode(Rig r, int mode)
    {
        Set(r, r.S1, (mode & 2) != 0);
        Set(r, r.S0, (mode & 1) != 0);
        r.Sim.RunUntilQuiescent();
    }

    private static void SetData(Rig r, int nibble)
    {
        for (int i = 0; i < 4; i++)
            Set(r, r.D[i], ((nibble >> i) & 1) != 0);
        r.Sim.RunUntilQuiescent();
    }

    private static void Pulse(Rig r)
    {
        r.Clk.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();
        r.Clk.SetHigh(r.Sim);          // rising edge -- the active edge
        r.Sim.RunUntilQuiescent();
    }

    private static int ReadQ(Rig r)
    {
        int v = 0;
        for (int i = 0; i < 4; i++)
            if (r.Q[i].Value == Signal.High) v |= 1 << i;
        return v;
    }

    private static void Load(Rig r, int nibble)
    {
        SetMode(r, 0b11);
        SetData(r, nibble);
        Pulse(r);
    }

    // ------------------------------------------------------ the four modes

    [Fact]
    public void Parallel_load_captures_data_on_rising_edge()
    {
        var r = Build();
        Load(r, 0b1010);
        Assert.Equal(0b1010, ReadQ(r));
    }

    [Fact]
    public void Hold_mode_retains_data_across_edges()
    {
        var r = Build();
        Load(r, 0b0110);
        SetMode(r, 0b00);
        Pulse(r);
        Pulse(r);
        Assert.Equal(0b0110, ReadQ(r));
    }

    [Fact]
    public void Mode_01_shifts_toward_Q3_with_DSR_entering_at_Q0()
    {
        var r = Build();
        Load(r, 0b0011);
        SetMode(r, 0b01);

        Set(r, r.Dsr, true);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0b0111, ReadQ(r));    // 0011 <<, DSR=1 in at Q0

        Set(r, r.Dsr, false);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0b1110, ReadQ(r));    // 0111 <<, DSR=0 in at Q0
    }

    [Fact]
    public void Mode_10_shifts_toward_Q0_with_DSL_entering_at_Q3()
    {
        var r = Build();
        Load(r, 0b1100);
        SetMode(r, 0b10);

        Set(r, r.Dsl, true);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0b1110, ReadQ(r));    // 1100 >>, DSL=1 in at Q3

        Set(r, r.Dsl, false);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0b0111, ReadQ(r));    // 1110 >>, DSL=0 in at Q3
    }

    // ------------------------------------------------------- edge sampling

    [Fact]
    public void Mode_and_data_changes_between_edges_do_nothing()
    {
        var r = Build();
        Load(r, 0b0101);

        // Park in hold, then wiggle mode into LOAD with different data and
        // back WITHOUT a clock edge -- nothing may change.
        SetMode(r, 0b00);
        SetData(r, 0b1111);
        SetMode(r, 0b11);
        SetMode(r, 0b00);
        Assert.Equal(0b0101, ReadQ(r));

        // The next edge samples the CURRENT mode (hold), not any past one.
        Pulse(r);
        Assert.Equal(0b0101, ReadQ(r));
    }

    // --------------------------------------------------------- async clear

    [Fact]
    public void Clear_is_asynchronous_and_overrides_the_clock()
    {
        var r = Build();
        Load(r, 0b1011);

        // Assert /CLR with the clock parked: outputs drop with no edge.
        Set(r, r.Clr, false);
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0, ReadQ(r));

        // Edges while /CLR is held low cannot load past it.
        SetMode(r, 0b11);
        SetData(r, 0b1111);
        Pulse(r);
        Assert.Equal(0, ReadQ(r));

        // Release /CLR: the register stays at 0 until the next edge acts.
        Set(r, r.Clr, true);
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0, ReadQ(r));
        Pulse(r);
        Assert.Equal(0b1111, ReadQ(r));
    }

    // ------------------------------------- '153 serial-fill integration

    /// <summary>
    /// The ALU Rev 2 TOS shape, scaled to one chip pair: a '153 section
    /// selects the fill bit into DSL while the '194 sits in mode 10
    /// (toward Q0 -- the arithmetic right shift with Q3 as MSB):
    ///   fill = 0        -> SHR (logical)
    ///   fill = old Q3   -> ASR (sign replicated)
    ///   fill = old Q0   -> ROR (wraparound)
    /// The Q3/Q0 feedback runs through the mux and settles between edges,
    /// which is exactly the closed loop the physical board relies on.
    /// (Select mapping here is illustrative -- verify the exact Rev 2
    /// IR-bit wiring against the ALU doc when the full board is captured.)
    /// </summary>
    [Fact]
    public void Serial_fill_mux_gives_SHR_ASR_ROR()
    {
        Rig r = null!;
        int id = 1000;
        Net N() => new(id++);

        r = new Rig();
        Net clr = N(), dsr = N();
        Net d0 = N(), d1 = N(), d2 = N(), d3 = N();
        Net dsl = N(), s0 = N(), s1 = N(), clk = N();
        r.Q = new[] { N(), N(), N(), N() };

        Hc194 sr = new(
            clrN: clr, dsr: dsr,
            d0: d0, d1: d1, d2: d2, d3: d3,
            dsl: dsl, s0: s0, s1: s1, clkN: clk,
            q0: r.Q[0], q1: r.Q[1], q2: r.Q[2], q3: r.Q[3]);

        // '153 section 1: 1I0 = fill 0, 1I1 = Q3 (ASR), 1I2 = Q0 (ROR),
        // 1I3 = unused; 1Y drives DSL. Section 2 idle on dummy nets.
        Net muxE1 = N(), muxS1 = N(), muxS0 = N(), muxI3 = N();
        Net dummyY2 = N(), dI20 = N(), dI21 = N(), dI22 = N(), dI23 = N(), muxE2 = N();
        Net fillLow = N();

        Hc153 mux = new(
            e1N: muxE1, s1: muxS1,
            i1_3: muxI3, i1_2: r.Q[0], i1_1: r.Q[3], i1_0: fillLow,
            y1: dsl,
            y2: dummyY2, i2_0: dI20, i2_1: dI21, i2_2: dI22, i2_3: dI23,
            s0: muxS0, e2N: muxE2);

        r.Clr = new ManualClock(clr); r.Dsr = new ManualClock(dsr);
        r.D = new[] { new ManualClock(d0), new ManualClock(d1),
                      new ManualClock(d2), new ManualClock(d3) };
        r.S0 = new ManualClock(s0); r.S1 = new ManualClock(s1);
        r.Clk = new ManualClock(clk);
        ManualClock mS1 = new(muxS1), mS0 = new(muxS0);
        GndDriverLike(out IChip e1Low, muxE1);
        GndDriverLike(out IChip e2Low, muxE2);    // section 2 enabled but inert: inputs read Low, 2Y unobserved
        GndDriverLike(out IChip fill0, fillLow);

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { sr, mux, r.Clr, r.Dsr, r.D[0], r.D[1], r.D[2], r.D[3],
                          r.S0, r.S1, r.Clk, mS1, mS0, e1Low, e2Low, fill0 });
        r.Sim.Start();
        r.Clr.SetHigh(r.Sim);
        r.Clk.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();

        void MuxSelect(int sel)
        {
            if ((sel & 2) != 0) mS1.SetHigh(r.Sim); else mS1.SetLow(r.Sim);
            if ((sel & 1) != 0) mS0.SetHigh(r.Sim); else mS0.SetLow(r.Sim);
            r.Sim.RunUntilQuiescent();
        }

        // Load 0b1001 (-7 signed, Q3 the sign), then park in mode 10.
        Load(r, 0b1001);
        SetMode(r, 0b10);

        // ASR (fill = Q3): 1001 -> 1100 -> 1110  (-7 -> -4 -> -2).
        MuxSelect(1);
        Pulse(r);
        Assert.Equal(0b1100, ReadQ(r));
        Pulse(r);
        Assert.Equal(0b1110, ReadQ(r));

        // ROR (fill = Q0): 1110 -> 0111 -> 1011.
        MuxSelect(2);
        Pulse(r);
        Assert.Equal(0b0111, ReadQ(r));
        Pulse(r);
        Assert.Equal(0b1011, ReadQ(r));

        // SHR (fill = 0): 1011 -> 0101.
        MuxSelect(0);
        Pulse(r);
        Assert.Equal(0b0101, ReadQ(r));
    }

    // Tiny helper: pin a net solidly Low via the standard source chip,
    // keeping the integration rig readable.
    private static void GndDriverLike(out IChip chip, Net net) =>
        chip = new TTLSim.Chips.Sources.GndDriver(net);
}
