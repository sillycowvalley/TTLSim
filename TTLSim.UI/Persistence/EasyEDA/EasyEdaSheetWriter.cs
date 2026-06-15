using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using TTLSim.UI.Components;
using TTLSim.UI.Model;
using TTLSim.UI.Routing;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// Builds the .esch newline-delimited JSON for one sheet.
///
/// Each TTLSim unit / power symbol becomes one EasyEDA COMPONENT record
/// followed by its ATTR records. Each electrical net becomes one WIRE
/// record (carrying every Connection in that net as concatenated
/// segments) -- see the per-net emission loop in Build for why.
///
/// World coordinates: TTLSim grid units * 10 = EasyEDA pixels.
/// </summary>
internal static class EasyEDASheetWriter
{
    private const int Scale = 10;       // TTLSim grid unit -> EasyEDA pixels

    // Preamble FONTSTYLE ids. These live outside the numeric "st<N>" space
    // that IdGenerator produces (starting at st100), so they can't collide
    // with dynamically allocated style ids no matter how many wires the
    // sheet contains. EasyEDA's importer treats FONTSTYLE ids as opaque
    // strings, so the name itself is free-form.
    private const string StyleDefault = "stDefault";        // Name and bookkeeping ATTRs
    private const string StyleDevice = "stDevice";          // invisible Device ATTR, size "10"
    private const string StyleDesignator = "stDesignator";  // visible Designator label, size 8
    private const string StyleValue = "stValue";            // visible Value/Name label, size 6
    private const string StyleNoConnect = "stNoConnect";    // No-Connect flag marker, size 6.75

    // Fetched fresh per access rather than cached: Logging.Log.Reset()
    // would leave a cached ILogger stale.
    private static Microsoft.Extensions.Logging.ILogger log =>
        TTLSim.UI.Logging.Log.For(nameof(EasyEDASheetWriter));

