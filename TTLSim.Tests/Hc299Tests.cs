using TTLSim.Chips.Passives;
using TTLSim.Chips.Registers;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74HC299 8-bit universal shift register. Control inputs
/// ride ManualClocks; parallel LOAD DATA rides weak PullDrivers on the
/// I/O nets -- the chip's own drivers are high-Z in load mode so the
/// pulls own the bus, and once the chip drives again its Strong drivers
/// override the pulls with no contention. That is also exactly how the
/// tests observe high-Z: a pull that "wins" proves the buffer released.
///
/// The behaviour this file exists to pin down is the split personality:
/// the register is edge-triggered but the I/O buffer enable is
/// COMBINATIONAL -- S1=S0=HIGH or either /OE HIGH releases the bus
/// immediately, no clock edge involved.
/// </summary>
public class Hc299Tests
{
    private sealed class Rig
    {
        public Simulator Sim = null!;
        public ManualClock S0 = null!, S1 = null!, Oe1 = null!, Oe2 = null!;
        public ManualClock Clr = null!, Dsr = null!, Dsl = null!, Clk = null!;
        public Net[] Io = null!;                        // I/O0..I/O7
        public Net Q0Tap = null!, Q7Tap = null!;
        public IChip Chip = null!;
    }

    /// <summary>
    /// Build one '299 with all controls on ManualClocks and the given weak
    /// pulls installed on the I/O nets (bit -> pull level). The rig starts
    /// every test with /CLR released, both /OEs low (buffers enabled), and
    /// the clock parked low.
    /// </summary>
    private static Rig Build(int pullPattern = 0)
    {
        Rig r = new();
        int id = 0;
        Net N() => new(id++);

        Net s0 = N(), oe1 = N(), oe2 = N(), clr = N();
        Net dsr = N(), clk = N(), dsl = N(), s1 = N();
        r.Io = new Net[8];
        for (int i = 0; i < 8; i++) r.Io[i] = N();
        r.Q0Tap = N(); r.Q7Tap = N();

        Hc299 sr = new(
            s0: s0, oe1N: oe1, oe2N: oe2, clrN: clr,
            dsr: dsr, clkN: clk, dsl: dsl, s1: s1,
            io0: r.Io[0], io1: r.Io[1], io2: r.Io[2], io3: r.Io[3],
            io4: r.Io[4], io5: r.Io[5], io6: r.Io[6], io7: r.Io[7],
            q0Tap: r.Q0Tap, q7Tap: r.Q7Tap);
        r.Chip = sr;

        r.S0 = new ManualClock(s0); r.S1 = new ManualClock(s1);
        r.Oe1 = new ManualClock(oe1); r.Oe2 = new ManualClock(oe2);
        r.Clr = new ManualClock(clr); r.Dsr = new ManualClock(dsr);
        r.Dsl = new ManualClock(dsl); r.Clk = new ManualClock(clk);

        var chips = new System.Collections.Generic.List<IChip>
            { sr, r.S0, r.S1, r.Oe1, r.Oe2, r.Clr, r.Dsr, r.Dsl, r.Clk };
        for (int i = 0; i < 8; i++)
            chips.Add(new PullDriver(r.Io[i],
                ((pullPattern >> i) & 1) != 0 ? Signal.High : Signal.Low));

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), chips);
        r.Sim.Start();
        r.Clr.SetHigh(r.Sim);          // /CLR inactive
        r.Oe1.SetLow(r.Sim);           // buffers enabled
        r.Oe2.SetLow(r.Sim);
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

    private static void Pulse(Rig r)
    {
        r.Clk.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();
        r.Clk.SetHigh(r.Sim);
        r.Sim.RunUntilQuiescent();
    }

    private static int ReadIo(Rig r)
    {
        int v = 0;
        for (int i = 0; i < 8; i++)
            if (r.Io[i].Value == Signal.High) v |= 1 << i;
        return v;
    }

    /// <summary>Load a byte: the pulls installed at Build() carry it.</summary>
    private static void Load(Rig r)
    {
        SetMode(r, 0b11);               // buffers release; pulls own the bus
        Pulse(r);                        // edge samples the pulled pattern
        SetMode(r, 0b00);                // hold; chip drives again
    }

    // ------------------------------------------------------ load and hold

    [Fact]
    public void Parallel_load_captures_the_external_bus()
    {
        var r = Build(pullPattern: 0x5A);
        Load(r);
        Assert.Equal(0x5A, ReadIo(r));                    // chip now drives it
        Assert.Equal(Signal.Low, r.Q0Tap.Value);          // bit 0 of 0x5A
        Assert.Equal(Signal.Low, r.Q7Tap.Value);          // bit 7 of 0x5A
    }

    [Fact]
    public void Hold_retains_across_edges()
    {
        var r = Build(pullPattern: 0xC3);
        Load(r);
        Pulse(r);
        Pulse(r);
        Assert.Equal(0xC3, ReadIo(r));
    }

    // ------------------------------------------------------------ shifting

    [Fact]
    public void Mode_01_shifts_toward_Q7_with_DSR_entering_at_bit0()
    {
        var r = Build(pullPattern: 0x81);
        Load(r);
        SetMode(r, 0b01);

        Set(r, r.Dsr, true);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0x03, ReadIo(r));      // 0x81<<1 = 0x02, DSR=1 in at bit 0
        Assert.Equal(Signal.Low, r.Q7Tap.Value);   // old bit 7 fell off

        Set(r, r.Dsr, false);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0x06, ReadIo(r));
    }

    [Fact]
    public void Mode_10_shifts_toward_Q0_with_DSL_entering_at_bit7()
    {
        var r = Build(pullPattern: 0x81);
        Load(r);
        SetMode(r, 0b10);

        Set(r, r.Dsl, true);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0xC0, ReadIo(r));      // 0x81>>1 = 0x40, DSL=1 in at bit 7
        Assert.Equal(Signal.Low, r.Q0Tap.Value);   // old bit 0 fell off

        Set(r, r.Dsl, false);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0x60, ReadIo(r));
    }

    // -------------------------------------- the combinational bus release

    [Fact]
    public void Selecting_load_mode_releases_the_bus_without_any_clock_edge()
    {
        var r = Build(pullPattern: 0x00);   // pull-downs everywhere
        // Get the register to all-ones so driven-High vs released is visible.
        SetMode(r, 0b01);
        Set(r, r.Dsr, true);
        r.Sim.RunUntilQuiescent();
        for (int i = 0; i < 8; i++) Pulse(r);
        Assert.Equal(0xFF, ReadIo(r));      // chip drives all High against pulls

        // S1=S0=HIGH with the clock PARKED: the buffers must let go
        // immediately and the pull-downs win on every I/O net.
        SetMode(r, 0b11);
        Assert.Equal(0x00, ReadIo(r));
        Assert.Equal(Signal.High, r.Q7Tap.Value);   // taps still drive (q untouched)

        // Leave load mode without ever clocking: the byte comes back.
        SetMode(r, 0b00);
        Assert.Equal(0xFF, ReadIo(r));
    }

    [Fact]
    public void Either_OE_high_releases_the_bus_but_register_still_operates()
    {
        var r = Build(pullPattern: 0x00);
        SetMode(r, 0b01);
        Set(r, r.Dsr, true);
        r.Sim.RunUntilQuiescent();
        Pulse(r);
        Assert.Equal(0x01, ReadIo(r));

        Set(r, r.Oe1, true);                // one enable high -> high-Z
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x00, ReadIo(r));      // pulls win

        // "The shift, hold, load and reset operations can still occur":
        // two more shifted 1s arrive while the bus is released.
        Pulse(r);
        Pulse(r);
        Assert.Equal(Signal.Low, r.Q7Tap.Value);   // q = 0x07, bit 7 still 0

        Set(r, r.Oe1, false);               // re-enable: current q appears
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x07, ReadIo(r));
    }

    // --------------------------------------------------------- async clear

    [Fact]
    public void Clear_is_asynchronous_and_pins_the_register_against_the_clock()
    {
        var r = Build(pullPattern: 0xFF);   // pull-ups: load data all-ones
        Load(r);
        Assert.Equal(0xFF, ReadIo(r));

        Set(r, r.Clr, false);               // clock parked: no edge involved
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x00, ReadIo(r));
        Assert.Equal(Signal.Low, r.Q0Tap.Value);
        Assert.Equal(Signal.Low, r.Q7Tap.Value);

        SetMode(r, 0b11);                   // try to load past the clear
        Pulse(r);
        SetMode(r, 0b00);
        Assert.Equal(0x00, ReadIo(r));

        Set(r, r.Clr, true);                // release: still zero until an edge acts
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x00, ReadIo(r));
    }

    // -------------------------------------------------- two-chip cascade

    /// <summary>
    /// The UART composition: chip A's Q7 tap feeds chip B's DSR, both in
    /// mode 01, so eight edges stream A's byte into B MSB-first while A
    /// backfills with zeros. This is the 16-bit expansion pattern the
    /// dedicated serial taps exist for -- and it exercises the tap
    /// settling between edges through a second chip's edge-sampled input.
    /// </summary>
    [Fact]
    public void Serial_cascade_streams_a_byte_from_A_to_B()
    {
        int id = 1000;
        Net N() => new(id++);

        // Shared controls: one mode, one clock, one /CLR for both chips.
        Net s0 = N(), s1 = N(), clr = N(), clk = N();
        Net oeA1 = N(), oeA2 = N(), oeB1 = N(), oeB2 = N();
        Net dslA = N(), dslB = N(), dsrA = N();
        Net link = N();                     // A.Q7 -> B.DSR
        Net q0A = N(), q0B = N(), q7B = N();

        Net[] ioA = new Net[8], ioB = new Net[8];
        for (int i = 0; i < 8; i++) { ioA[i] = N(); ioB[i] = N(); }

        Hc299 a = new(s0, oeA1, oeA2, clr, dsrA, clk, dslA, s1,
            ioA[0], ioA[1], ioA[2], ioA[3], ioA[4], ioA[5], ioA[6], ioA[7],
            q0A, link, label: "299A");
        Hc299 b = new(s0, oeB1, oeB2, clr, link, clk, dslB, s1,
            ioB[0], ioB[1], ioB[2], ioB[3], ioB[4], ioB[5], ioB[6], ioB[7],
            q0B, q7B, label: "299B");

        ManualClock mS0 = new(s0), mS1 = new(s1), mClr = new(clr), mClk = new(clk);
        var chips = new System.Collections.Generic.List<IChip>
        {
            a, b, mS0, mS1, mClr, mClk,
            new TTLSim.Chips.Sources.GndDriver(oeA1), new TTLSim.Chips.Sources.GndDriver(oeA2),
            new TTLSim.Chips.Sources.GndDriver(oeB1), new TTLSim.Chips.Sources.GndDriver(oeB2),
            new TTLSim.Chips.Sources.GndDriver(dslA), new TTLSim.Chips.Sources.GndDriver(dslB),
            new TTLSim.Chips.Sources.GndDriver(dsrA),
        };
        // Load data for A: pulls carrying 0xB4; B's bus pulled low.
        const int payload = 0xB4;
        for (int i = 0; i < 8; i++)
        {
            chips.Add(new PullDriver(ioA[i],
                ((payload >> i) & 1) != 0 ? Signal.High : Signal.Low));
            chips.Add(new PullDriver(ioB[i], Signal.Low));
        }

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()), chips);
        sim.Start();
        mClr.SetHigh(sim);
        mClk.SetLow(sim);
        sim.RunUntilQuiescent();

        void Mode(int m)
        {
            if ((m & 2) != 0) mS1.SetHigh(sim); else mS1.SetLow(sim);
            if ((m & 1) != 0) mS0.SetHigh(sim); else mS0.SetLow(sim);
            sim.RunUntilQuiescent();
        }
        void Tick()
        {
            mClk.SetLow(sim); sim.RunUntilQuiescent();
            mClk.SetHigh(sim); sim.RunUntilQuiescent();
        }
        int Read(Net[] io)
        {
            int v = 0;
            for (int i = 0; i < 8; i++) if (io[i].Value == Signal.High) v |= 1 << i;
            return v;
        }

        Mode(0b11); Tick();                 // both load: A = 0xB4, B = 0x00
        Mode(0b01);                          // both shift toward Q7
        for (int i = 0; i < 8; i++) Tick();  // stream A's byte across the link
        Mode(0b00);

        // Bit k of A exits its Q7 tap on edge (7-k) and lands in B's bit 0,
        // then climbs -- after 8 edges B holds A's original byte and A has
        // backfilled with DSR=0.
        Assert.Equal(payload, Read(ioB));
        Assert.Equal(0x00, Read(ioA));
    }
}
