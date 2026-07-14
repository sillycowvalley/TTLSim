using TTLSim.Chips.Registers;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74HC670 4x4 register file model. Every input rides a
/// ManualClock so the tests can move data, addresses, and the two enables
/// independently mid-run. The rig raises /GW immediately after Start, so
/// every test begins with the write latch CLOSED, the read port ENABLED
/// (/GR low), and all four words zero.
///
/// The transparency tests are the point of the model: the '670 write is a
/// level-sensitive latch, and the Blinky/Thumby clock-low /GW gating
/// discipline exists precisely because of the behaviours pinned down here
/// (Q follows D through an open latch; an address move under an open latch
/// smears the data into the newly addressed word).
/// </summary>
public class Hc670Tests
{
    private sealed class Rig
    {
        public Simulator Sim = null!;
        public ManualClock[] D = null!;                     // D1..D4 (index = bit)
        public ManualClock Wa = null!, Wb = null!, GwN = null!;
        public ManualClock Ra = null!, Rb = null!, GrN = null!;
        public Net[] Q = null!;                             // Q1..Q4 (index = bit)
    }

    private static Rig Build()
    {
        Rig r = new();
        int id = 0;
        Net N() => new(id++);

        Net d1 = N(), d2 = N(), d3 = N(), d4 = N();
        Net wa = N(), wb = N(), gw = N();
        Net ra = N(), rb = N(), gr = N();
        r.Q = new[] { N(), N(), N(), N() };

        Hc670 rf = new(
            d1: d1, d2: d2, d3: d3, d4: d4,
            wa: wa, wb: wb, gwN: gw,
            ra: ra, rb: rb, grN: gr,
            q1: r.Q[0], q2: r.Q[1], q3: r.Q[2], q4: r.Q[3]);

        r.D = new[] { new ManualClock(d1), new ManualClock(d2),
                      new ManualClock(d3), new ManualClock(d4) };
        r.Wa = new ManualClock(wa); r.Wb = new ManualClock(wb); r.GwN = new ManualClock(gw);
        r.Ra = new ManualClock(ra); r.Rb = new ManualClock(rb); r.GrN = new ManualClock(gr);

        r.Sim = new Simulator(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { rf, r.D[0], r.D[1], r.D[2], r.D[3],
                          r.Wa, r.Wb, r.GwN, r.Ra, r.Rb, r.GrN });
        r.Sim.Start();
        r.GwN.SetHigh(r.Sim);          // close the write latch
        r.Sim.RunUntilQuiescent();
        return r;
    }

    private static void Set(Rig r, ManualClock src, bool high)
    {
        if (high) src.SetHigh(r.Sim); else src.SetLow(r.Sim);
    }

    private static void SetData(Rig r, int nibble)
    {
        for (int i = 0; i < 4; i++)
            Set(r, r.D[i], ((nibble >> i) & 1) != 0);
        r.Sim.RunUntilQuiescent();
    }

    private static void SetWriteAddr(Rig r, int addr)
    {
        Set(r, r.Wa, (addr & 1) != 0);
        Set(r, r.Wb, (addr & 2) != 0);
        r.Sim.RunUntilQuiescent();
    }

    private static void SetReadAddr(Rig r, int addr)
    {
        Set(r, r.Ra, (addr & 1) != 0);
        Set(r, r.Rb, (addr & 2) != 0);
        r.Sim.RunUntilQuiescent();
    }

    // A disciplined write: address and data settled BEFORE the /GW pulse --
    // exactly what the clock-low gating guarantees on the board.
    private static void WritePulse(Rig r, int addr, int nibble)
    {
        SetWriteAddr(r, addr);
        SetData(r, nibble);
        r.GwN.SetLow(r.Sim);  r.Sim.RunUntilQuiescent();
        r.GwN.SetHigh(r.Sim); r.Sim.RunUntilQuiescent();
    }

    private static int ReadQ(Rig r)
    {
        int v = 0;
        for (int i = 0; i < 4; i++)
            if (r.Q[i].Value == Signal.High) v |= 1 << i;
        return v;
    }

