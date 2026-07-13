using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// Two halves of the same bug, from the blinky clock module: a '273 wired with
/// D and Q transposed on adjacent pins. The Q outputs land on the '00 latch's
/// outputs (a dead short), and the D inputs land on the '00 gating inputs (a
/// net nothing drives). Both were invisible -- the build reported zero errors
/// and the contended nets never logged a single transition, because a shorted
/// net resolves to Unknown and Unknown is also the initial value.
/// </summary>
public class NetFaultTests
{
    private static Driver Strong(Net n, Signal s) =>
        new(n, DriveStrength.Strong) { Output = s };

    private static Driver Weak(Net n, Signal s) =>
        new(n, DriveStrength.Weak) { Output = s };

    private static Driver Medium(Net n, Signal s) =>
        new(n, DriveStrength.Medium) { Output = s };

    [Fact]
    public void Healthy_net_has_no_fault()
    {
        Net n = new(1);
        Strong(n, Signal.High);
        Strong(n, Signal.High);   // two outputs agreeing is still a short, but
        Assert.Null(n.DetectFault());   // not one we can *detect* electrically
    }

    [Fact]
    public void Opposing_strong_drivers_are_a_fault()
    {
        Net n = new(1);
        Strong(n, Signal.High);
        Strong(n, Signal.Low);

        NetFault? f = n.DetectFault();

        Assert.NotNull(f);
        Assert.Equal(DriveStrength.Strong, f!.Strength);
        Assert.Equal(1, f.HighDrivers);
        Assert.Equal(1, f.LowDrivers);
        Assert.Equal(Signal.Unknown, n.Resolve());   // and Resolve agrees
    }

    [Fact]
    public void Strong_driver_beating_an_opposing_pullup_is_not_a_fault()
    {
        // The entire point of a pull-up. The strong tier decides; the weak
        // tier's disagreement with it is electrically irrelevant.
        Net n = new(1);
        Weak(n, Signal.High);
        Strong(n, Signal.Low);

        Assert.Null(n.DetectFault());
        Assert.Equal(Signal.Low, n.Resolve());
    }

    [Fact]
    public void Opposing_pulls_with_nothing_strong_are_a_fault()
    {
        Net n = new(1);
        Weak(n, Signal.High);
        Weak(n, Signal.Low);

        NetFault? f = n.DetectFault();

        Assert.NotNull(f);
        Assert.Equal(DriveStrength.Weak, f!.Strength);
    }

    [Fact]
    public void Opposing_diodes_fault_at_the_medium_tier()
    {
        Net n = new(1);
        Medium(n, Signal.High);
        Medium(n, Signal.Low);

        Assert.Equal(DriveStrength.Medium, n.DetectFault()!.Strength);
    }

    [Fact]
    public void Highz_drivers_never_contend()
    {
        // A tri-state bus at rest: one part driving, everyone else off.
        Net n = new(1);
        Strong(n, Signal.HighZ);
        Strong(n, Signal.HighZ);
        Strong(n, Signal.Low);

        Assert.Null(n.DetectFault());
    }

    [Fact]
    public void An_unknown_output_is_not_a_conflicting_level()
    {
        // A chip whose own inputs haven't resolved drives Unknown. That means
        // "I don't know", not "I'm pulling the other way" -- counting it as a
        // conflict would fire a spurious fault on every bus at power-up.
        Net n = new(1);
        Strong(n, Signal.Unknown);
        Strong(n, Signal.High);

        Assert.Null(n.DetectFault());
    }
}

/// <summary>
/// Build-time counterparts: TTL005 (two always-driving outputs on one net) and
/// TTL013 (a net made entirely of logic inputs).
/// </summary>
public class ElectricalScanTests
{
    [Fact]
    public void Two_totem_pole_outputs_on_one_net_is_TTL005()
    {
        // A '00 gate output and a '273 Q output on the same wire -- the exact
        // shape of the clock-module bug.
        ContentionInput input = new();
        input.AddDevice("d1", "U1", "00",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        input.AddDevice("d2", "U11", "273",
            new BuildUnit("u11", '\0', new[] { 1, 11, 13 }, null,
                OutputPinNumbers: new[] { 12 }));

        input.Connect("u1a", 3, "u11", 12);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error && d.Code == "TTL005");
    }