    public static string Build(Schematic schematic,
        IList<TTLSim.Core.Diagnostic> diagnostics)
    {
        var sb = new StringBuilder();
        var idGen = new IdGenerator();

        // ---- preamble ----
        AppendRecord(sb, "DOCTYPE", "SCH", "1.1");
        AppendObjectRecord(sb, "HEAD", new Dictionary<string, object?>
        {
            ["originX"] = 0,
            ["originY"] = 0,
            ["version"] = "2",
            ["maxId"] = 9999
        });

        // Font/line styles. Sizes chosen to match the EasyEDA-side
        // hand-tuned reference (see EasyEDA_Export.md §0).
        AppendRecord(sb, "FONTSTYLE", StyleDefault, null, null, null, null, null, null, null, null, null, null);
        AppendRecord(sb, "FONTSTYLE", StyleDevice, null, null, null, "10", null, null, null, null, null, null);
        AppendRecord(sb, "FONTSTYLE", StyleDesignator, null, null, null, 8, 0, 0, 0, null, 2, 0);
        AppendRecord(sb, "FONTSTYLE", StyleValue, null, null, null, 6, null, null, null, null, null, null);
        // No-Connect flag style. Matches the marker EasyEDA writes when you
        // place a No-Connect by hand (size 6.75).
        AppendRecord(sb, "FONTSTYLE", StyleNoConnect, null, null, null, 6.75, 0, 0, 0, 0, 2, 0);


        // ---- record placement positions per item ----
        // For each placeable item, we compute the EasyEDA COMPONENT placement
        // (worldX, worldY, rotation) AND the world positions of each of its
        // pins. The pin world positions are used to draw wires.
        var placements = new Dictionary<SchematicItem, EasyEDAPlacement>();

        foreach (var item in schematic.Items)
        {
            var part = LookupPart(item);
            placements[item] = ComputePlacement(item, part);
        }

        // ---- emit COMPONENT + ATTRs for each item ----
        // Capture each item's allocated COMPONENT id: the No-Connect pass
        // below builds pin-instance ids (componentId + symbol pin element id)
        // from it.
        var componentIds = new Dictionary<SchematicItem, string>();
        foreach (var item in schematic.Items)
        {
            var part = LookupPart(item);
            var placement = placements[item];
            componentIds[item] = EmitComponent(sb, item, part, placement, idGen);
        }

        // ---- emit WIRE records (one per electrical net) ----
        // Use TTLSim's router so wires avoid component bodies on the
        // TTLSim canvas, then snap the polyline endpoints to where the
        // EasyEDA components' pins actually sit (the EasyEDA library
        // symbols have their own pin spacings, which usually don't match
        // TTLSim's). The snap inserts a short extender segment if the
        // EasyEDA pin is offset from where TTLSim placed the pin.
        var routes = new Routing.WireRouter().RouteAll(schematic);

        // Router-output trace. Second-line diagnostic: when EasyEDA shows
        // wires in unexpected places, the first move per EasyEDA_Export.md
        // §0 is to extract and diff the .epro against a reference. THIS
        // log is only useful when that diff points at a wire-routing issue
        // rather than a record-shape issue -- then it tells you exactly
        // what the router gave the exporter, in TTLSim grid coordinates,
        // before any EasyEDA-side scaling or snapping.
        if (log.IsEnabled(LogLevel.Debug))
        {
            log.LogDebug(
                "Router produced {Count} polylines and {Junctions} junctions",
                routes.Polylines.Count, routes.Junctions.Count);
            foreach (var j in routes.Junctions)
                log.LogDebug("  junction at ({X},{Y})", j.X, j.Y);

            int connIdx = 0;
            foreach (var conn in schematic.Connections)
            {
                string aDesc = $"{conn.A.Owner?.GetType().Name ?? "?"}.pin{conn.A.Number}";
                string bDesc = $"{conn.B.Owner?.GetType().Name ?? "?"}.pin{conn.B.Number}";
                var aPos = conn.A.WorldPosition;
                var bPos = conn.B.WorldPosition;
                string polylineStr = routes.Polylines.TryGetValue(conn, out var poly)
                    ? string.Join(" -> ", poly.Select(p => $"({p.X},{p.Y})"))
                    : "(no polyline)";
                log.LogDebug(
                    "  Conn[{Idx}] {A}@({AX},{AY}) <-> {B}@({BX},{BY})  polyline: {Poly}",
                    connIdx++, aDesc, aPos.X, aPos.Y, bDesc, bPos.X, bPos.Y, polylineStr);
            }
        }

        // Group connections by net (transitive pin closure). The grouping
        // drives BOTH the colour-mismatch diagnostic AND the per-net WIRE
        // emission -- EasyEDA's PCB sync ("Update PCB from Schematic")
        // only fuses segments into one electrical net when they live in
        // the SAME WIRE record and share endpoints there. It does NOT
        // infer junctions from one WIRE's endpoint landing on another
        // WIRE's interior segment, nor even from two separate WIREs
        // sharing an exact corner point. A hand-saved EasyEDA reference
        // file with a T-junction puts all of its segments in one WIRE
        // record, with the trunk vertex appearing as a shared endpoint
        // between segments inside that single record.
        var netGroups = GroupConnectionsByNet(schematic.Connections);

        // ---- EDA003: cross-net coincident wire corners --------------------
        // Detection lives in Routing.CoincidentCornerDetector so the canvas
        // can show the same warnings live, before export. See its summary
        // for why these cells matter (EasyEDA infers a junction → silent
        // net merge).
        {
            var netIdOf = new Dictionary<Connection, int>(schematic.Connections.Count);
            for (int netId = 0; netId < netGroups.Count; netId++)
                foreach (var c in netGroups[netId]) netIdOf[c] = netId;

            var corners = Routing.CoincidentCornerDetector.Detect(
                schematic.Connections, routes, c => netIdOf[c]);

            foreach (var corner in corners)
            {
                var connIds = corner.Connections.Select(c => c.Id).ToList();
                var netList = string.Join(" and ",
                    corner.NetIds.Select(n => DescribeNet(netGroups[n])));

                diagnostics.Add(new TTLSim.Core.Diagnostic(
                    TTLSim.Core.DiagnosticSeverity.Error,
                    "EDA003",
                    $"Coincident wire corner at ({corner.Cell.X},{corner.Cell.Y}) " +
                    $"between nets {netList} — EasyEDA will merge these as a " +
                    "single net. Adjust wire routing or component placement so " +
                    "the corner does not touch another net's wire.",
                    ConnectionIds: connIds,
                    GridPoint: new TTLSim.Core.Diagnostic.GridLocation(
                        corner.Cell.X, corner.Cell.Y)));
            }
        }

        foreach (var group in netGroups)
        {
            if (group.Count < 2) continue;
            var firstColour = group[0].Color;
            bool mismatch = false;
            foreach (var c in group)
            {
                if (c.Color != firstColour) { mismatch = true; break; }
            }
            if (!mismatch) continue;

            // Build a human description of the net for the warning. Prefer
            // the catalogue-supplied net name (VCC/GND); otherwise list pins.
            string netDesc = DescribeNet(group);
            var distinctColours = new HashSet<WireColor>();
            foreach (var c in group) distinctColours.Add(c.Color);
            var colourList = string.Join(", ", distinctColours);
            var connIds = new List<string>(group.Count);
            foreach (var c in group) connIds.Add(c.Id);

            diagnostics.Add(new TTLSim.Core.Diagnostic(
                TTLSim.Core.DiagnosticSeverity.Warning,
                "EDA001",
                $"Wires on net {netDesc} disagree on colour ({colourList}). " +
                "EasyEDA renders one colour per net; the colour of the first " +
                "connection emitted will win.",
                ConnectionIds: connIds));
        }

        // Emit ONE WIRE per electrical net. Every Connection in the net
        // contributes its router polyline (snapped to EasyEDA pin
        // positions by BuildEasyEdaPolyline), and all the resulting
        // segments are concatenated into a single WIRE record's segment
        // list. Branch endpoints from the router already sit on trunk-
        // junction cells, so the resulting WIRE has shared endpoints at
        // T-junctions inside its own segment list -- which is exactly
        // what EasyEDA's PCB sync looks for to fuse the segments into
        // one electrical net. See EasyEDA_Export.md §6.1 -- the "stylistic
        // only" caveat there is wrong; per-net emission is required for
        // Update-PCB-from-Schematic to track connectivity correctly.
        foreach (var group in netGroups)
        {
            string? netName = NetNameForGroup(group);
            // Colour: first connection wins. The EDA001 diagnostic above
            // already warns when a net's connections disagree on colour.
            string hex = WireColorToHex(group[0].Color);

            var segments = new List<List<float>>();
            foreach (var conn in group)
            {
                var easyEdaPolyline = BuildEasyEdaPolyline(
                    conn, routes, placements, diagnostics);
                AppendSegments(segments, easyEdaPolyline);
            }

            if (segments.Count == 0) continue;  // degenerate net
            EmitWireFromSegments(sb, segments, netName, hex, idGen);
        }

        // ---- emit No-Connect flags for floating pins ----
        // Every pin not present in any Connection is intentionally left open
        // in TTLSim (connectivity is validated upstream by the simulator and
        // the floating-input diagnostic). Flag each so EasyEDA's "floating
        // pin" DRC stays quiet without any manual placement.
        EmitNoConnectFlags(sb, schematic, placements, componentIds, idGen);

        return sb.ToString();
    }

