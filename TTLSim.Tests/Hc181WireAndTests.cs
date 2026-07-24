using TTLSim.Chips.Alu;
using TTLSim.Chips.Sources;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Open-collector behaviour at the resolver level: the change-request's net
/// resolution table, row by row, expressed against Net.Resolve and
/// Net.DetectFault directly. No new drive type exists -- an OC pin is a
/// Strong driver that outputs Low when asserting and HighZ when releasing
/// (the Hc125 release idiom applied per-value), and the existing strength
/// tiers produce every row of the table unchanged. These tests pin that
/// down so a future resolver change cannot silently break the wire-AND.
/// </summary>
public class OpenCollectorResolutionTests
{
    private static Driver Oc(Net n, bool asserting) =>
        new(n, DriveStrength.Strong) { Output = asserting ? Signal.Low : Signal.HighZ };

    private static Driver Weak(Net n, Signal s) =>
        new(n, DriveStrength.Weak) { Output = s };

    private static Driver TotemPole(Net n, Signal s) =>
        new(n, DriveStrength.Strong) { Output = s };

    [Fact]
    public void Any_oc_pin_low_wins_regardless_of_pullup()
    {
        Net n = new(1);
        Oc(n, asserting: true);
        Oc(n, asserting: false);
        Weak(n, Signal.High);

        Assert.Equal(Signal.Low, n.Resolve());
        Assert.Null(n.DetectFault());
    }

    [Fact]
    public void All_released_with_pullup_reads_weak_high()
    {
        Net n = new(1);
        Oc(n, asserting: false);
        Oc(n, asserting: false);
        Weak(n, Signal.High);

        Signal value = n.Resolve(out DriveStrength strength);
        Assert.Equal(Signal.High, value);
        Assert.Equal(DriveStrength.Weak, strength);
        Assert.Null(n.DetectFault());
    }

    [Fact]
    public void All_released_with_pulldown_reads_weak_low()
    {
        Net n = new(1);
        Oc(n, asserting: false);
        Weak(n, Signal.Low);

        Assert.Equal(Signal.Low, n.Resolve());
        Assert.Null(n.DetectFault());
    }

    [Fact]
    public void All_released_with_no_resistor_is_highz_not_high()
    {
        // The CR's "must not silently read high" row. Nothing contributes,
        // so Resolve reports HighZ; the net's displayed Value then stays at
        // its initial Unknown under the engine's no-op optimisation (the
        // behaviour DiodeContactTests documents), which is visibly wrong on
        // the canvas rather than a plausible-looking High.
        Net n = new(1);
        Oc(n, asserting: false);
        Oc(n, asserting: false);

        Assert.Equal(Signal.HighZ, n.Resolve());
        Assert.Null(n.DetectFault());
    }

    [Fact]
    public void Multiple_oc_pins_low_together_are_legal()
    {
        // The point of the wire-AND: same tier, same level, no conflict.
        Net n = new(1);
        Oc(n, asserting: true);
        Oc(n, asserting: true);
        Weak(n, Signal.High);

        Assert.Equal(Signal.Low, n.Resolve());
        Assert.Null(n.DetectFault());
    }

    [Fact]
    public void Oc_low_against_totem_pole_high_is_a_fault()
    {
        // The one genuinely illegal combination: an asserting OC pin sinks
        // against a push-pull stage sourcing. Same tier, opposite levels.
        Net n = new(1);
        Oc(n, asserting: true);
        TotemPole(n, Signal.High);

        Assert.Equal(Signal.Unknown, n.Resolve());

        NetFault? f = n.DetectFault();
        Assert.NotNull(f);
        Assert.Equal(DriveStrength.Strong, f!.Strength);
    }
}

