using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// Exports a Schematic to an EasyEDA Pro `.epro` project file (a zip archive
/// containing `project.json` plus `.esch` / `.esym` / `.efoo` NDJSON files).
///
/// Covers passives (resistor, LED) and the VCC/GND power symbols. Other parts
/// throw NotImplementedException by design — the catalogue is grown one part
/// at a time as new symbol resources are added.
///
/// Resistors use a templated symbol: one shared <c>resistor.esym</c> resource
/// with placeholders that get substituted per-value at zip-build time. So a
/// schematic with 1k, 10k, and 100k resistors produces three distinct .esym
/// files in the zip (one per value), all generated from the same template.
/// LED, VCC, GND use verbatim shipped .esym files (one each).
/// </summary>
public static class EasyEDAExporter
{
    /// <summary>
    /// Write the schematic to <paramref name="path"/> as a `.epro` zip.
    /// Throws NotImplementedException if any device or item in the schematic
    /// has no entry in EasyEDACatalogue (or, for resistors, an unparseable
    /// or unsupported value). Returns non-fatal diagnostics (wire-colour
    /// mismatches within a net, router/exporter pin mismatches) — the file
    /// is still produced; the diagnostics are advisory.
    /// </summary>
    public static EasyEDAExportResult Export(Schematic schematic, string path)
    {
        var diagnostics = new List<TTLSim.Core.Diagnostic>();

        // 1. Discover what parts we need. One catalogue entry per *kind* of
        //    part actually used. For value-bearing parts (resistors), each
        //    distinct value counts as its own kind because each value has
        //    its own EasyEDA library entry (UUID, MPN, LCSC part number).
        var partsUsed = CollectUsedParts(schematic);

        // 2. Build the sheet NDJSON. This places one COMPONENT per unit /
        //    power symbol, plus one WIRE per Connection. The sheet writer
        //    resolves catalogue parts directly via EasyEDACatalogue (it
        //    doesn't need partsUsed; that's for resource emission).
        //    The sheet writer appends to the diagnostics list as it goes.
        string schematicUuid = NewUuid();
        string sheetEsch = EasyEDASheetWriter.Build(schematic, diagnostics);

        // 3. Build project.json carrying the device-binding manifest. The
        //    project title shown inside EasyEDA matches the export filename
        //    so the user finds it under a recognisable name.
        string projectTitle = Path.GetFileNameWithoutExtension(path);
        string projectJson = EasyEDAProjectManifest.Build(
            schematicUuid, projectTitle, partsUsed);

        // 4. Assemble the zip.
        using var zipStream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // The editor writes empty placeholder folders at the root. Mirror them
        // so re-imports look identical to a project saved out of EasyEDA itself.
        foreach (var folder in new[]
        {
            "SHEET/", "INSTANCE/", "SYMBOL/", "PCB/", "FOOTPRINT/",
            "POUR/", "PANEL/", "BLOB/", "FONT/"
        })
        {
            zip.CreateEntry(folder);
        }

        WriteTextEntry(zip, $"SHEET/{schematicUuid}/1.esch", sheetEsch);

        // Embed each distinct symbol once. For templated parts (resistors),
        // each part has a unique SymbolUuid (one per value) AND a shared
        // template resource name, so the loop writes one .esym per value
        // by substituting placeholders.
        var emittedSymbols = new HashSet<string>();
        var emittedFootprints = new HashSet<string>();
        foreach (var part in partsUsed.Values)
        {
            if (emittedSymbols.Add(part.SymbolUuid))
            {
                string esym = LoadResource(part.SymbolResourceName);
                if (part.SymbolTemplateTokens != null)
                    esym = ApplyTemplateTokens(esym, part.SymbolTemplateTokens);
                WriteTextEntry(zip, $"SYMBOL/{part.SymbolUuid}.esym", esym);
            }

            if (part.FootprintUuid != null
                && part.FootprintResourceName != null
                && emittedFootprints.Add(part.FootprintUuid))
            {
                WriteTextEntry(zip, $"FOOTPRINT/{part.FootprintUuid}.efoo",
                    LoadResource(part.FootprintResourceName));
            }
        }

        WriteTextEntry(zip, "project.json", projectJson);

        return new EasyEDAExportResult(diagnostics);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// Discover the catalogue parts referenced by the schematic. The dictionary
    /// is keyed by SymbolUuid -- a string that uniquely identifies the
    /// *catalogue entry* -- so a 1k resistor and a 10k resistor are treated
    /// as separate kinds even though they share the same TTLSim PartDefinition.
    ///
    /// For non-value-bearing parts (LED, VCC, GND) each TTLSim PartDefinition
    /// or item type maps to one CataloguePart, so dedup behaves identically
    /// to the previous "key by PartDefinition" approach.
    ///
    /// Used downstream only via .Values, by EasyEDAProjectManifest.Build (to
    /// emit the device/symbol/footprint sections of project.json) and by the
    /// zip-assembly loop in Export (to write each .esym/.efoo once). The
    /// sheet writer does not consult this dictionary; it resolves parts on
    /// demand via EasyEDACatalogue.
    /// </summary>
    private static Dictionary<string, CataloguePart> CollectUsedParts(Schematic schematic)
    {
        var result = new Dictionary<string, CataloguePart>();

        foreach (var device in schematic.Devices)
        {
            CataloguePart part = EasyEDACatalogue.LookupForDevice(device);
            if (result.ContainsKey(part.SymbolUuid)) continue;
            result[part.SymbolUuid] = part;
        }

        foreach (var item in schematic.Items)
        {
            if (item is Unit) continue;          // covered via Devices above
            CataloguePart part = EasyEDACatalogue.LookupForStandaloneItem(item);
            if (result.ContainsKey(part.SymbolUuid)) continue;
            result[part.SymbolUuid] = part;
        }

        return result;
    }

    private static string ApplyTemplateTokens(string template,
        IReadOnlyDictionary<string, string> tokens)
    {
        // Plain string replacement -- the template uses @@TOKEN@@ markers
        // that are unique enough not to collide with valid NDJSON content.
        foreach (var (placeholder, replacement) in tokens)
            template = template.Replace(placeholder, replacement);
        return template;
    }

    internal static string LoadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        // Resources live under TTLSim.UI/Persistence/EasyEDA/Resources/, which
        // gives them the logical name "TTLSim.UI.Persistence.EasyEDA.Resources.<file>".
        string fullName = $"TTLSim.UI.Persistence.EasyEDA.Resources.{name}";
        using var stream = asm.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{fullName}' not found. Check .csproj <EmbeddedResource> entries.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteTextEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    internal static string NewUuid() => Guid.NewGuid().ToString("N");
}