using TTLSim.Chips.Counters;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for one half of a 74LS390. Each half is two independent counter
/// sections sharing a common async master reset:
///   * a ÷2 (CKA → QA), and
///   * a ÷5 (CKB → QB,QC,QD with QB as LSB, QD as MSB).
/// </summary>
public class Hc390Tests
{
    [Fact]
    public void Div2_toggles_QA_on_CKA_falling_edge()
    {
        Net cka = new(1), ckb = new(2), mr = new(3);
        Net qa = new(4), qb = new(5), qc = new(6), qd = new(7);

        Hc390Half cnt = new(cka, ckb, mr, qa, qb, qc, qd);
        ManualClock ckaSrc = new(cka);
        GndDriver ckbLow = new(ckb);
        GndDriver mrLow = new(mr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, ckaSrc, ckbLow, mrLow });

        sim.Start();
        sim.RunUntilQuiescent();
        Assert.Equal(Signal.Low, qa.Value);

        // Rising edge -> no count.
        ckaSrc.SetHigh(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.Low, qa.Value);

        // First falling edge -> QA toggles to HIGH.
        ckaSrc.SetLow(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.High, qa.Value);

        // Second falling edge -> QA toggles back to LOW.
        ckaSrc.SetHigh(sim);
        ckaSrc.SetLow(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.Low, qa.Value);
    }

    [Fact]
    public void Div5_walks_through_states_0_to_4_then_wraps()
    {
        Net cka = new(1), ckb = new(2), mr = new(3);
        Net qa = new(4), qb = new(5), qc = new(6), qd = new(7);

        Hc390Half cnt = new(cka, ckb, mr, qa, qb, qc, qd);
        GndDriver ckaLow = new(cka);
        ManualClock ckbSrc = new(ckb);
        GndDriver mrLow = new(mr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, ckaLow, ckbSrc, mrLow });

        sim.Start();
        sim.RunUntilQuiescent();
        Assert.Equal(0, ReadDiv5(qb, qc, qd));

        // Expected sequence on each falling edge: 1,2,3,4,0,1,...
        int[] expected = { 1, 2, 3, 4, 0, 1, 2 };
        for (int i = 0; i < expected.Length; i++)
        {
            ckbSrc.SetHigh(sim);
            ckbSrc.SetLow(sim);
            sim.RunUntil(sim.CurrentTick + 100_000);
            Assert.Equal(expected[i], ReadDiv5(qb, qc, qd));
        }

        // QA must not have moved while only CKB was pulsed.
        Assert.Equal(Signal.Low, qa.Value);
    }

    [Fact]
    public void Two_sections_are_independent()
    {
        // Pulse CKA twice and CKB three times. Expect QA=0 (toggled twice)
        // and div5=3.
        Net cka = new(1), ckb = new(2), mr = new(3);
        Net qa = new(4), qb = new(5), qc = new(6), qd = new(7);

        Hc390Half cnt = new(cka, ckb, mr, qa, qb, qc, qd);
        ManualClock ckaSrc = new(cka);
        ManualClock ckbSrc = new(ckb);
        GndDriver mrLow = new(mr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, ckaSrc, ckbSrc, mrLow });

        sim.Start();
        sim.RunUntilQuiescent();

        for (int i = 0; i < 2; i++)
        {
            ckaSrc.SetHigh(sim);
            ckaSrc.SetLow(sim);
            sim.RunUntil(sim.CurrentTick + 100_000);
        }
        for (int i = 0; i < 3; i++)
        {
            ckbSrc.SetHigh(sim);
            ckbSrc.SetLow(sim);
            sim.RunUntil(sim.CurrentTick + 100_000);
        }

        Assert.Equal(Signal.Low, qa.Value);          // 2 toggles -> back to 0
        Assert.Equal(3, ReadDiv5(qb, qc, qd));       // 3 falling edges on CKB
    }

    [Fact]
    public void Master_reset_clears_both_sections_immediately()
    {
        Net cka = new(1), ckb = new(2), mr = new(3);
        Net qa = new(4), qb = new(5), qc = new(6), qd = new(7);

        Hc390Half cnt = new(cka, ckb, mr, qa, qb, qc, qd);
        ManualClock ckaSrc = new(cka);
        ManualClock ckbSrc = new(ckb);
        ManualClock mrSrc = new(mr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, ckaSrc, ckbSrc, mrSrc });

        sim.Start();
        sim.RunUntilQuiescent();

        // QA -> HIGH (one pulse).
        ckaSrc.SetHigh(sim);
        ckaSrc.SetLow(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        // div5 -> 3 (three pulses).
        for (int i = 0; i < 3; i++)
        {
            ckbSrc.SetHigh(sim);
            ckbSrc.SetLow(sim);
            sim.RunUntil(sim.CurrentTick + 100_000);
        }
        Assert.Equal(Signal.High, qa.Value);
        Assert.Equal(3, ReadDiv5(qb, qc, qd));

        // Assert MR.
        mrSrc.SetHigh(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.Low, qa.Value);
        Assert.Equal(0, ReadDiv5(qb, qc, qd));
    }

    [Fact]
    public void BCD_div10_wiring_produces_0_through_9()
    {
        // Standard 74LS390 BCD-decade wiring: input on CKA, QA tied to CKB.
        // QA becomes Q0 (bit 0), QB..QD become Q1..Q3.
        Net clkIn = new(1), qaCkb = new(2), mr = new(3);
        Net qb = new(4), qc = new(5), qd = new(6);

        // qa AND ckb share the same net (qaCkb).
        Hc390Half cnt = new(cka: clkIn, ckb: qaCkb, mr: mr,
                            qa: qaCkb, qb: qb, qc: qc, qd: qd);
        ManualClock src = new(clkIn);
        GndDriver mrLow = new(mr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, src, mrLow });

        sim.Start();
        sim.RunUntilQuiescent();

        // Pulse 11 times and check the count rolls 1..9, then 0, then 1.
        int[] expected = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 1 };
        for (int i = 0; i < expected.Length; i++)
        {
            src.SetHigh(sim);
            src.SetLow(sim);
            sim.RunUntil(sim.CurrentTick + 100_000);

            int q0 = qaCkb.Value == Signal.High ? 1 : 0;
            int q1 = qb.Value == Signal.High ? 1 : 0;
            int q2 = qc.Value == Signal.High ? 1 : 0;
            int q3 = qd.Value == Signal.High ? 1 : 0;
            int actual = q0 | (q1 << 1) | (q2 << 2) | (q3 << 3);
            Assert.Equal(expected[i], actual);
        }
    }

    private static int ReadDiv5(Net qb, Net qc, Net qd)
    {
        int v = 0;
        if (qb.Value == Signal.High) v |= 1;
        if (qc.Value == Signal.High) v |= 2;
        if (qd.Value == Signal.High) v |= 4;
        return v;
    }
}