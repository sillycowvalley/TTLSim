using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Export;

/// <summary>
/// BOM label sheet export: one printable stick-on label per physical chip on
/// the current schematic, duplicates included and grouped by part number,
/// written as a multi-page A4 PDF. Print at 100% (no printer scaling), cut
/// out, and place on the DIP top -- pin names align with the legs.
///
/// All geometry, typography, and provenance rules live in Chip_Labels.md and
/// are ratified by physical print tests; the constants below transcribe that
/// document directly. Text renders as filled vector paths from the embedded
/// VectorFont.json (see LabelVectorFont), so the PDF has no font
/// dependencies and is dimensionally exact on any printer.
/// </summary>
public static class ChipLabelSheetExporter
{
    // ---- page (A4, points; 1 pt = 1/72 inch) ---------------------------
    private const double PageWidth = 595.32;
    private const double PageHeight = 841.92;
    private const double Margin = 28.35;             // 10 mm
    private const double MmToPt = 72.0 / 25.4;

    // ---- label geometry (Chip_Labels.md section 2) ----------------------
    private const double RowPitch = 7.2;             // 0.1 in per pin pair -- physical DIP pitch
    private const double NarrowWidth = 17.0;         // 6.0 mm  (0.3 in packages)
    private const double WideWidth = 36.0;           // 12.7 mm (0.6 in packages)
    private const double BorderStroke = 0.4;

    /// <summary>
    /// Narrow (0.3 in) package label lengths in mm, keyed by pin count.
    /// Sized to the SHORTEST common JEDEC body so the sticker fits any
    /// manufacturer. The 24 row is the skinny DIP-24 (GAL20V8 class) --
    /// provisional pending a caliper check against real stock. An unknown
    /// pin count falls back to rows x 2.54 mm so a new package still
    /// exports; add its measured row here afterwards.
    /// </summary>
    private static readonly Dictionary<int, double> NarrowLengthMm = new()
    {
        [8] = 9.0,
        [14] = 18.5,
        [16] = 18.5,
        [18] = 21.5,
        [20] = 23.5,
        [24] = 29.0,
    };

    // ---- typography (Chip_Labels.md section 2) --------------------------
    private const double PinNameSize = 4.0;          // matches Grant Searle's originals
    private const double PinNameFloor = 2.4;         // fit lower bound
    private const double ComfortableSize = 3.2;      // below this, tier-2 fitting kicks in
    private const double TwoLineMaxSize = 3.0;       // two lines must fit the 7.2 pt row
    private const double EdgeInset = 1.2;            // name to label edge (normal rows)
    private const double TightInset = 0.5;           // name to label edge (crowded rows)
    private const double CentreGap = 2.0;            // min gap between the two columns
    private const double BorderClearance = 0.55;     // 0.2 mm glyph-to-border clearance
    /// <summary>Tint of the big chip name behind the pin names: a gray
    /// level from 0.0 (black) to 1.0 (white). Lower = darker / more
    /// prominent, higher = fainter. The single knob for that colour.</summary>
    private const double PartNumberGray = 0.78;
    private const double PartNumberWidthFraction = 0.62;
    private const double PartNumberLengthFraction = 0.88;

    // ---- sheet layout ----------------------------------------------------
    private const double GapInsideGroup = 8.0;       // between duplicate labels
    private const double GapBetweenGroups = 18.0;
    private const double CaptionSize = 4.0;
    private const double CaptionGap = 3.0;           // caption to label top
    private const double ShelfGap = 16.0;            // vertical gap between layout rows

