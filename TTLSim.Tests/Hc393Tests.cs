using TTLSim.Chips.Counters;
using TTLSim.Chips.Gates;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class Hc393Tests
{
    /// <summary>
    /// Drive a counter from a programmable test source and pulse it manually.
    /// Source schedules High at t=0 then Low at t=50000; the falling edge
    /// at t=50000 is what we expect to clock the counter.
    /// </summary>
    [Fact]
    public void Counter_increments_on_clock_falling_edge()
    {
        Net clk = new(1), clr = new(2);
        Net q0 = new(3), q1 = new(4), q2 = new(5), q3 = new(6);

        Hc393Half cnt = new(clk, clr, q0, q1, q2, q3);
        ManualClock src = new(clk);
        GndDriver clrLow = new(clr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, src, clrLow });

        sim.Start();
        sim.RunUntilQuiescent();
        // After Start, counter is at 0000.
        Assert.Equal(Signal.Low, q0.Value);
        Assert.Equal(Signal.Low, q3.Value);

        // First rising edge of CLK -> no count.
        src.SetHigh(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.Low, q0.Value);

        // First falling edge -> count = 1 (Q0 high).
        src.SetLow(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.High, q0.Value);

        // 2nd rise (no-op), 2nd fall -> count = 2 (Q1 high, Q0 low).
        src.SetHigh(sim);
        src.SetLow(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.Low, q0.Value);
        Assert.Equal(Signal.High, q1.Value);
    }

    [Fact]
    public void Async_clear_zeroes_all_outputs_immediately()
    {
        Net clk = new(1), clr = new(2);
        Net q0 = new(3), q1 = new(4), q2 = new(5), q3 = new(6);

        Hc393Half cnt = new(clk, clr, q0, q1, q2, q3);
        ManualClock src = new(clk);
        ManualClock clrSrc = new(clr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, src, clrSrc });

        sim.Start();
        sim.RunUntilQuiescent();

        // Clock the counter up to 3 (Q0 high, Q1 high).
        for (int i = 0; i < 3; i++)
        {
            src.SetHigh(sim);
            src.SetLow(sim);
            sim.RunUntil(sim.CurrentTick + 100_000);
        }
        Assert.Equal(Signal.High, q0.Value);
        Assert.Equal(Signal.High, q1.Value);

        // Assert async clear.
        clrSrc.SetHigh(sim);
        sim.RunUntil(sim.CurrentTick + 100_000);
        Assert.Equal(Signal.Low, q0.Value);
        Assert.Equal(Signal.Low, q1.Value);
        Assert.Equal(Signal.Low, q2.Value);
        Assert.Equal(Signal.Low, q3.Value);
    }

    [Fact]
    public void Counts_through_all_sixteen_states()
    {
        Net clk = new(1), clr = new(2);
        Net q0 = new(3), q1 = new(4), q2 = new(5), q3 = new(6);

        Hc393Half cnt = new(clk, clr, q0, q1, q2, q3);
        ManualClock src = new(clk);
        GndDriver clrLow = new(clr);

        Simulator sim = new(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            new IChip[] { cnt, src, clrLow });

        sim.Start();
        sim.RunUntilQuiescent();

        // Pulse 16 times and check the count rolls over to 0.
        for (int i = 1; i <= 16; i++)
        {
            src.SetHigh(sim);
            src.SetLow(sim);
            sim.RunUntil(sim.CurrentTick + 100_000);

            int expected = i & 0xF;
            int actual = ReadCount(q0, q1, q2, q3);
            Assert.Equal(expected, actual);
        }
    }

    private static int ReadCount(Net q0, Net q1, Net q2, Net q3)
    {
        int v = 0;
        if (q0.Value == Signal.High) v |= 1;
        if (q1.Value == Signal.High) v |= 2;
        if (q2.Value == Signal.High) v |= 4;
        if (q3.Value == Signal.High) v |= 8;
        return v;
    }
}

/// <summary>
/// A clock net we drive manually via SetHigh/SetLow. Lets us pulse the
/// counter from a test without a free-running ClockSource.
/// </summary>
internal sealed class ManualClock : IChip
{
    private readonly Net net;
    private readonly Driver driver;

    public ManualClock(Net net)
    {
        this.net = net;
        driver = new Driver(net, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers => new[] { 1 };
    public IReadOnlyList<Net> Nets => new[] { net };

    public void Initialize(IScheduler scheduler) => scheduler.Schedule(0, driver, Signal.Low);
    public void OnInputChanged(int pinIndex, IScheduler scheduler) { }

    public void SetHigh(Simulator sim) => ((IScheduler)sim).Schedule(0, driver, Signal.High);
    public void SetLow(Simulator sim) => ((IScheduler)sim).Schedule(0, driver, Signal.Low);
}