    [Fact]
    public void Powers_up_zero_on_every_word()
    {
        Rig r = Build();
        for (int addr = 0; addr < 4; addr++)
        {
            SetReadAddr(r, addr);
            Assert.Equal(0, ReadQ(r));
        }
    }

    [Fact]
    public void Writes_and_reads_back_all_four_words()
    {
        Rig r = Build();
        int[] values = { 0x5, 0xA, 0x3, 0xC };

        for (int addr = 0; addr < 4; addr++)
            WritePulse(r, addr, values[addr]);

        // Walk the read address both directions -- the ports are
        // independent, so the last write address must not matter.
        for (int addr = 0; addr < 4; addr++)
        {
            SetReadAddr(r, addr);
            Assert.Equal(values[addr], ReadQ(r));
        }
        for (int addr = 3; addr >= 0; addr--)
        {
            SetReadAddr(r, addr);
            Assert.Equal(values[addr], ReadQ(r));
        }
    }

    [Fact]
    public void Outputs_are_highZ_when_GR_high_and_return_when_reenabled()
    {
        Rig r = Build();
        WritePulse(r, 1, 0x9);
        SetReadAddr(r, 1);
        Assert.Equal(0x9, ReadQ(r));

        r.GrN.SetHigh(r.Sim);
        r.Sim.RunUntilQuiescent();
        for (int i = 0; i < 4; i++)
            Assert.Equal(Signal.HighZ, r.Q[i].Value);

        r.GrN.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x9, ReadQ(r));
    }

    [Fact]
    public void Write_latch_is_transparent_while_GW_low()
    {
        // THE TRAP, part 1: with the latch open, Q (same word) follows D.
        Rig r = Build();
        SetReadAddr(r, 2);
        SetWriteAddr(r, 2);
        SetData(r, 0x9);

        r.GwN.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x9, ReadQ(r));

        // D moves while the latch is open -- the word (and Q) follow.
        SetData(r, 0x6);
        Assert.Equal(0x6, ReadQ(r));

        // Latch closes; D moves again; the word holds.
        r.GwN.SetHigh(r.Sim);
        r.Sim.RunUntilQuiescent();
        SetData(r, 0xF);
        Assert.Equal(0x6, ReadQ(r));
    }

    [Fact]
    public void Write_address_move_under_open_latch_smears_data()
    {
        // THE TRAP, part 2: moving WA/WB while /GW is low opens the newly
        // addressed word to D as well -- both words end up written. This is
        // the garbage-write hazard the /GW = WE AND CLK-low gating fences.
        Rig r = Build();
        WritePulse(r, 0, 0x2);
        WritePulse(r, 1, 0x4);

        SetWriteAddr(r, 0);
        SetData(r, 0x7);
        r.GwN.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();

        SetWriteAddr(r, 1);        // latch still open
        r.GwN.SetHigh(r.Sim);
        r.Sim.RunUntilQuiescent();

        SetReadAddr(r, 0);
        Assert.Equal(0x7, ReadQ(r));   // original target written
        SetReadAddr(r, 1);
        Assert.Equal(0x7, ReadQ(r));   // and the word the address moved across
    }

    [Fact]
    public void Read_port_is_undisturbed_by_a_write_to_another_word()
    {
        Rig r = Build();
        WritePulse(r, 1, 0x4);
        SetReadAddr(r, 1);
        Assert.Equal(0x4, ReadQ(r));

        // Open the latch on word 3 while reading word 1 -- the read port
        // must not blink.
        SetWriteAddr(r, 3);
        SetData(r, 0xB);
        r.GwN.SetLow(r.Sim);
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x4, ReadQ(r));

        r.GwN.SetHigh(r.Sim);
        r.Sim.RunUntilQuiescent();
        Assert.Equal(0x4, ReadQ(r));

        SetReadAddr(r, 3);
        Assert.Equal(0xB, ReadQ(r));
    }
}