    /// <summary>
    /// Group connections by electrical net using union-find over their pins.
    /// Same algorithm as WireRouter.GroupByPin and NetTable.Build, but local
    /// to this file so the sheet writer doesn't pull in the core NetTable
    /// type (which is built later in the build pipeline and isn't available
    /// during export).
    /// </summary>
    private static List<List<Connection>> GroupConnectionsByNet(
        IReadOnlyList<Connection> connections)
    {
        var parent = new int[connections.Count];
        for (int i = 0; i < connections.Count; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var pinToConn = new Dictionary<Pin, int>();
        for (int i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            if (pinToConn.TryGetValue(c.A, out int j1)) Union(i, j1);
            else pinToConn[c.A] = i;
            if (pinToConn.TryGetValue(c.B, out int j2)) Union(i, j2);
            else pinToConn[c.B] = i;
        }

        var byRoot = new Dictionary<int, List<Connection>>();
        for (int i = 0; i < connections.Count; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out var list))
                byRoot[root] = list = new List<Connection>();
            list.Add(connections[i]);
        }
        return byRoot.Values.ToList();
    }

    /// <summary>
    /// Describe a net for diagnostic messages. If any pin on the net belongs
    /// to a Net Flag part (VCC/GND), use that flag's net name. Otherwise
    /// list the pins on the net (e.g. "containing R1.1, R2.1, U3.7").
    /// </summary>
    private static string DescribeNet(List<Connection> group)
    {
        // Look for a net-flag pin first.
        foreach (var c in group)
        {
            var flagName = NetNameForPin(c.A) ?? NetNameForPin(c.B);
            if (flagName is not null) return $"'{flagName}'";
        }

        // Otherwise enumerate pins. Distinct + ordered for readability.
        var pinDescs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in group)
        {
            pinDescs.Add(PinDescription(c.A));
            pinDescs.Add(PinDescription(c.B));
        }
        return "containing " + string.Join(", ", pinDescs);
    }

    private static string PinDescription(Pin pin)
    {
        if (pin.Owner is Unit u) return $"{u.Device.Designator}.{pin.Number}";
        if (pin.Owner is SchematicItem si)
        {
            string label = string.IsNullOrEmpty(si.Label) ? si.GetType().Name : si.Label!;
            return $"{label}.{pin.Number}";
        }
        return $"?.{pin.Number}";
    }

    /// <summary>
    /// Net name for a wire: if either endpoint pin is on a Net Flag part,
    /// return that flag's net name. Drives the WIRE's NET ATTR so EasyEDA's
    /// DRC accepts the connection between the wire and the netflag.
    /// </summary>
    private static string? NetNameForConnection(Connection conn)
    {
        return NetNameForPin(conn.A) ?? NetNameForPin(conn.B);
    }

    /// <summary>
    /// Net name for an entire net group: scan every pin in the group and
    /// return the first netflag name found (VCC / GND / etc). Returns null
    /// for unnamed signal nets. Used now that we emit one WIRE per net --
    /// a netflag attached to only one Connection of a multi-pin net still
    /// has to tag the resulting WIRE, so we resolve the name at net
    /// granularity rather than per-Connection.
    /// </summary>
    private static string? NetNameForGroup(IReadOnlyList<Connection> group)
    {
        foreach (var conn in group)
        {
            string? n = NetNameForPin(conn.A) ?? NetNameForPin(conn.B);
            if (n != null) return n;
        }
        return null;
    }

    private static string? NetNameForPin(Pin pin)
    {
        if (pin.Owner is not SchematicItem item) return null;
        // Resolve via the catalogue directly; results are deterministic
        // per device/item, and the catalogue carries the IsNetFlag/NetName
        // metadata for power-flag parts.
        CataloguePart part = item is Unit unit
            ? EasyEDACatalogue.LookupForDevice(unit.Device)
            : EasyEDACatalogue.LookupForStandaloneItem(item);
        return part.IsNetFlag ? part.NetName : null;
    }


    // ----------------------------------------------------------- placement

    /// <summary>
    /// <summary>
    /// Where the EasyEDA COMPONENT goes, and what world coordinate each of
    /// the TTLSim pins of this item lands at on the EasyEDA sheet.
    ///
    /// Two rotation values are carried because TTLSim and EasyEDA use
    /// opposite rotation senses (visually, the same numeric rotation
    /// produces mirrored bodies for asymmetric symbols). The COMPONENT
    /// record's rotation field, the RotatePoint math, and the pin world
    /// positions all use EasyEdaRotationDeg so they stay self-consistent
    /// with how EasyEDA renders the symbol. Per-part label offset tables
    /// key off TtlSimRotationDeg because they were measured against the
    /// user's drawing intent in TTLSim.
    /// </summary>
    private sealed record EasyEDAPlacement(
        float ComponentX,
        float ComponentY,
        int EasyEdaRotationDeg,
        int TtlSimRotationDeg,
        Dictionary<int, PointF> PinWorldPositions);

    /// <summary>
    /// Anchor the EasyEDA component so that its first listed pin lines up
    /// with the corresponding TTLSim pin's world position (× scale). All
    /// other pins follow from the catalogue's recorded local offsets,
    /// rotated by the item's rotation.
    /// </summary>
    private static EasyEDAPlacement ComputePlacement(SchematicItem item, CataloguePart part)
    {
        int ttlSimRotDeg = (int)item.Rotation;
        // TTLSim and EasyEDA use opposite rotation senses for most parts
        // (visually, the same numeric rotation rotates an asymmetric body
        // the opposite way), so we apply a (360 - r) % 360 shim. Parts
        // whose Pins carry SwapR90R270 = true -- currently only the pin
        // headers -- have their world positions already in EasyEDA's
        // rotation sense, so for those we use the rotation verbatim;
        // applying the shim would double-invert and ship a wrong-rotation
        // .epro. The chosen value drives both RotatePoint and the
        // COMPONENT record so EasyEDA's renderer agrees with our
        // pin-world-position math. 0 and 180 are self-complementary
        // either way; 90 and 270 are where the two paths diverge.
        int easyEdaRotDeg = part.MatchesEasyEdaRotationSense
            ? ttlSimRotDeg
            : (360 - ttlSimRotDeg) % 360;
        easyEdaRotDeg = (easyEdaRotDeg + part.EasyEdaRotationOffsetDeg) % 360;

        // Pick the first known pin number for the anchor.
        int anchorPinNumber = part.PinLocalPositions.Keys.First();

        // TTLSim pin world position in TTLSim grid units, scaled to EasyEDA pixels.
        Pin anchorTtlsimPin = item.Pins.First(p => p.Number == anchorPinNumber);
        Point ttlsimPinWorld = anchorTtlsimPin.WorldPosition;
        float anchorWorldX = ttlsimPinWorld.X * Scale;
        float anchorWorldY = -ttlsimPinWorld.Y * Scale;   // EasyEDA Y is up

        // EasyEDA local pin position, rotated to match.
        Point anchorLocalUnrotated = part.PinLocalPositions[anchorPinNumber];
        PointF anchorLocalRotated = RotatePoint(anchorLocalUnrotated, easyEdaRotDeg);

        // ComponentX/Y is the world position of EasyEDA's origin for this
        // component, such that the anchor pin lands at the target.
        float componentX = anchorWorldX - anchorLocalRotated.X;
        float componentY = anchorWorldY - anchorLocalRotated.Y;

        // Compute all pin world positions for downstream wire emission.
        var pinWorlds = new Dictionary<int, PointF>();
        foreach (var (pinNum, localUnrotated) in part.PinLocalPositions)
        {
            PointF localRotated = RotatePoint(localUnrotated, easyEdaRotDeg);
            pinWorlds[pinNum] = new PointF(
                componentX + localRotated.X,
                componentY + localRotated.Y);
        }

        return new EasyEDAPlacement(componentX, componentY, easyEdaRotDeg, ttlSimRotDeg, pinWorlds);
    }

    /// <summary>
    /// Rotate (x, y) by 0/90/180/270 degrees in EasyEDA's rendering
    /// convention. Derived empirically from EasyEDA reference files
    /// (LEDs_with_D5_and_D6_added.epro) — at rotation 90, library pin 1
    /// at local (-20, 0) lands at offset (0, -20) from the anchor; at
    /// rotation 270, it lands at (0, +20). The 0 and 180 cases are their
    /// own self-inverse so they're unambiguous.
    /// </summary>
    private static PointF RotatePoint(Point p, int rotDeg) => rotDeg switch
    {
        0 => new PointF(p.X, p.Y),
        90 => new PointF(-p.Y, p.X),
        180 => new PointF(-p.X, -p.Y),
        270 => new PointF(p.Y, -p.X),
        _ => throw new ArgumentOutOfRangeException(nameof(rotDeg),
            $"Rotation must be 0/90/180/270, got {rotDeg}.")
    };

    private static PointF WorldPinPosition(Pin pin,
        Dictionary<SchematicItem, EasyEDAPlacement> placements)
    {
        if (pin.Owner == null)
            throw new InvalidOperationException("Pin has no owner; cannot resolve world position.");

        if (!placements.TryGetValue(pin.Owner, out var placement))
            throw new InvalidOperationException(
                $"No placement computed for owner of pin {pin.Owner.GetType().Name}.{pin.Number}.");

        if (!placement.PinWorldPositions.TryGetValue(pin.Number, out var pos))
            throw new InvalidOperationException(
                $"Catalogue entry for {pin.Owner.GetType().Name} has no pin {pin.Number} " +
                "in PinLocalPositions. Update EasyEDACatalogue.");

        return pos;
    }

    private static CataloguePart LookupPart(SchematicItem item)
    {
        // Resolve via the catalogue directly. Lookup is deterministic per
        // device/item, so there's no need for a cache; per-value resistor
        // parts in particular are not addressable by SchematicItem anyway.
        return item is Unit unit
            ? EasyEDACatalogue.LookupForDevice(unit.Device)
            : EasyEDACatalogue.LookupForStandaloneItem(item);
    }

    // ----------------------------------------------------------- emission

    private static string EmitComponent(StringBuilder sb, SchematicItem item,
        CataloguePart part, EasyEDAPlacement placement, IdGenerator idGen)
    {
        string componentId = idGen.NewComponentId();

        // Net Flag COMPONENTs have empty title; regular components carry
        // their PART name with the .N sub-part suffix.
        string componentTitle = part.IsNetFlag ? "" : part.PartTitle;
        AppendRecord(sb, "COMPONENT", componentId, componentTitle,
            placement.ComponentX, placement.ComponentY,
            placement.EasyEdaRotationDeg, 0, new Dictionary<string, object?>(), 0);

        if (part.IsNetFlag)
        {
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Symbol", part.SymbolUuid, null, null, null, null, null, StyleDesignator, 0);
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Device", part.DeviceUuid, 0, 0, null, null, 0, StyleDevice, 0);
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Name", part.NetName, 0, 0, null, null, 0, StyleDefault, 0);
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Relevance", "[]", 0, 0,
                placement.ComponentX + 15, placement.ComponentY + 5, 0, StyleDefault, 0);
            return componentId;
        }

        // Regular component below.
        // Designator resolution, in priority order:
        //   - Unit: its owning Device's designator (e.g. "U1").
        //   - IDesignatedItem (e.g. a can oscillator): its own auto-numbered
        //     designator (e.g. "X1"). If somehow still blank, fall back to its
        //     ReferencePrefix + "?" ("X?") so EasyEDA auto-numbers on import.
        //   - Any other standalone item: a generic "U?" placeholder.
        // The "?" forms also satisfy EasyEDA's DRC rule (a designator must be
        // letters + number or "?"; a bare "?" alone trips it).
        string designator;
        if (item is Unit unit)
        {
            designator = unit.Device.Designator;
        }
        else if (item is IDesignatedItem designated)
        {
            designator = string.IsNullOrEmpty(designated.Designator)
                ? designated.ReferencePrefix + "?"
                : designated.Designator;
        }
        else
        {
            designator = "U?";
        }

        // Label positioning. Per-part offsets live on CataloguePart.LabelOffsets
        // -- the catalogue authors derive them by hand-tuning in EasyEDA and
        // reading back from the saved .epro (see EasyEDA_Export.md §0).
        // Different body shapes (resistor vs LED vs capacitor) genuinely
        // need different offsets, and even within one part the four
        // rotations can each carry their own values.
        if (part.LabelOffsets is null)
        {
            throw new InvalidOperationException(
                $"CataloguePart for '{part.PartTitle}' is missing LabelOffsets. " +
                "Every non-net-flag part must specify label offsets per rotation.");
        }
        // Label offsets key by TTLSim rotation: they were measured
        // against the user's drawing intent in TTLSim. The body in
        // EasyEDA renders the same way the user drew it (thanks to the
        // rotation-sense conversion in ComputePlacement), so an offset
        // that anchored the label correctly in TTLSim still anchors it
        // correctly here.
        LabelOffsetSet offsets = placement.TtlSimRotationDeg switch
        {
            0 => part.LabelOffsets.Rot0,
            90 => part.LabelOffsets.Rot90,
            180 => part.LabelOffsets.Rot180,
            270 => part.LabelOffsets.Rot270,
            _ => throw new ArgumentOutOfRangeException(
                nameof(placement),
                $"Rotation must be 0/90/180/270, got {placement.TtlSimRotationDeg}.")
        };

        // Text rotation for the Designator and Name labels. 0 -> null
        // (EasyEDA's "unrotated" form, matching every existing part's
        // output byte-for-byte); 90/180/270 emit the degree value so the
        // label text rotates to sit beside a vertical-body chip. Only DIP
        // chips at R90/R270 set this; all other parts leave it 0.
        int? labelTextRot = offsets.TextRotationDeg == 0
            ? (int?)null
            : offsets.TextRotationDeg;

        AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
            "Symbol", part.SymbolUuid, null, null, null, null, null, StyleDesignator, 0);
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
            "Designator", designator,
            null, 1,
            placement.ComponentX + offsets.Designator.X,
            placement.ComponentY + offsets.Designator.Y,
            labelTextRot, StyleDesignator, 0);
        // Name ATTR has four shapes:
        //   - For parts with EmitTemplatedName (DIP ICs): Name is emitted
        //     as value=null, valVisible=1, default style -- EasyEDA renders
        //     the device template's templated Name (e.g. "NE555N" from
        //     "={Manufacturer Part}"). Takes precedence over the others.
        //   - For parts with EmitNameOverride (LED): Name carries the
        //     user's label as a visible inscription. Style StyleValue
        //     (size 6), keyVisible=null, matching what EasyEDA's editor
        //     writes when the user toggles Name visibility on.
        //   - For parts with EmitNameOverride + NameLabelUsesDesignatorStyle
        //     (header): Name is the user's label rendered at the same size
        //     as the designator (StyleDesignator, size 8), with keyVisible=0
        //     and rotation=0 -- matching the hand-edited Headers .epro.
        //   - For parts without EmitNameOverride (resistor): Name is the
        //     bookkeeping ATTR blanked to "". Style StyleDefault, keyVisible=0.
        string nameValue = part.EmitNameOverride ? (item.Label ?? "") : "";
        if (part.EmitTemplatedName)
        {
            // DIP IC: render the device template's templated Name (e.g.
            // "={Manufacturer Part}" -> "NE555N") at this position.
            // value=null tells EasyEDA to fall back to the template;
            // valVisible=1 makes it show. labelTextRot rotates the text to
            // sit beside the body at R90/R270 (null = horizontal otherwise).
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Name", null, null, 1,
                placement.ComponentX + offsets.Name.X,
                placement.ComponentY + offsets.Name.Y,
                labelTextRot, StyleDefault, 0);
        }
        else if (part.EmitNameOverride && part.NameLabelUsesDesignatorStyle)
        {
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Name", nameValue, 0, 1,
                placement.ComponentX + offsets.Name.X,
                placement.ComponentY + offsets.Name.Y,
                0, StyleDesignator, 0);
        }
        else if (part.EmitNameOverride)
        {
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Name", nameValue, null, 1,
                placement.ComponentX + offsets.Name.X,
                placement.ComponentY + offsets.Name.Y,
                null, StyleValue, 0);
        }
        else
        {
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Name", nameValue, 0, 1,
                placement.ComponentX + offsets.Name.X,
                placement.ComponentY + offsets.Name.Y,
                0, StyleDefault, 0);
        }
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
            "Device", part.DeviceUuid, 0, 0, null, null, 0, StyleDevice, 0);
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
            "Reuse Block", "", 0, 0, null, null, 0, StyleDefault, 0);
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
            "Group ID", "", 0, 0, null, null, 0, StyleDefault, 0);
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
            "Channel ID", "", 0, 0, null, null, 0, StyleDefault, 0);
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
            "Unique ID", "gg" + componentId, 0, 0, null, null, 0, StyleDefault, 0);

        // Value ATTR for parts that want a visible Value on the schematic.
        // Two modes:
        //   - ValueOverride is non-null (Frankenstein resistor):
        //     emit the override string directly. The placed instance
        //     carries its own display text, separate from whatever
        //     value the shared device template carries.
        //   - ValueOverride is null:
        //     emit value=null with valVisible=1 so EasyEDA falls back
        //     to the device template's Value field. Original behaviour
        //     from when each value had its own per-value device entry;
        //     kept for parts that might still want it later.
        // Without this ATTR at all, EasyEDA stores any templated value
        // internally but doesn't render it.
        if (part.EmitValueLabel)
        {
            AppendRecord(sb, "ATTR", idGen.NewAttrId(), componentId,
                "Value", part.ValueOverride, null, 1,
                placement.ComponentX + offsets.Value.X,
                placement.ComponentY + offsets.Value.Y,
                null, StyleValue, 0);
        }

        return componentId;
    }

    // ----------------------------------------------------------- no-connect

    /// <summary>
    /// Emit a No-Connect flag ATTR for every pin that takes part in no
    /// Connection. In EasyEDA Pro a No-Connect is not a placed object -- it
    /// is an ATTR with key "NO_CONNECT" value "yes" whose parent is the pin
    /// INSTANCE id, formed as the placed COMPONENT's id concatenated with the
    /// pin's element id inside its symbol (e.g. component "e1068" + symbol pin
    /// element "e4" => "e1068e4"). The symbol pin element ids are read from
    /// the .esym itself, since they are author-defined and differ per symbol
    /// (vendor-lifted, hand-authored, and synthesised DIP symbols all number
    /// their pin elements differently).
    ///
    /// Net-flag parts (VCC/GND) are skipped: a No-Connect on a power flag is
    /// meaningless, and their single pin is always wired anyway.
    /// </summary>
    private static void EmitNoConnectFlags(
        StringBuilder sb,
        Schematic schematic,
        Dictionary<SchematicItem, EasyEDAPlacement> placements,
        Dictionary<SchematicItem, string> componentIds,
        IdGenerator idGen)
    {
        // Pins that appear in at least one Connection.
        var connectedPins = new HashSet<Pin>();
        foreach (var conn in schematic.Connections)
        {
            connectedPins.Add(conn.A);
            connectedPins.Add(conn.B);
        }

        // number -> symbol-pin-element-id, parsed once per .esym resource and
        // cached (many components share one symbol; templated resistors share
        // one template, whose pin element ids are not touched by token
        // substitution).
        var pinElementCache = new Dictionary<string, IReadOnlyDictionary<int, string>>();

        foreach (var item in schematic.Items)
        {
            var part = LookupPart(item);
            if (part.IsNetFlag) continue;                       // no NC on power flags
            if (!componentIds.TryGetValue(item, out var componentId)) continue;
            if (!placements.TryGetValue(item, out var placement)) continue;

            IReadOnlyDictionary<int, string>? numberToElement = null;

            foreach (var pin in item.Pins)
            {
                if (connectedPins.Contains(pin)) continue;       // wired -> no NC

                // Lazily resolve the symbol's pin-number -> element-id map the
                // first time this item actually needs it.
                numberToElement ??= GetPinElementIds(part, pinElementCache);

                if (!numberToElement.TryGetValue(pin.Number, out var elementId))
                    continue;   // symbol has no element for this pin number
                if (!placement.PinWorldPositions.TryGetValue(pin.Number, out var pos))
                    continue;   // no placed position (shouldn't happen for a real pin)

                AppendRecord(sb, "ATTR", idGen.NewAttrId(),
                    componentId + elementId, "NO_CONNECT", "yes",
                    0, 0, (int)pos.X, (int)pos.Y, 0, StyleNoConnect, 0);
            }
        }
    }

    /// <summary>
    /// Pin-number -> symbol pin element id for a part's symbol, parsed from
    /// the embedded .esym resource. Cached by resource name.
    /// </summary>
    private static IReadOnlyDictionary<int, string> GetPinElementIds(
        CataloguePart part,
        Dictionary<string, IReadOnlyDictionary<int, string>> cache)
    {
        if (cache.TryGetValue(part.SymbolResourceName, out var cached))
            return cached;

        // Load the same .esym text the zip writer emits, including template
        // token substitution (DIP/resistor templates). Pin element ids and
        // numbers are token-independent, so caching by resource name is safe
        // even though different instances substitute different pin names.
        string esym = EasyEDAExporter.LoadResource(part.SymbolResourceName);
        if (part.SymbolTemplateTokens != null)
        {
            foreach (var (placeholder, replacement) in part.SymbolTemplateTokens)
                esym = esym.Replace(placeholder, replacement);
        }

        var map = ParsePinNumberToElementId(esym);
        cache[part.SymbolResourceName] = map;
        return map;
    }

    /// <summary>
    /// Parse an .esym (NDJSON) into a pin-number -> pin-element-id map. Each
    /// PIN record's id is its element id; the pin's number is carried by a
    /// sibling ATTR record with key "NUMBER" whose parent is that element id.
    /// Two passes so the map is robust to record ordering.
    /// </summary>
    private static IReadOnlyDictionary<int, string> ParsePinNumberToElementId(string esym)
    {
        var pinIds = new HashSet<string>();
        var numberAttrs = new List<(string parent, string number)>();

        foreach (var rawLine in esym.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonElement rec;
            try { rec = JsonSerializer.Deserialize<JsonElement>(line); }
            catch (JsonException) { continue; }
            if (rec.ValueKind != JsonValueKind.Array || rec.GetArrayLength() == 0) continue;

            string tag = rec[0].ValueKind == JsonValueKind.String ? rec[0].GetString()! : "";
            if (tag == "PIN")
            {
                if (rec.GetArrayLength() > 1 && rec[1].ValueKind == JsonValueKind.String)
                    pinIds.Add(rec[1].GetString()!);
            }
            else if (tag == "ATTR" && rec.GetArrayLength() > 4
                     && rec[3].ValueKind == JsonValueKind.String
                     && rec[3].GetString() == "NUMBER")
            {
                string parent = rec[2].ValueKind == JsonValueKind.String ? rec[2].GetString()! : "";
                string number = rec[4].ValueKind switch
                {
                    JsonValueKind.String => rec[4].GetString()!,
                    JsonValueKind.Number => rec[4].GetRawText(),
                    _ => ""
                };
                if (parent.Length > 0 && number.Length > 0)
                    numberAttrs.Add((parent, number));
            }
        }

        var map = new Dictionary<int, string>();
        foreach (var (parent, number) in numberAttrs)
        {
            if (pinIds.Contains(parent) && int.TryParse(number, out int n))
                map[n] = parent;
        }
        return map;
    }

    /// <summary>
    /// Build the EasyEDA-coordinate polyline for one Connection.
    ///
    /// The router's polylines come in TTLSim grid units. The exporter must:
    /// (a) scale them to EasyEDA pixels, and (b) for endpoints that
    /// correspond to pins, pull them onto the EasyEDA pin position — which
    /// can differ from TTLSim's pin position because the EasyEDA library
    /// symbol has its own pin spacing (e.g. resistor pins at ±20 px in
    /// EasyEDA's frame vs. ±30 px in TTLSim grid units scaled by 10).
    ///
    /// Critically, NOT every router endpoint is at a pin. For star-routed
    /// nets the router emits branch polylines that END AT A TRUNK-JUNCTION
    /// CELL on the existing trunk, not at the other pin of the Connection.
    /// Those junction-cell endpoints must be emitted as-is — extending them
    /// out to a pin produces a redundant rail at the common-pin's row,
    /// which is the parallel-rails artefact described in EasyEDA_Export.md §0.
    ///
    /// Per-endpoint decision: if the router endpoint is within 0.5 grid
    /// units (5 EasyEDA px) of either pin's TTLSim-grid-scaled position,
    /// snap it to that pin's EasyEDA position. If it's far from both pins,
    /// it's a trunk-junction cell — emit it scaled, no snap. If a router
    /// endpoint is far from both pins AND doesn't look like a junction
    /// (caller can't tell here), raise EDA002; the wire is still emitted
    /// but the geometry may be wrong.
    /// </summary>
    private static IReadOnlyList<PointF> BuildEasyEdaPolyline(
        Connection conn,
        RouteResult routes,
        Dictionary<SchematicItem, EasyEDAPlacement> placements,
        IList<TTLSim.Core.Diagnostic> diagnostics)
    {
        // Authoritative pin world positions: where we placed the EasyEDA
        // pins on the sheet.
        PointF easyEdaA = WorldPinPosition(conn.A, placements);
        PointF easyEdaB = WorldPinPosition(conn.B, placements);

        if (!routes.Polylines.TryGetValue(conn, out var grid) || grid.Count < 2)
        {
            // Fallback: straight line from EasyEDA pin to EasyEDA pin via
            // an L corner (if needed).
            return BuildLShape(easyEdaA, easyEdaB);
        }

        var pts = new List<PointF>(grid.Count + 2);
        foreach (var p in grid) pts.Add(GridToEasyEda(p));

        // 0.5 grid units == 5 EasyEDA px. Squared so we don't sqrt.
        const float PinSnapToleranceSq = 25f;

        PointF gridScaledA = GridToEasyEda(conn.A.WorldPosition);
        PointF gridScaledB = GridToEasyEda(conn.B.WorldPosition);

        // For each end of the polyline, find its closest pin (if any).
        // Returns the EasyEDA pin position to snap to, or null meaning
        // "this endpoint isn't at a pin — it's a trunk-junction cell, emit
        // as-is".
        PointF? snapStart = ClosestPinSnap(pts[0],
            gridScaledA, gridScaledB, easyEdaA, easyEdaB, PinSnapToleranceSq);
        PointF? snapEnd = ClosestPinSnap(pts[^1],
            gridScaledA, gridScaledB, easyEdaA, easyEdaB, PinSnapToleranceSq);

        // If NEITHER endpoint is at a pin, something is wrong: a polyline
        // should connect at least one of its named pins (the other might
        // be a junction). Raise EDA002 with the connection as locator.
        if (snapStart is null && snapEnd is null)
        {
            diagnostics.Add(new TTLSim.Core.Diagnostic(
                TTLSim.Core.DiagnosticSeverity.Warning,
                "EDA002",
                $"Router polyline for connection {PinDescription(conn.A)} ↔ " +
                $"{PinDescription(conn.B)} has neither endpoint at a pin " +
                $"(pts[0]={pts[0]}, pts[^1]={pts[^1]}, " +
                $"pin A scaled={gridScaledA}, pin B scaled={gridScaledB}). " +
                "Wire emitted as-is; geometry may be wrong.",
                ConnectionIds: new[] { conn.Id }));
        }

        if (snapStart.HasValue) SnapEndpoint(pts, atEnd: false, snapStart.Value);
        if (snapEnd.HasValue) SnapEndpoint(pts, atEnd: true, snapEnd.Value);
        return pts;
    }

    /// <summary>
    /// Decide whether a router-polyline endpoint corresponds to a pin and,
    /// if so, return the EasyEDA pin position to snap it to. Returns null
    /// if the endpoint is far from both pins (i.e. it's a junction cell
    /// the router placed deliberately on a trunk).
    /// </summary>
    private static PointF? ClosestPinSnap(
        PointF endpoint,
        PointF gridScaledA, PointF gridScaledB,
        PointF easyEdaA, PointF easyEdaB,
        float toleranceSq)
    {
        float distSqA = SqDist(endpoint, gridScaledA);
        float distSqB = SqDist(endpoint, gridScaledB);
        bool nearA = distSqA <= toleranceSq;
        bool nearB = distSqB <= toleranceSq;
        if (!nearA && !nearB) return null;
        // If both pins are within tolerance (shouldn't happen on a grid-
        // aligned schematic, but defensive), pick the closer.
        if (nearA && nearB) return distSqA <= distSqB ? easyEdaA : easyEdaB;
        return nearA ? easyEdaA : easyEdaB;
    }

    private static float SqDist(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static void SnapEndpoint(List<PointF> pts, bool atEnd, PointF target)
    {
        int endIdx = atEnd ? pts.Count - 1 : 0;
        int nextIdx = atEnd ? pts.Count - 2 : 1;
        PointF current = pts[endIdx];
        if (Math.Abs(current.X - target.X) < 0.001f
            && Math.Abs(current.Y - target.Y) < 0.001f)
            return; // already on target, no snap needed

        PointF neighbour = pts[nextIdx];
        // Adjacent router segment goes along an axis (router guarantees
        // orthogonal). Insert an extender along the same axis to keep
        // orthogonality.
        bool segmentIsHorizontal = Math.Abs(current.Y - neighbour.Y) < 0.001f;

        if (segmentIsHorizontal)
        {
            // Slide along X first (keep Y), then drop/rise to target.
            PointF bridge = new PointF(target.X, current.Y);
            pts[endIdx] = target;
            if (Math.Abs(bridge.X - current.X) > 0.001f
                && Math.Abs(target.Y - current.Y) > 0.001f)
            {
                // Two-segment extender needed.
                pts.Insert(atEnd ? pts.Count - 1 : 1, bridge);
            }
        }
        else
        {
            // Slide along Y first.
            PointF bridge = new PointF(current.X, target.Y);
            pts[endIdx] = target;
            if (Math.Abs(bridge.Y - current.Y) > 0.001f
                && Math.Abs(target.X - current.X) > 0.001f)
            {
                pts.Insert(atEnd ? pts.Count - 1 : 1, bridge);
            }
        }
    }

    private static IReadOnlyList<PointF> BuildLShape(PointF a, PointF b)
    {
        if (Math.Abs(a.Y - b.Y) < 0.001f || Math.Abs(a.X - b.X) < 0.001f)
            return new[] { a, b };
        return new[] { a, new PointF(b.X, a.Y), b };
    }

    /// <summary>
    /// Append the axis-aligned segments of one polyline onto a shared
    /// segments list. Zero-length segments are skipped (the router
    /// occasionally produces them at endpoint snap points). Used by the
    /// per-net WIRE emission to fold multiple Connections' polylines into
    /// one WIRE record's segment array -- EasyEDA's PCB sync only sees
    /// connectivity inside a single WIRE record.
    /// </summary>
    private static void AppendSegments(
        List<List<float>> segments, IReadOnlyList<PointF> polyline)
    {
        for (int i = 0; i + 1 < polyline.Count; i++)
        {
            var p = polyline[i];
            var q = polyline[i + 1];
            if (Math.Abs(p.X - q.X) < 0.001f && Math.Abs(p.Y - q.Y) < 0.001f)
                continue;
            segments.Add(new() { p.X, p.Y, q.X, q.Y });
        }
    }

    /// <summary>
    /// Emit one LINESTYLE + one WIRE + its NET/Relevance ATTRs from a
    /// pre-built segment list. The segments come from one or more
    /// polylines concatenated together -- one WIRE per electrical net,
    /// carrying every Connection in the net.
    /// </summary>
    private static void EmitWireFromSegments(StringBuilder sb,
        List<List<float>> segments,
        string? netName, string hexColor, IdGenerator idGen)
    {
        string styleId = idGen.NewLineStyleId();
        AppendRecord(sb, "LINESTYLE", styleId, hexColor, null, null, null, null);

        string wireId = idGen.NewWireId();
        AppendRecord(sb, "WIRE", wireId, segments, styleId, 0);

        AppendRecord(sb, "ATTR", idGen.NewAttrId(), wireId,
            "Relevance", "[]", 0, 0, null, null, 0, StyleDefault, 0);
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), wireId,
            "NET", netName ?? "", 0, 0, null, null, 0, StyleDefault, 0);
    }

    private static void EmitWire(StringBuilder sb,
        IReadOnlyList<Point> gridPolyline,
        string? netName, string hexColor, IdGenerator idGen)
    {
        EmitWire(sb, gridPolyline.Select(GridToEasyEda).ToArray(),
                 netName, hexColor, idGen);
    }

    private static void EmitWire(StringBuilder sb,
        IReadOnlyList<PointF> polyline,
        string? netName, string hexColor, IdGenerator idGen)
    {
        // Each consecutive pair of polyline points becomes one segment.
        // EasyEDA expects each segment as [x1,y1,x2,y2] in its segments
        // array. The router guarantees axis-aligned segments (one of dx
        // or dy is zero), so we emit them straight through.
        var segments = new List<List<float>>(polyline.Count - 1);
        for (int i = 0; i + 1 < polyline.Count; i++)
        {
            var p = polyline[i];
            var q = polyline[i + 1];
            // Skip zero-length segments the router occasionally emits.
            if (Math.Abs(p.X - q.X) < 0.001f && Math.Abs(p.Y - q.Y) < 0.001f)
                continue;
            segments.Add(new() { p.X, p.Y, q.X, q.Y });
        }

        if (segments.Count == 0) return;  // degenerate, nothing to emit

        // Each wire gets its own LINESTYLE so it can carry its own colour.
        string styleId = idGen.NewLineStyleId();
        AppendRecord(sb, "LINESTYLE", styleId, hexColor, null, null, null, null);

        string wireId = idGen.NewWireId();
        AppendRecord(sb, "WIRE", wireId, segments, styleId, 0);

        AppendRecord(sb, "ATTR", idGen.NewAttrId(), wireId,
            "Relevance", "[]", 0, 0, null, null, 0, StyleDefault, 0);
        AppendRecord(sb, "ATTR", idGen.NewAttrId(), wireId,
            "NET", netName ?? "", 0, 0, null, null, 0, StyleDefault, 0);
    }

    /// <summary>
    /// Convert a TTLSim grid-unit point to EasyEDA pixel coordinates:
    /// multiply by Scale and invert Y (TTLSim Y-down → EasyEDA Y-up).
    /// </summary>
    private static PointF GridToEasyEda(Point grid) =>
        new PointF(grid.X * Scale, -grid.Y * Scale);

    /// <summary>
    /// Hex CSS-style colour string for a TTLSim WireColor, matching the
    /// RGB values in WireColors.ToColor(). EasyEDA's LINESTYLE second
    /// field accepts "#RRGGBB" form.
    /// </summary>
    private static string WireColorToHex(WireColor color)
    {
        Color c = color.ToColor();
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // ----------------------------------------------------------- JSON output

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // No indenting, no extra whitespace -- NDJSON is one record per line.
    };

    private static void AppendRecord(StringBuilder sb, params object?[] fields)
    {
        sb.Append(JsonSerializer.Serialize(fields, SerializerOptions));
        sb.Append('\n');
    }

    private static void AppendObjectRecord(StringBuilder sb, string cmd, Dictionary<string, object?> obj)
    {
        // EasyEDA HEAD records are `[cmd, {...}]` -- the second slot is an object.
        AppendRecord(sb, cmd, obj);
    }

    // ----------------------------------------------------------- id allocation

    private sealed class IdGenerator
    {
        private int next = 1000;
        private int nextStyle = 100;
        public string NewComponentId() => $"e{next++}";
        public string NewAttrId() => $"e{next++}";
        public string NewWireId() => $"e{next++}";
        public string NewLineStyleId() => $"st{nextStyle++}";
    }
}