    /// <summary>
    /// Export the BOM label sheet for <paramref name="schematic"/> to
    /// <paramref name="filePath"/>. Returns the number of labels written
    /// (0 = nothing labelable on the schematic; no file is written).
    /// </summary>
    public static int Export(Schematic schematic, string filePath)
    {
        LabelVectorFont font = LabelVectorFont.LoadEmbedded();

        // ---- BOM: group labelable devices ---------------------------------
        // Labelable = box chips (ChipPartDefinition) in a DIP package.
        // TO-92 parts (DS1813) have no flat top to stick a label on.
        // Passives, headers, and standalone items are not chips.
        //
        // GALs group by part number AND fuse map: a GAL is whatever its
        // program makes it, so two differently-programmed GAL16V8s must not
        // share a label. A programmed GAL displays its design name from the
        // JEDEC header ("GAL1_ALU") and its header/fuse-derived pin names;
        // its caption carries the designators so the sticker is traceable to
        // the schematic.
        var groups = schematic.Devices
            .Where(d => d.Definition is ChipPartDefinition { To92: false })
            .GroupBy(d => d.Definition is ChipPartDefinition cp
                          && Device.Identifiers.Gal.Contains(cp.PartNumber)
                ? d.FullPartNumber + "\n" + (d.Program ?? "") + "\n" + UnitLabel(d)
                : d.FullPartNumber)
            .Select(g => BuildGroup(g.ToList()))
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0) return 0;

        // ---- shelf-pack the labels across pages ---------------------------
        var layout = new SheetLayout();
        int labelCount = 0;

        foreach (LabelGroup group in groups)
        {
            (double width, double height) = LabelSize(group.Definition);
            double slotHeight = CaptionSize + CaptionGap + height;
            string caption = group.Count == 1
                ? group.DisplayName
                : string.Format(CultureInfo.InvariantCulture, "{0} x {1}", group.Count, group.DisplayName);
            if (group.DesignatorNote is not null)
                caption += " (" + group.DesignatorNote + ")";
            double captionWidth = font.MeasureText(caption, CaptionSize);

            for (int i = 0; i < group.Count; i++)
            {
                (double x, double slotTop, bool startedRow) = layout.Place(width, slotHeight);

                // Caption above the first label of the group, and again after
                // any row/page wrap so a split group stays identified. The
                // caption can be wider than the label, so its extent is
                // reserved -- the next group starts past it, never under it.
                if (i == 0 || startedRow)
                {
                    layout.Ops.Append("0 0 0 rg\n");
                    font.AppendTextOps(layout.Ops, caption, x, slotTop - CaptionSize, CaptionSize);
                    layout.ReserveRight(x + captionWidth);
                }

                DrawLabel(layout.Ops, font, group,
                    x, slotTop - CaptionSize - CaptionGap);
                labelCount++;
            }

            layout.EndGroup();
        }

