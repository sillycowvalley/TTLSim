using System.Collections.Generic;
using TTLSim.Core;
using Xunit;

namespace TTLSim.Tests;

/// <summary>
/// TTL040 (no program image loaded) and TTL041 (image present but unreadable),
/// for the two program-bearing families: parallel EEPROM (Intel HEX) and GAL
/// (JEDEC fuse map).
/// </summary>
public class ProgramImageCheckTests
{
    // ------------------------------------------------------- TTL040: blank

    [Fact]
    public void Eeprom_with_no_program_is_TTL040()
    {
        ProgramInput input = new();
        input.AddChip("d1", "U9", "28C256", program: null);

        var found = ProgramImageCheck.Check(input);

        Assert.Contains(found,
            d => d.Severity == DiagnosticSeverity.Warning
                 && d.Code == "TTL040"
                 && d.ItemId == "u1");
    }

    [Fact]
    public void Gal_with_no_program_is_TTL040()
    {
        ProgramInput input = new();
        input.AddChip("d1", "U4", "GAL16V8", program: null);

        var found = ProgramImageCheck.Check(input);

        Assert.Contains(found,
            d => d.Severity == DiagnosticSeverity.Warning && d.Code == "TTL040");
    }

    [Fact]
    public void Whitespace_only_program_counts_as_blank()
    {
        // The property grid writes "" when the user clears the field, and the
        // serializer round-trips it; treat it exactly like null.
        ProgramInput input = new();
        input.AddChip("d1", "U9", "28C64", program: "   \r\n  ");

        var found = ProgramImageCheck.Check(input);

        Assert.Contains(found, d => d.Code == "TTL040");
    }

    [Fact]
    public void Every_eeprom_and_gal_part_number_is_recognised()
    {
        // Guards the identifier tables against a part being added to the
        // catalogue and quietly escaping the check.
        string[] parts =
        {
            "28C256", "28C128", "28C64", "28C16",
            "GAL16V8", "GAL20V8", "GAL22V10",
        };

        foreach (string part in parts)
        {
            ProgramInput input = new();
            input.AddChip("d1", "U1", part, program: null);

            Assert.Contains(ProgramImageCheck.Check(input), d => d.Code == "TTL040");
        }
    }

    // -------------------------------------------------- TTL041: malformed

    [Fact]
    public void Gal_with_unparseable_fuse_map_is_TTL041()
    {
        // No QF field at all -- JedecFuseMap.Parse throws FormatException
        // ("JEDEC file has no QF (fuse count) field.").
        ProgramInput input = new();
        input.AddChip("d1", "U4", "GAL16V8", program: "this is not a fuse map");

        var found = ProgramImageCheck.Check(input);

        Assert.Contains(found,
            d => d.Severity == DiagnosticSeverity.Warning && d.Code == "TTL041");
        Assert.DoesNotContain(found, d => d.Code == "TTL040");
    }

    [Fact]
    public void Eeprom_with_unparseable_hex_is_TTL041()
    {
        ProgramInput input = new();
        input.AddChip("d1", "U9", "28C256", program: ":ZZNOTHEX");

        var found = ProgramImageCheck.Check(input);

        Assert.Contains(found,
            d => d.Severity == DiagnosticSeverity.Warning && d.Code == "TTL041");
    }

    // ------------------------------------------------------------- quiet

    [Fact]
    public void Sram_is_never_flagged()
    {
        // SRAM powers up blank by design and carries no program at all.
        ProgramInput input = new();
        input.AddChip("d1", "U10", "62256", program: null);
        input.AddChip("d2", "U11", "6116", program: null);
        input.AddChip("d3", "U12", "2114", program: null);

        Assert.Empty(ProgramImageCheck.Check(input));
    }

    [Fact]
    public void Ordinary_logic_parts_are_never_flagged()
    {
        ProgramInput input = new();
        input.AddChip("d1", "U1", "00", program: null);
        input.AddChip("d2", "U2", "181", program: null);

        Assert.Empty(ProgramImageCheck.Check(input));
    }

    // ----------------------------------------------------- builder wiring

    [Fact]
    public void Builder_surfaces_TTL040_in_the_build_result()
    {
        // The check must run inside SchematicBuilder.Build, not just standalone.
        ProgramInput input = new();
        input.AddChip("d1", "U9", "28C256", program: null);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.Contains(result.Diagnostics, d => d.Code == "TTL040");
    }

    [Fact]
    public void A_blank_eeprom_does_not_block_the_build()
    {
        // Warning, not Error: the rest of the board must still simulate.
        ProgramInput input = new();
        input.AddChip("d1", "U9", "28C256", program: null);

        BuildResult result = new SchematicBuilder().Build(input);

        Assert.True(result.Succeeded);
    }
}

/// <summary>
/// Minimal IBuildInput carrying a Program string, which neither FakeInput nor
/// ContentionInput exposes. Single-unit box parts only -- every EEPROM and GAL
/// is one. No WinForms anywhere.
/// </summary>
internal sealed class ProgramInput : IBuildInput
{
    private readonly List<BuildDevice> devices = new();

    public IEnumerable<BuildDevice> Devices => devices;
    public IEnumerable<BuildItem> Items => System.Array.Empty<BuildItem>();
    public IEnumerable<(PinRef A, PinRef B)> Connections =>
        System.Array.Empty<(PinRef, PinRef)>();

    /// <summary>Adds a single-unit box chip whose unit id is always "u1", so
    /// the locator assertion has a fixed target.</summary>
    public void AddChip(string id, string designator, string partId, string? program)
    {
        BuildUnit unit = new("u1", '\0', System.Array.Empty<int>(), null);
        devices.Add(new BuildDevice(
            id, designator, partId, "HC", null, null, new[] { unit },
            Program: program));
    }
}
