using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Persistence;

/// <summary>
/// Saves and loads schematics to/from .ttlproj JSON files.
///
/// This type owns only the file concerns: JSON (de)serialisation, the view
/// (zoom/pan) block, and migration of legacy on-disk shapes. The object-graph
/// &lt;-&gt; DTO mapping itself lives in <see cref="SchematicDtoMapper"/> and is
/// the single home for it -- both this loader and <see cref="ClipboardService"/>
/// go through the mapper, so the part-lookup tables and unit construction exist
/// in exactly one place and cannot drift.
///
/// Two things stay here rather than in the mapper because they are intrinsically
/// file-format / legacy concerns that never arise from a clipboard payload:
///
///   1. Legacy Wires. Files written by older versions stored wires with
///      embedded waypoint geometry. On load those are migrated to plain
///      Connections (pin-anchored only; free-point endpoints are dropped, and
///      waypoint geometry is discarded since the new model derives geometry at
///      paint time). The mapper never writes Wires, so this only ever fires for
///      genuinely old files.
///
///   2. Missing-designator fill-in. A designated standalone item (the canned
///      oscillator, reference prefix "X") in a file old enough to predate the
///      designator field arrives with an empty designator. The historical load
///      behaviour assigns it the next free X-number; the mapper's Preserve path
///      leaves ids and designators verbatim, so the loader does this fix-up.
/// </summary>
public static class SchematicSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ------------------------------------------------------------------ Save

    public static void Save(string path, Schematic schematic, float zoom, PointF pan)
    {
        var file = new SchematicFile
        {
            Schematic = SchematicDtoMapper.ToDto(
                schematic.Devices, schematic.Items, schematic.Connections),
            View = new ViewDto
            {
                Zoom = zoom,
                Pan = new PointDto { X = (int)pan.X, Y = (int)pan.Y }
            }
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(path, json);
    }

    // ------------------------------------------------------------------ Load

    public sealed class LoadResult
    {
        public Schematic Schematic { get; init; } = new();
        public float? Zoom { get; init; }
        public PointF? Pan { get; init; }
    }

    public static LoadResult Load(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<SchematicFile>(json, JsonOptions)
            ?? throw new InvalidDataException("File is empty or invalid.");

        // Rebuild the object graph through the single mapper. Preserve keeps the
        // file's ids and designators verbatim -- on load the file is the source
        // of truth, so no designatorScope is needed.
        var result = SchematicDtoMapper.FromDto(file.Schematic, IdPolicy.Preserve, null);

        var schematic = new Schematic();
        schematic.Devices.AddRange(result.Devices);
        foreach (var item in result.Items)
            schematic.Add(item);
        foreach (var connection in result.Connections)
            schematic.Add(connection);

        // Compat fix-up (1): give any designated item that predates the
        // designator field the next free number, unique against everything
        // already loaded. Assigned one at a time so each is visible to the next
        // NextDesignator call.
        foreach (var item in schematic.Items)
        {
            if (item is IDesignatedItem des && string.IsNullOrEmpty(des.Designator))
                des.Designator = schematic.NextDesignator(des.ReferencePrefix);
        }

        // Compat fix-up (2): migrate legacy Wires to Connections. Free-point
        // endpoints have no pin to attach to and are dropped; unresolved
        // endpoints are dropped. The mapper never writes Wires.
        var itemsById = schematic.Items.ToDictionary(i => i.Id);
        foreach (var wire in file.Schematic.Wires)
        {
            if (wire.A.FreePoint != null || wire.B.FreePoint != null) continue;

            var a = ResolveLegacyPin(wire.A, itemsById);
            var b = ResolveLegacyPin(wire.B, itemsById);
            if (a == null || b == null) continue;

            schematic.Add(new Connection(a, b) { Id = wire.Id });
        }

        return new LoadResult
        {
            Schematic = schematic,
            Zoom = file.View?.Zoom,
            Pan = file.View != null ? new PointF(file.View.Pan.X, file.View.Pan.Y) : null
        };
    }

    private static Pin? ResolveLegacyPin(EndpointDto dto,
        Dictionary<string, SchematicItem> itemsById)
    {
        if (string.IsNullOrEmpty(dto.ItemId)) return null;
        if (!itemsById.TryGetValue(dto.ItemId, out var item)) return null;
        return item.Pins.FirstOrDefault(p => p.Number == dto.PinNumber);
    }
}