        // ---- write the PDF -------------------------------------------------
        File.WriteAllBytes(filePath, BuildPdf(layout.Pages));
        return labelCount;
    }

    private sealed record LabelGroup(
        string DisplayName,
        string LabelText,
        ChipPartDefinition Definition,
        int Count,
        Dictionary<int, string>? PinNameOverrides,
        string? DesignatorNote);

    /// <summary>
    /// Build one label group from the devices sharing a group key. For a
    /// programmed GAL: the display name is the design name from the JEDEC
    /// header (falling back to the part number), the pin names are the
    /// header signal names overlaid on the fuse-derived role labels
    /// (matching ChipUnit's symbol labelling), and the caption gains the
    /// device designators.
    /// </summary>
    private static LabelGroup BuildGroup(List<Device> devices)
    {
        Device first = devices[0];
        var chip = (ChipPartDefinition)first.Definition;
        string displayName = first.FullPartNumber;
        Dictionary<int, string>? overrides = null;
        string? designatorNote = null;

        bool isGal = Device.Identifiers.Gal.Contains(chip.PartNumber);

        // Gray in-sticker text, in precedence order for a GAL:
        //   1. the unit's user label -- populated at JEDEC import and freely
        //      editable, so it is the authoritative user-facing name;
        //   2. the cleaned design name from the fuse-map header
        //      ("PLD1_ALU" -> "1 ALU") for older projects whose label was
        //      never populated;
        //   3. the bare device type (16V8, 22V10) for unprogrammed or
        //      nameless parts.
        // The full design name stays in the caption for traceability.
        string labelText = isGal && chip.PartNumber.StartsWith("GAL", StringComparison.Ordinal)
            ? chip.PartNumber.Substring(3)
            : first.FullPartNumber;
        if (isGal && !string.IsNullOrWhiteSpace(first.Program))
        {
            string program = first.Program!;
            displayName = TTLSim.Chips.Pld.GalJedecHeader.TryParseDesignName(program)
                          ?? displayName;
            labelText = TTLSim.Chips.Pld.GalJedecHeader.TryParseDisplayName(program)
                        ?? labelText;

            var derived = TTLSim.Chips.Pld.GalPinModel.TryDerive(chip.PartNumber, program);
            var headerNames = TTLSim.Chips.Pld.GalJedecHeader.TryParsePinNames(program);
            if (derived is not null || headerNames is not null)
            {
                overrides = new Dictionary<int, string>();
                if (derived is not null)
                    foreach (var gp in derived)
                        overrides[gp.Number] = gp.Label;
                if (headerNames is not null)
                    foreach (var kv in headerNames)
                        overrides[kv.Key] = kv.Value;
            }

            designatorNote = string.Join(" ", devices
                .Select(d => d.Designator)
                .Where(d => !string.IsNullOrEmpty(d))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            if (designatorNote.Length == 0) designatorNote = null;
        }

        // Highest precedence: the unit's user label (see above). Grouping
        // already keys GALs on this label, so every device in the group
        // shares it.
        if (isGal)
        {
            string userLabel = UnitLabel(first);
            if (userLabel.Length > 0) labelText = userLabel;
        }

        return new LabelGroup(displayName, labelText, chip, devices.Count, overrides, designatorNote);
    }

    /// <summary>The user label of the device's chip unit (the free-text
    /// label drawn beside the symbol, populated at JEDEC import), trimmed;
    /// empty when unlabelled.</summary>
    private static string UnitLabel(Device device)
    {
        foreach (var unit in device.Units)
        {
            if (unit is ChipUnit)
                return unit.Label.Trim();
        }
        return "";
    }

    // ------------------------------------------------------------------ shelf layout

    /// <summary>
    /// Left-to-right, top-to-bottom shelf packing across A4 pages. Each
    /// placed slot advances the cursor by its width plus the in-group gap;
    /// <see cref="EndGroup"/> widens the gap before the next group starts.
    /// A slot that doesn't fit the remaining shelf width starts a new shelf;
    /// a shelf that doesn't fit the remaining page height starts a new page.
    /// </summary>
    private sealed class SheetLayout
    {
        public List<StringBuilder> Pages { get; } = new();
        public StringBuilder Ops { get; private set; } = null!;

        private double x;
        private double shelfTop;
        private double shelfHeight;

        public SheetLayout() => StartPage();

        /// <summary>Reserve a slot; returns its top-left corner and whether a
        /// new shelf (or page) was started to fit it.</summary>
        public (double X, double SlotTop, bool StartedRow) Place(double width, double height)
        {
            bool startedRow = false;

            if (x + width > PageWidth - Margin)
            {
                StartShelf();
                startedRow = true;
            }
            if (shelfTop - height < Margin)
            {
                StartPage();
                startedRow = true;
            }

            double placedX = x;
            x += width + GapInsideGroup;
            shelfHeight = Math.Max(shelfHeight, height);
            return (placedX, shelfTop, startedRow);
        }

        // Rightmost caption extent on the current shelf; the next group must
        // start past it even when its labels are narrower than the caption.
        private double reservedRight;

        public void ReserveRight(double rightEdge) =>
            reservedRight = Math.Max(reservedRight, rightEdge);

        public void EndGroup() =>
            x = Math.Max(x + GapBetweenGroups - GapInsideGroup, reservedRight + GapBetweenGroups);

        private void StartShelf()
        {
            shelfTop -= shelfHeight + ShelfGap;
            x = Margin;
            shelfHeight = 0;
            reservedRight = 0;
        }

        private void StartPage()
        {
            Ops = new StringBuilder("0 0 0 rg\n0 0 0 RG\n");
            Pages.Add(Ops);
            x = Margin;
            shelfTop = PageHeight - Margin;
            shelfHeight = 0;
            reservedRight = 0;
        }
    }

    // ------------------------------------------------------------------ one label

    private static (double Width, double Height) LabelSize(ChipPartDefinition chip)
    {
        int rows = (chip.PinCount + 1) / 2;
        bool wide = IsWidePackage(chip);
        double width = wide ? WideWidth : NarrowWidth;
        double height = wide
            ? rows * RowPitch
            : (NarrowLengthMm.TryGetValue(chip.PinCount, out double mm)
                ? mm * MmToPt
                : rows * RowPitch);   // unknown narrow package: usable fallback
        return (width, height);
    }

    /// <summary>
    /// Physical package width. ChipPartDefinition.BodyWidth is a schematic
    /// drawing width in grid cells, but across the whole current catalogue it
    /// tracks the physical package: 12 for every 0.6 in part (memories, '181,
    /// 6116) and 8 for every 0.3 in part -- including the GAL20V8, which is a
    /// 24-pin SKINNY DIP and must NOT be widened by pin count. If a future
    /// definition breaks this correlation, add an explicit package field to
    /// ChipPartDefinition instead of patching here.
    /// </summary>
    private static bool IsWidePackage(ChipPartDefinition chip) => chip.BodyWidth >= 12;

    private static void DrawLabel(StringBuilder ops, LabelVectorFont font,
        LabelGroup group, double x, double yTop)
    {
        ChipPartDefinition chip = group.Definition;
        string partName = group.LabelText;
        int rows = (chip.PinCount + 1) / 2;
        (double width, double height) = LabelSize(chip);
        double yBottom = yTop - height;

        // Border.
        ops.Append(LabelVectorFont.Fmt(BorderStroke)).Append(" w\n0 0 0 RG\n")
           .Append(LabelVectorFont.Fmt(x)).Append(' ')
           .Append(LabelVectorFont.Fmt(yBottom)).Append(' ')
           .Append(LabelVectorFont.Fmt(width)).Append(' ')
           .Append(LabelVectorFont.Fmt(height)).Append(" re S\n");

        // Part number FIRST (underneath): gray, rotated 90, centred, as large
        // as fits -- 62% of the width (cap-height basis), shrinking until its
        // length fits 88% of the label.
        double partSize = PartNumberWidthFraction * width / LabelVectorFont.CapHeightEm;
        while (partSize > 4.0 && font.MeasureText(partName, partSize) > PartNumberLengthFraction * height)
            partSize -= 0.25;
        double partLength = font.MeasureText(partName, partSize);
        string gray = LabelVectorFont.Fmt(PartNumberGray);
        ops.Append(gray).Append(' ').Append(gray).Append(' ').Append(gray).Append(" rg\n");
        font.AppendTextOps(ops, partName,
            x + width / 2 + LabelVectorFont.CapHeightEm * partSize / 2,
            yTop - height / 2 - partLength / 2,
            partSize, rotationDeg: 90);
        ops.Append("0 0 0 rg\n");

        // Pin names on top. Left column pins 1..N/2 top-to-bottom; right
        // column pin N opposite pin 1 (DIP mirror). Rows on exact 0.1 in
        // pitch centred in the body; names verbatim from the definition;
        // NC prints blank; leading '/' kept literally.
        // Pin names: the group's overrides (GAL header signal names over
        // fuse-derived roles) where present, else the static definition.
        var namesByNumber = chip.Pins.ToDictionary(p => p.Number, p => p.Name);
        if (group.PinNameOverrides is not null)
            foreach (var kv in group.PinNameOverrides)
                namesByNumber[kv.Key] = kv.Value;
        double pinSpan = (rows - 1) * RowPitch;
        double firstRowCentreY = yTop - (height - pinSpan) / 2;

        // Pass 1: resolve each row's layout (single line or underscore
        // split, normal or tight inset) and its natural fit size.
        // Pass 2 draws every row at the label-wide MINIMUM of those sizes,
        // so all pin names on one label share a single font size -- the
        // most crowded row sets it (Chip_Labels.md section 2).
        var rowPlans = new List<(double CentreY, string[] Left, string[] Right, double Inset, double Size)>();
        for (int row = 0; row < rows; row++)
        {
            double centreY = firstRowCentreY - row * RowPitch;
            string left = PrintableName(namesByNumber, row + 1);
            string right = PrintableName(namesByNumber, chip.PinCount - row);
            if (left.Length == 0 && right.Length == 0) continue;

            double unitLeft = font.MeasureText(left, 1.0);
            double unitRight = font.MeasureText(right, 1.0);
            double SizeFor(double uL, double uR, double inset)
            {
                double available = width - 2 * inset - CentreGap;
                double total = uL + uR;
                return total <= 0 ? PinNameSize
                    : Math.Min(PinNameSize, available / total);
            }

            // Tier 1: a single line at the normal edge inset, when it stays
            // comfortably readable.
            double plain = SizeFor(unitLeft, unitRight, EdgeInset);
            if (plain >= ComfortableSize)
            {
                rowPlans.Add((centreY, new[] { left }, new[] { right }, EdgeInset, plain));
                continue;
            }

            // Tier 2: crowded row. Names move to the tight inset, and a name
            // containing '_' splits onto two lines at the first underscore
            // (the underscore itself is dropped) -- "IOADDR_SEL" becomes
            // "IOADDR" over "SEL". The row takes whichever way yields the
            // larger text.
            string[] leftLines = SplitAtUnderscore(left);
            string[] rightLines = SplitAtUnderscore(right);
            bool anySplit = leftLines.Length > 1 || rightLines.Length > 1;

            double singleSize = Math.Max(PinNameFloor,
                SizeFor(unitLeft, unitRight, TightInset));
            double splitSize = Math.Max(PinNameFloor, Math.Min(TwoLineMaxSize,
                SizeFor(MaxUnitWidth(font, leftLines), MaxUnitWidth(font, rightLines), TightInset)));

            if (anySplit && splitSize > singleSize)
                rowPlans.Add((centreY, leftLines, rightLines, TightInset, splitSize));
            else
                rowPlans.Add((centreY, new[] { left }, new[] { right }, TightInset, singleSize));
        }

        // Pass 2: uniform size = the smallest natural fit on this label.
        double uniformSize = PinNameSize;
        foreach (var plan in rowPlans)
            uniformSize = Math.Min(uniformSize, plan.Size);

        foreach (var plan in rowPlans)
        {
            DrawPinText(ops, font, plan.Left, uniformSize, x, yTop, yBottom,
                plan.CentreY, width, plan.Inset, alignRight: false);
            DrawPinText(ops, font, plan.Right, uniformSize, x, yTop, yBottom,
                plan.CentreY, width, plan.Inset, alignRight: true);
        }
    }

    /// <summary>
    /// Draw one pin's name as one or two stacked lines, vertically centred
    /// on its row, with the whole block clamped inside the label border
    /// (cap height above the top baseline, slash descender below the bottom
    /// one, plus clearance). Lines left-align at the inset or right-align to
    /// the opposite edge.
    /// </summary>
    private static void DrawPinText(StringBuilder ops, LabelVectorFont font,
        string[] lines, double size, double x, double yTop, double yBottom,
        double centreY, double width, double inset, bool alignRight)
    {
        if (lines.Length == 0 || (lines.Length == 1 && lines[0].Length == 0)) return;

        double capTop = LabelVectorFont.CapHeightEm * size;
        double descender = LabelVectorFont.DescenderEm * size;
        double lineGap = lines.Length > 1 ? 0.35 * size : 0;

        // Baselines, top line first, centred as a block on the row.
        var baselines = new double[lines.Length];
        if (lines.Length == 1)
        {
            baselines[0] = centreY - capTop / 2;
        }
        else
        {
            baselines[0] = centreY + lineGap / 2;
            baselines[1] = centreY - lineGap / 2 - capTop;
        }

        // Clamp the block inside the border, shifting all lines together.
        double blockTop = baselines[0] + capTop;
        double blockBottom = baselines[baselines.Length - 1] - descender;
        double shift = 0;
        if (blockTop > yTop - BorderClearance) shift = (yTop - BorderClearance) - blockTop;
        if (blockBottom + shift < yBottom + BorderClearance)
            shift = (yBottom + BorderClearance) - blockBottom;

        foreach (var pair in lines.Select((text, i) => (text, i)))
        {
            if (pair.text.Length == 0) continue;
            double textX = alignRight
                ? x + width - inset - font.MeasureText(pair.text, size)
                : x + inset;
            font.AppendTextOps(ops, pair.text, textX, baselines[pair.i] + shift, size);
        }
    }

    /// <summary>
    /// Split a pin name at its first underscore into two lines, dropping the
    /// underscore ("IOADDR_SEL" -> "IOADDR", "SEL"). Names without an
    /// interior underscore stay as one line.
    /// </summary>
    private static string[] SplitAtUnderscore(string name)
    {
        int idx = name.IndexOf('_');
        if (idx <= 0 || idx >= name.Length - 1) return new[] { name };
        return new[] { name.Substring(0, idx), name.Substring(idx + 1) };
    }

    private static double MaxUnitWidth(LabelVectorFont font, string[] lines)
    {
        double max = 0;
        foreach (string line in lines)
            max = Math.Max(max, font.MeasureText(line, 1.0));
        return max;
    }

    private static string PrintableName(Dictionary<int, string> namesByNumber, int pinNumber)
    {
        if (!namesByNumber.TryGetValue(pinNumber, out string? name)) return string.Empty;
        return string.Equals(name, "NC", StringComparison.OrdinalIgnoreCase) ? string.Empty : name;
    }

    // ------------------------------------------------------------------ minimal PDF writer

    /// <summary>
    /// Assemble a multi-page PDF from per-page content streams. Object layout:
    /// 1 = catalog, 2 = pages tree, then alternating page / content objects.
    /// Plain uncompressed streams -- the content is small and this keeps the
    /// writer dependency-free and byte-deterministic.
    /// </summary>
    private static byte[] BuildPdf(List<StringBuilder> pages)
    {
        var objects = new List<string> { string.Empty };    // index 0 unused; objects are 1-based
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");   // obj 1
        objects.Add(string.Empty);                           // obj 2 placeholder (pages tree)

        var pageRefs = new List<string>();
        foreach (StringBuilder pageOps in pages)
        {
            string content = pageOps.ToString();
            int pageObj = objects.Count;
            int contentObj = pageObj + 1;
            pageRefs.Add(string.Format(CultureInfo.InvariantCulture, "{0} 0 R", pageObj));
            objects.Add(string.Format(CultureInfo.InvariantCulture,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}] /Contents {2} 0 R >>",
                LabelVectorFont.Fmt(PageWidth), LabelVectorFont.Fmt(PageHeight), contentObj));
            objects.Add(string.Format(CultureInfo.InvariantCulture,
                "<< /Length {0} >>\nstream\n{1}\nendstream",
                Encoding.ASCII.GetByteCount(content), content));
        }

        objects[2] = string.Format(CultureInfo.InvariantCulture,
            "<< /Type /Pages /Kids [{0}] /Count {1} >>",
            string.Join(" ", pageRefs), pages.Count);

        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 1; i < objects.Count; i++)
        {
            offsets[i] = Encoding.ASCII.GetByteCount(body.ToString());
            body.Append(i).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xrefOffset = Encoding.ASCII.GetByteCount(body.ToString());
        body.Append("xref\n0 ").Append(objects.Count).Append('\n')
            .Append("0000000000 65535 f \n");
        for (int i = 1; i < objects.Count; i++)
            body.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        body.Append("trailer\n<< /Size ").Append(objects.Count)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(body.ToString());
    }
}