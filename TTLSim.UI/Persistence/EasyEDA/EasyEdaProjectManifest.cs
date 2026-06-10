using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// Builds the project.json manifest for the .epro zip. Each used part
/// contributes one entry to the `symbols`, `footprints`, and `devices`
/// sections.
///
/// Two sources of fragments:
///   - <c>device_fragments.json</c> (embedded resource): static fragments
///     for LED, VCC, GND, and the shared resistor footprint. Looked up by
///     UUID at export time.
///   - <see cref="CataloguePart.InlineDeviceJson"/> and
///     <see cref="CataloguePart.InlineSymbolJson"/>: per-instance fragments
///     synthesised at catalogue lookup time (used for the 75 per-value
///     resistor parts). When present, these win over the static fragments.
/// </summary>
internal static class EasyEDAProjectManifest
{
    public static string Build(string schematicUuid,
        string projectTitle,
        IReadOnlyDictionary<string, CataloguePart> partsUsed)
    {
        // Load the embedded device-definitions fragment. It contains the
        // full device/symbol/footprint metadata blocks keyed by UUID for
        // every static (non-synthesised) part. The resistor footprint is
        // also in here -- shared across all 75 resistor values.
        var fragmentsJson = EasyEDAExporter.LoadResource("device_fragments.json");
        var fragments = JsonNode.Parse(fragmentsJson)!.AsObject();

        var symbolsOut = new JsonObject();
        var footprintsOut = new JsonObject();
        var devicesOut = new JsonObject();
        var emittedSymbols = new HashSet<string>();
        var emittedFootprints = new HashSet<string>();
        var emittedDevices = new HashSet<string>();

        foreach (var part in partsUsed.Values)
        {
            if (emittedSymbols.Add(part.SymbolUuid))
            {
                symbolsOut[part.SymbolUuid] = part.InlineSymbolJson != null
                    ? JsonNode.Parse(part.InlineSymbolJson)!
                    : LookupFragment(fragments, "symbols", part.SymbolUuid);
            }

            if (part.FootprintUuid != null && emittedFootprints.Add(part.FootprintUuid))
            {
                // Footprints are always static -- per-value parts share a
                // common footprint by design.
                footprintsOut[part.FootprintUuid] =
                    LookupFragment(fragments, "footprints", part.FootprintUuid);
            }

            if (emittedDevices.Add(part.DeviceUuid))
            {
                devicesOut[part.DeviceUuid] = part.InlineDeviceJson != null
                    ? JsonNode.Parse(part.InlineDeviceJson)!
                    : LookupFragment(fragments, "devices", part.DeviceUuid);
            }
        }

        var sheetUuid = EasyEDAExporter.NewUuid();
        var manifest = new JsonObject
        {
            ["schematics"] = new JsonObject
            {
                [schematicUuid] = new JsonObject
                {
                    ["name"] = "Schematic1",
                    ["sheets"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "Board1",
                            ["id"]   = 1,
                            ["uuid"] = sheetUuid
                        }
                    }
                }
            },
            ["pcbs"] = new JsonObject(),
            ["panels"] = new JsonObject(),
            ["symbols"] = symbolsOut,
            ["footprints"] = footprintsOut,
            ["devices"] = devicesOut,
            ["boards"] = new JsonObject(),
            ["config"] = new JsonObject
            {
                ["title"] = projectTitle,
                ["cbbProject"] = false,
                ["defaultSheet"] = "",
                ["editorVersion"] = "2.2.47.7"
            }
        };

        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode DeepClone(JsonNode node)
    {
        // System.Text.Json.Nodes doesn't have a built-in deep clone; round-trip
        // through serialisation. Safe for the small device fragments we have.
        return JsonNode.Parse(node.ToJsonString())!;
    }

    private static JsonNode LookupFragment(JsonObject fragments, string section, string uuid)
    {
        var sec = fragments[section] as JsonObject
            ?? throw new InvalidOperationException(
                $"device_fragments.json is missing the '{section}' section.");
        var entry = sec[uuid]
            ?? throw new InvalidOperationException(
                $"device_fragments.json has no '{section}' entry for UUID '{uuid}'. " +
                "The UUID constant in EasyEDACatalogue.cs likely doesn't match the " +
                "resource file, or a CataloguePart returned a synthesised UUID without " +
                "supplying InlineDeviceJson/InlineSymbolJson.");
        return DeepClone(entry);
    }
}
