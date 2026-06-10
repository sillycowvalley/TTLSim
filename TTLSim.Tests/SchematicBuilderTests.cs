using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

public class SchematicBuilderTests
{
    [Fact]
    public void Empty_input_builds_with_no_diagnostics()
    {
        FakeInput input = new();
        BuildResult result = new SchematicBuilder().Build(input);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.NetTable);
        Assert.Empty(result.NetTable!.Nets);
    }

    [Fact]
    public void Connections_become_nets()
    {
        FakeInput input = new();
        input.Connect("U1", 1, "U2", 2);
        input.Connect("U2", 2, "U3", 3);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.True(result.Succeeded);
        Assert.Single(result.NetTable!.Nets);
    }

    [Fact]
    public void Vcc_to_gnd_short_is_an_error()
    {
        FakeInput input = new();
        input.AddItem("VCC1", BuildItemKind.Vcc, pinNumbers: new[] { 0 });
        input.AddItem("GND1", BuildItemKind.Gnd, pinNumbers: new[] { 0 });
        input.Connect("VCC1", 0, "GND1", 0);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error && d.Code == "TTL001");
    }

    [Fact]
    public void Vcc_and_gnd_on_separate_nets_is_fine()
    {
        FakeInput input = new();
        input.AddItem("VCC1", BuildItemKind.Vcc, new[] { 0 });
        input.AddItem("GND1", BuildItemKind.Gnd, new[] { 0 });
        input.Connect("VCC1", 0, "U1", 14);
        input.Connect("GND1", 0, "U1", 7);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Completely_unconnected_unit_emits_warning()
    {
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "08",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        // No connections at all.

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Code == "TTL010");
    }

    [Fact]
    public void Unit_with_at_least_one_connected_pin_is_not_flagged()
    {
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "08",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        // Wire all three pins so neither TTL010, 011, nor 012 fires.
        input.Connect("u1a", 1, "other", 100);
        input.Connect("u1a", 2, "other", 101);
        input.Connect("u1a", 3, "other", 102);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "TTL010" or "TTL011" or "TTL012");
    }

    [Fact]
    public void One_floating_input_emits_TTL011()
    {
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "08",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        // Input pin 1 wired; pin 2 floating; output wired.
        input.Connect("u1a", 1, "other", 100);
        input.Connect("u1a", 3, "other", 101);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.Contains(result.Diagnostics,
            d => d.Code == "TTL011" && d.PinNumber == 2);
    }

    [Fact]
    public void Dangling_output_on_entirely_unused_quad_emits_TTL012()
    {
        // Two gates on the same device, both with inputs wired but outputs
        // floating. "No gate on the device drives anything" -> the entire IC
        // is wasted silicon, so TTL012 fires.
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "08",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3),
            new BuildUnit("u1b", 'b', new[] { 4, 5 }, OutputPinNumber: 6));
        input.Connect("u1a", 1, "other", 100);
        input.Connect("u1a", 2, "other", 101);
        input.Connect("u1b", 4, "other", 102);
        input.Connect("u1b", 5, "other", 103);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.Contains(result.Diagnostics, d => d.Code == "TTL012");
    }

    [Fact]
    public void Dangling_output_on_partially_used_quad_does_not_emit_TTL012()
    {
        // Partial use of a quad gate is normal: as long as at least one gate
        // on the device drives something, floating outputs on the other gates
        // are not flagged.
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "08",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3),
            new BuildUnit("u1b", 'b', new[] { 4, 5 }, OutputPinNumber: 6));
        // u1a is fully wired (drives a real load); u1b has inputs wired but
        // its output is left floating.
        input.Connect("u1a", 1, "other", 100);
        input.Connect("u1a", 2, "other", 101);
        input.Connect("u1a", 3, "other", 102);
        input.Connect("u1b", 4, "other", 103);
        input.Connect("u1b", 5, "other", 104);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL012");
    }

    [Fact]
    public void Fully_unused_unit_emits_only_TTL010_not_TTL011()
    {
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "08",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.Contains(result.Diagnostics, d => d.Code == "TTL010");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL011");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL012");
    }

    [Fact]
    public void Unsupported_part_emits_TTL020_error()
    {
        // The '181 is on the catalogue (so users can place it) but has no
        // chip model yet. Building a schematic containing one must surface
        // a TTL020 rather than quietly producing a partial simulator.
        //
        // The diagnostic's ItemId points at the device's first unit so the
        // OutputPanel's double-click locator (which resolves against the
        // schematic's item list) can pan and select it.
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "181",
            new BuildUnit("u1", '\0', new[] { 1, 2 }, OutputPinNumber: null));

        FakeChipFactory factory = new(unsupportedPartIds: new[] { "181" });
        BuildResult result = new SchematicBuilder(factory).Build(input);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                 && d.Code == "TTL020"
                 && d.ItemId == "u1");
        Assert.Null(result.Simulator);
    }

    [Fact]
    public void Unsupported_part_with_no_units_still_emits_TTL020()
    {
        // Defensive: a Device with zero units shouldn't crash the diagnostic.
        // ItemId is allowed to be null in that case; the user just won't get
        // the click-to-locate behaviour.
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "181");

        FakeChipFactory factory = new(unsupportedPartIds: new[] { "181" });
        BuildResult result = new SchematicBuilder(factory).Build(input);

        Assert.Contains(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                 && d.Code == "TTL020"
                 && d.ItemId == null);
    }

    [Fact]
    public void Supported_part_does_not_emit_TTL020()
    {
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "08",
            new BuildUnit("u1a", 'a', new[] { 1, 2 }, OutputPinNumber: 3));
        input.Connect("u1a", 1, "other", 100);
        input.Connect("u1a", 2, "other", 101);
        input.Connect("u1a", 3, "other", 102);

        FakeChipFactory factory = new(unsupportedPartIds: new[] { "181" });
        BuildResult result = new SchematicBuilder(factory).Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL020");
    }

    [Fact]
    public void Default_null_factory_does_not_emit_TTL020()
    {
        // SchematicBuilder() with no factory uses the internal NullChipFactory,
        // which has no opinion about simulation support -- so the existing
        // tests that don't supply a factory keep working.
        FakeInput input = new();
        input.AddDevice("dev1", "U1", "181");

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TTL020");
    }
}