/// <summary>
/// The change-request's integration case built directly in code: two '181
/// slices with their open-collector A=B pins wire-ANDed on one net under a
/// weak pull-up, forming an 8-bit equality signal. Each slice runs
/// S=0110 (subtract) with carry asserted, so its slice result is A minus B
/// and the A=B pin releases exactly on nibble equality. The rig mirrors
/// Hc181Tests' conventions (active-low operand pins, StaticWeakDriver
/// pull-up) so the two files read alike.
/// </summary>
public class Hc181WireAndTests
{
    [Theory]
    // Both nibbles equal: both slices release, the pull-up owns the net.
    [InlineData(0x5A, 0x5A, true)]
    // Low nibbles equal, high nibbles differ: the low slice releases but the
    // high slice pulls the shared net down. This is the vector that proves
    // the wire-AND rather than a single slice.
    [InlineData(0x0F, 0x1F, false)]
    // Low nibbles differ.
    [InlineData(0x5A, 0x5B, false)]
    // Both slices differ: two OC pins asserting together -- legal, still low.
    [InlineData(0x00, 0x11, false)]
    public void Wire_anded_AeqB_reads_high_only_on_full_equality(
        int a, int b, bool expectEqual)
    {
        (Net aeqb, Simulator sim) = BuildTwoSlices(a, b);
        sim.RunUntilQuiescent();

        Assert.Equal(expectEqual ? Signal.High : Signal.Low, aeqb.Value);
        // Legal in every vector: released pins and agreeing lows never fault.
        Assert.Null(aeqb.DetectFault());
    }

    [Fact]
    public void Shared_AeqB_idles_at_a_defined_level()
    {
        // CR test 7: whenever both slices release, the pull-up owns the net,
        // so the idle level is a defined High -- never a float.
        (Net aeqb, Simulator sim) = BuildTwoSlices(0x33, 0x33);
        sim.RunUntilQuiescent();

        Assert.Equal(Signal.High, aeqb.Value);
        Signal value = aeqb.Resolve(out DriveStrength strength);
        Assert.Equal(Signal.High, value);
        Assert.Equal(DriveStrength.Weak, strength);
    }

    // ------------------------------------------------------------------ rig

    /// <summary>
    /// Two '181s: the low slice on bits 3..0, the high slice on bits 7..4,
    /// A=B pins on ONE shared net with a weak pull-up. Every other output
    /// gets its own private net. S=0110, M low, Cn low (carry asserted), so
    /// per slice F = A - B and A=B releases on nibble equality.
    /// </summary>
    private static (Net AeqB, Simulator Sim) BuildTwoSlices(int a, int b)
    {
        int id = 0;
        Net N() => new(id++);

        Net aeqb = N();
        List<IChip> chips = new();

        foreach (int shift in new[] { 0, 4 })
        {
            int an = (a >> shift) & 0xF;
            int bn = (b >> shift) & 0xF;

            Net a0 = N(), a1 = N(), a2 = N(), a3 = N();
            Net b0 = N(), b1 = N(), b2 = N(), b3 = N();
            Net s0 = N(), s1 = N(), s2 = N(), s3 = N();
            Net m = N(), cn = N();

            chips.Add(new Hc181(
                b0: b0, a0: a0,
                s3: s3, s2: s2, s1: s1, s0: s0,
                cn: cn, m: m,
                f0: N(), f1: N(), f2: N(), f3: N(),
                aeqb: aeqb,
                y: N(), x: N(),
                cnP4: N(),
                b3: b3, a3: a3, b2: b2, a2: a2, b1: b1, a1: a1));

            // Active-low operand pins: a 1 bit drives the pin LOW.
            DriveActiveLow(chips, a0, an, 0);
            DriveActiveLow(chips, a1, an, 1);
            DriveActiveLow(chips, a2, an, 2);
            DriveActiveLow(chips, a3, an, 3);
            DriveActiveLow(chips, b0, bn, 0);
            DriveActiveLow(chips, b1, bn, 1);
            DriveActiveLow(chips, b2, bn, 2);
            DriveActiveLow(chips, b3, bn, 3);

            // S=0110 subtract: S1, S2 high; S0, S3 low. M low (arithmetic).
            chips.Add(new GndDriver(s0));
            chips.Add(new VccDriver(s1));
            chips.Add(new VccDriver(s2));
            chips.Add(new GndDriver(s3));
            chips.Add(new GndDriver(m));

            // Cn LOW asserts carry, making the slice result A - B, so the
            // release condition is plain nibble equality. Each slice gets
            // its own Cn drive -- the equality tie needs no carry chain.
            chips.Add(new GndDriver(cn));
        }

        // The pull-up resistor: a weak High on the shared net, as in the
        // real module (RAEQ to VCC).
        Driver pullup = new(aeqb, DriveStrength.Weak);
        chips.Add(new StaticDriver(pullup, Signal.High));

        Simulator sim = new(
            NetTable.Build(System.Array.Empty<(PinRef, PinRef)>()),
            chips.ToArray());
        sim.Start();
        return (aeqb, sim);
    }

