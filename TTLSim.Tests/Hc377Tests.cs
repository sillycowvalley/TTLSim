using TTLSim.Chips.Registers;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74HC377 octal D register model. Edge-triggered with a
/// synchronous /EN, so the rig drives CLK (and, in one test, /EN) with a
/// small scripted source; D inputs are static Vcc/Gnd drivers.
/// </summary>
public class Hc377Tests
{
    private sealed class Rig
    {
        public Net[] Q = null!;
        public Simulator Sim = null!;

        public int ReadQ()
        {
            int v = 0;
            for (int i = 0; i < 8; i++)
                if (Q[i].Value == Signal.High)
                    v |= 1 << i;
            return v;
        }
    }

    /// <summary>
    /// Build a '377 with D driven to the given byte, /EN and CLK each driven
    /// through their own (tick, level) sequences.
    /// </summary>
    private static Rig Build(int data,
        (long Tick, Signal Level)[] enSequence,
        (long Tick, Signal Level)[] clkSequence)
    {
        int id = 0;
        Net N() => new(id++);

        Net enN = N(), clk = N();
        Net[] d = new Net[8];
        Net[] q = new Net[8];
        for (int i = 0; i < 8; i++) { d[i] = N(); q[i] = N(); }

        List<IChip> chips = new()
        {
            new SequenceSource(enN, enSequence),
            new SequenceSource(clk, clkSequence),
            new Hc377(
                enN: enN, clkN: clk,
                d0: d[0], d1: d[1], d2: d[2], d3: d[3],
                d4: d[4], d5: d[5], d6: d[6], d7: d[7],
                q0: q[0], q1: q[1], q2: q[2], q3: q[3],
                q4: q[4], q5: q[5], q6: q[6], q7: q[7])
        };
        for (int i = 0; i < 8; i++)
            chips.Add(((data >> i) & 1) != 0 ? (IChip)new VccDriver(d[i]) : new GndDriver(d[i]));

        Rig r = new() { Q = q };
        r.Sim = new Simulator(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            chips);
        r.Sim.Start();
        r.Sim.RunUntilQuiescent();
        return r;
    }

    private static (long, Signal)[] Level(Signal s) =>
        new[] { (0L, s) };

    // One rising edge, comfortably after power-up settles.
    private static (long, Signal)[] OneRisingEdge() =>
        new[] { (0L, Signal.Low), (100_000L, Signal.High) };

    // Two rising edges: at 100 ns and at 300 ns.
    private static (long, Signal)[] TwoRisingEdges() =>
        new[] { (0L, Signal.Low), (100_000L, Signal.High),
                (200_000L, Signal.Low), (300_000L, Signal.High) };

    [Fact]
    public void Enabled_edge_latches_data()
    {
        var r = Build(data: 0xA5, Level(Signal.Low), OneRisingEdge());
        Assert.Equal(0xA5, r.ReadQ());
    }

    [Fact]
    public void Disabled_edge_holds()
    {
        var r = Build(data: 0xA5, Level(Signal.High), OneRisingEdge());
        Assert.Equal(0x00, r.ReadQ());
    }

    [Fact]
    public void Falling_edge_does_not_latch()
    {
        var r = Build(data: 0xFF, Level(Signal.Low),
            new[] { (0L, Signal.High), (100_000L, Signal.Low) });
        Assert.Equal(0x00, r.ReadQ());
    }

    [Fact]
    public void Enable_is_sampled_per_edge()
    {
        // /EN low for the first edge (loads 0xC3), then high before the
        // second edge (holds despite the edge). The register keeps the
        // first value.
        var r = Build(data: 0xC3,
            enSequence: new[] { (0L, Signal.Low), (200_000L, Signal.High) },
            clkSequence: TwoRisingEdges());
        Assert.Equal(0xC3, r.ReadQ());
    }

    [Fact]
    public void Enable_raised_before_first_edge_blocks_it()
    {
        // /EN starts low but goes high before the only edge -- nothing loads.
        var r = Build(data: 0xC3,
            enSequence: new[] { (0L, Signal.Low), (50_000L, Signal.High) },
            clkSequence: OneRisingEdge());
        Assert.Equal(0x00, r.ReadQ());
    }

    /// <summary>
    /// Drives one net through a scripted (tick, level) sequence, scheduled
    /// once at Initialize. Ticks are absolute from simulation start.
    /// </summary>
    private sealed class SequenceSource : IChip
    {
        private readonly Net net;
        private readonly Driver driver;
        private readonly (long Tick, Signal Level)[] sequence;

        public SequenceSource(Net net, (long Tick, Signal Level)[] sequence)
        {
            this.net = net;
            this.sequence = sequence;
            driver = new Driver(net, DriveStrength.Strong);
        }

        public IReadOnlyList<int> PinNumbers { get; } = Array.Empty<int>();
        public IReadOnlyList<Net> Nets { get; } = Array.Empty<Net>();

        public void Initialize(IScheduler scheduler)
        {
            foreach (var (tick, level) in sequence)
                scheduler.Schedule(tick, driver, level);
        }

        public void OnInputChanged(int pinIndex, IScheduler scheduler) { }
    }
}