/// <summary>
/// Test factory that produces no chips but can be configured to say
/// "I don't simulate this part" for specific part identifiers, so the
/// TTL020 diagnostic phase has something to react to.
/// </summary>
internal sealed class FakeChipFactory : IChipFactory
{
    private readonly HashSet<string> unsupported;

    public FakeChipFactory(IEnumerable<string> unsupportedPartIds)
    {
        unsupported = new HashSet<string>(unsupportedPartIds);
    }

    public IChip? CreateForDevice(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet) => null;
    public IChip? CreateForItem(BuildItem item, IReadOnlyDictionary<int, Net> pinToNet) => null;
    public IEnumerable<IChip> CreateForUnits(
        BuildDevice device,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, Net>> unitPinMaps,
        IReadOnlyDictionary<int, Signal> powerNets) => System.Array.Empty<IChip>();

    public bool IsSimulated(BuildDevice device) =>
        !unsupported.Contains(device.PartIdentifier);
}

/// <summary>Minimal hand-rolled IBuildInput for tests. No WinForms anywhere.</summary>
internal sealed class FakeInput : IBuildInput
{
    private readonly List<BuildDevice> devices = new();
    private readonly List<BuildItem> items = new();
    private readonly List<(PinRef, PinRef)> conns = new();

    public IEnumerable<BuildDevice> Devices => devices;
    public IEnumerable<BuildItem> Items => items;
    public IEnumerable<(PinRef A, PinRef B)> Connections => conns;

    public void AddDevice(string id, string designator, string partId, params BuildUnit[] units) =>
        devices.Add(new BuildDevice(id, designator, partId, "HC", null, null, units));

    public void AddItem(string id, BuildItemKind kind, int[] pinNumbers) =>
        items.Add(new BuildItem(id, kind, pinNumbers));

    public void Connect(string itemA, int pinA, string itemB, int pinB) =>
        conns.Add((new PinRef(itemA, pinA), new PinRef(itemB, pinB)));
}