    private static void DriveActiveLow(List<IChip> chips, Net net, int value, int bit)
    {
        bool asserted = ((value >> bit) & 1) != 0;
        chips.Add(asserted ? (IChip)new GndDriver(net) : new VccDriver(net));
    }

    /// <summary>Schedules one fixed value onto a pre-built Driver at
    /// Initialize -- the pull-up stand-in, as in Hc181Tests.</summary>
    private sealed class StaticDriver : IChip
    {
        private readonly Driver driver;
        private readonly Signal value;

        public StaticDriver(Driver driver, Signal value)
        {
            this.driver = driver;
            this.value = value;
        }

        public IReadOnlyList<int> PinNumbers { get; } = System.Array.Empty<int>();
        public IReadOnlyList<Net> Nets { get; } = System.Array.Empty<Net>();
        public void Initialize(IScheduler scheduler) => scheduler.Schedule(0, driver, value);
        public void OnInputChanged(int pinIndex, IScheduler scheduler) { }
    }
}

/// <summary>
/// Build-time side of the change: with the '181 removed from the
/// TotemPoleParts whitelist, two A=B pins tied together must no longer
/// raise TTL005 -- the legal wire-AND builds clean and contention policing
/// falls to the runtime detector, exactly as it already does for every
/// tri-state part. The two-totem-pole regression (TTL005 still firing for
/// e.g. '00 vs '273) lives in ElectricalScanTests and is unchanged.
/// </summary>
public class Hc181Ttl005Tests
{
    [Fact]
    public void Two_181_AeqB_outputs_on_one_net_is_not_TTL005()
    {
        WireAndInput input = new();
        input.AddDevice("d1", "U1", "181",
            new BuildUnit("u1", '\0', new[] { 1, 2 }, null,
                OutputPinNumbers: new[] { 14 }));
        input.AddDevice("d2", "U2", "181",
            new BuildUnit("u2", '\0', new[] { 1, 2 }, null,
                OutputPinNumbers: new[] { 14 }));

        input.Connect("u1", 14, "u2", 14);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL005");
    }

    [Fact]
    public void The_181_is_not_classified_totem_pole()
    {
        // The whitelist is the single input to the static check; pin this
        // directly so the wire-AND cannot be re-broken by someone "fixing"
        // the apparently missing arithmetic entry.
        Assert.False(TotemPoleParts.IsTotemPole("181"));
        // Its neighbours stay in.
        Assert.True(TotemPoleParts.IsTotemPole("182"));
        Assert.True(TotemPoleParts.IsTotemPole("283"));
        Assert.True(TotemPoleParts.IsTotemPole("688"));
    }

    /// <summary>Minimal IBuildInput, private to this file so it cannot
    /// entangle with the fakes other test files declare.</summary>
    private sealed class WireAndInput : IBuildInput
    {
        private readonly List<BuildDevice> devices = new();
        private readonly List<(PinRef, PinRef)> conns = new();

        public IEnumerable<BuildDevice> Devices => devices;
        public IEnumerable<BuildItem> Items => System.Array.Empty<BuildItem>();
        public IEnumerable<(PinRef A, PinRef B)> Connections => conns;

        public void AddDevice(string id, string designator, string partId,
            params BuildUnit[] units) =>
            devices.Add(new BuildDevice(id, designator, partId, "HC", null, null, units));

        public void Connect(string itemA, int pinA, string itemB, int pinB) =>
            conns.Add((new PinRef(itemA, pinA), new PinRef(itemB, pinB)));
    }
}
