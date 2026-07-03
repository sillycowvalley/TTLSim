using TTLSim.Chips.Passives;
using TTLSim.Chips.Registers;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Tests for the 74HC374 octal D register model. The chip is edge-triggered,
/// so the rig drives CLK with a small scripted source (a scheduled Low/High
/// sequence) rather than a free-running clock; D inputs are static Vcc/Gnd
/// drivers. Tri-state is observed through a weak pull-up on a Q net: with
/// /OE low the chip's strong driver wins; with /OE high the chip releases
/// and the pull wins.
/// </summary>
public class Hc374Tests
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
    /// Build a '374 with D driven to the given byte and CLK driven through
    /// the given (tick, level) sequence. oeHigh selects /OE. pullUpQ0 adds a
    /// weak pull-up on Q0 so tri-state release is observable.
    /// </summary>
    private static Rig Build(int data, bool oeHigh, (long Tick, Signal Level)[] clkSequence,
        bool pullUpQ0 = false)
    {
        int id = 0;
        Net N() => new(id++);

        Net oeN = N(), clk = N();
        Net[] d = new Net[8];
        Net[] q = new Net[8];
        for (int i = 0; i < 8; i++) { d[i] = N(); q[i] = N(); }

        List<IChip> chips = new()
        {
            oeHigh ? new VccDriver(oeN) : new GndDriver(oeN),
            new SequenceSource(clk, clkSequence),
            new Hc374(
                oeN: oeN, clkN: clk,
                d0: d[0], d1: d[1], d2: d[2], d3: d[3],
                d4: d[4], d5: d[5], d6: d[6], d7: d[7],
                q0: q[0], q1: q[1], q2: q[2], q3: q[3],
                q4: q[4], q5: q[5], q6: q[6], q7: q[7])
        };
        for (int i = 0; i < 8; i++)
            chips.Add(((data >> i) & 1) != 0 ? (IChip)new VccDriver(d[i]) : new GndDriver(d[i]));

        if (pullUpQ0)
            chips.Add(new PullDriver(q[0], Signal.High));

        Rig r = new() { Q = q };
        r.Sim = new Simulator(
            NetTable.Build(Array.Empty<(PinRef, PinRef)>()),
            chips);
        r.Sim.Start();
        r.Sim.RunUntilQuiescent();
        return r;
    }

    // One rising edge, comfortably after power-up settles.
    private static (long, Signal)[] OneRisingEdge() =>
        new[] { (0L, Signal.Low), (100_000L, Signal.High) };

    // Clock held low throughout -- no edge ever.
    private static (long, Signal)[] NoEdge() =>
        new[] { (0L, Signal.Low) };

    // High from the start, then falling -- never a Low -> High transition.
    private static (long, Signal)[] FallingEdgeOnly() =>
        new[] { (0L, Signal.High), (100_000L, Signal.Low) };

    [Fact]
    public void Rising_edge_latches_data_to_outputs()
    {
        var r = Build(data: 0xA5, oeHigh: false, OneRisingEdge());
        Assert.Equal(0xA5, r.ReadQ());
    }

    [Fact]
    public void Without_an_edge_outputs_hold_the_powerup_zero()
    {
        var r = Build(data: 0xFF, oeHigh: false, NoEdge());
        Assert.Equal(0x00, r.ReadQ());
    }

    [Fact]
    public void Falling_edge_does_not_latch()
    {
        var r = Build(data: 0xFF, oeHigh: false, FallingEdgeOnly());
        Assert.Equal(0x00, r.ReadQ());
    }

    [Fact]
    public void Oe_high_releases_outputs_and_a_weak_pull_wins()
    {
        // Latched bit 0 is LOW (data 0x00) -- with /OE low the chip's strong
        // Low would beat the pull-up. With /OE high the chip releases and
        // the weak pull-up defines the net as High.
        var r = Build(data: 0x00, oeHigh: true, OneRisingEdge(), pullUpQ0: true);
        Assert.Equal(Signal.High, r.Q[0].Value);
    }

    [Fact]
    public void Oe_low_drives_outputs_over_a_weak_pull()
    {
        var r = Build(data: 0x00, oeHigh: false, OneRisingEdge(), pullUpQ0: true);
        Assert.Equal(Signal.Low, r.Q[0].Value);
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