    [Fact]
    public void Tri_state_parts_sharing_a_net_is_not_TTL005()
    {
        // Two '245 transceivers on one bus. Legal, common, and must not error.
        ContentionInput input = new();
        input.AddDevice("d1", "U1", "245",
            new BuildUnit("u1", '\0', new[] { 1, 19 }, null,
                OutputPinNumbers: new[] { 2 }));
        input.AddDevice("d2", "U2", "245",
            new BuildUnit("u2", '\0', new[] { 1, 19 }, null,
                OutputPinNumbers: new[] { 2 }));

        input.Connect("u1", 2, "u2", 2);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL005");
    }

    [Fact]
    public void Contention_can_be_downgraded_to_a_warning()
    {
        ContentionInput input = new();
        input.AddDevice("d1", "U1", "00",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        input.AddDevice("d2", "U2", "00",
            new BuildUnit("u2a", 'a', new[] { 4, 5 }, OutputPinNumber: 6));

        input.Connect("u1a", 3, "u2a", 6);

        BuildResult result = new SchematicBuilder(contentionIsWarning: true).Build(input);

        Assert.Contains(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Code == "TTL005");
    }

    [Fact]
    public void Input_wired_only_to_another_input_is_TTL013()
    {
        // '273 D4 (pin 13) wired to a '00 gate input. Both are inputs, so the
        // net is "connected" -- TTL011 sees nothing -- yet nothing drives it.
        ContentionInput input = new();
        input.AddDevice("d1", "U11", "273",
            new BuildUnit("u11", '\0', new[] { 13 }, null,
                OutputPinNumbers: new[] { 12 }));
        input.AddDevice("d2", "U12", "00",
            new BuildUnit("u12a", 'a', new[] { 4, 5 }, OutputPinNumber: 6));

        input.Connect("u11", 13, "u12a", 5);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.Contains(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Code == "TTL013");
    }

    [Fact]
    public void An_input_net_with_a_passive_on_it_is_not_TTL013()
    {
        // A pull-up feeding a gate input: the resistor terminal is listed as an
        // "input" by the build adapter, but PullDriver very much drives it.
        ContentionInput input = new();
        input.AddDevice("d1", "U1", "00",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        input.AddPassive("d2", "R1", "resistor",
            new BuildUnit("r1", '\0', new[] { 1, 2 }, null));

        input.Connect("r1", 2, "u1a", 1);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL013");
    }

    [Fact]
    public void A_driven_input_net_is_not_TTL013()
    {
        ContentionInput input = new();
        input.AddDevice("d1", "U1", "00",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        input.AddDevice("d2", "U2", "00",
            new BuildUnit("u2a", 'a', new[] { 4, 5 }, OutputPinNumber: 6));

        input.Connect("u1a", 3, "u2a", 4);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL013");
    }
}

/// <summary>
/// Minimal IBuildInput for the electrical scan, with an IsPassive flag the
/// shared FakeInput doesn't expose. No WinForms anywhere.
/// </summary>
internal sealed class ContentionInput : IBuildInput
{
    private readonly List<BuildDevice> devices = new();
    private readonly List<BuildItem> items = new();
    private readonly List<(PinRef, PinRef)> conns = new();

    public IEnumerable<BuildDevice> Devices => devices;
    public IEnumerable<BuildItem> Items => items;
    public IEnumerable<(PinRef A, PinRef B)> Connections => conns;

    public void AddDevice(string id, string designator, string partId, params BuildUnit[] units) =>
        devices.Add(new BuildDevice(id, designator, partId, "HC", null, null, units));

    public void AddPassive(string id, string designator, string partId, params BuildUnit[] units) =>
        devices.Add(new BuildDevice(id, designator, partId, null, null, null, units,
            IsPassive: true));

    public void Connect(string itemA, int pinA, string itemB, int pinB) =>
        conns.Add((new PinRef(itemA, pinA), new PinRef(itemB, pinB)));
}