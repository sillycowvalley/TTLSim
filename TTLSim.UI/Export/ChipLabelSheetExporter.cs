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
    private const double PinNameFloor = 2.4;         // shrink-to-fit lower bound
    private const double PinNameStep = 0.2;
    private const double EdgeInset = 1.2;            // name to label edge
    private const double CentreGap = 2.0;            // min gap between the two columns
    private const double BorderClearance = 0.55;     // 0.2 mm glyph-to-border clearance
    private const double PartNumberGray = 0.78;      // light gray, behind the pin names
    private const double PartNumberWidthFraction = 0.62;
    private const double PartNumberLengthFraction = 0.88;

    // ---- sheet layout ----------------------------------------------------
    private const double GapInsideGroup = 8.0;       // between duplicate labels
    private const double GapBetweenGroups = 18.0;
    private const double CaptionSize = 6.0;
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

        // ---- BOM: group labelable devices by displayed part number -------
        // Labelable = box chips (ChipPartDefinition) in a DIP package.
        // TO-92 parts (DS1813) have no flat top to stick a label on.
        // Passives, headers, and standalone items are not chips.
        var groups = schematic.Devices
            .Where(d => d.Definition is ChipPartDefinition { To92: false })
            .GroupBy(d => d.FullPartNumber)
            .Select(g => new LabelGroup(
                g.Key,
                (ChipPartDefinition)g.First().Definition,
                g.Count()))
            .OrderBy(g => g.PartName, StringComparer.OrdinalIgnoreCase)
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
                ? group.PartName
                : string.Format(CultureInfo.InvariantCulture, "{0} x {1}", group.Count, group.PartName);

            for (int i = 0; i < group.Count; i++)
            {
                (double x, double slotTop, bool startedRow) = layout.Place(width, slotHeight);

                // Caption above the first label of the group, and again after
                // any row/page wrap so a split group stays identified.
                if (i == 0 || startedRow)
                {
                    layout.Ops.Append("0 0 0 rg\n");
                    font.AppendTextOps(layout.Ops, caption, x, slotTop - CaptionSize, CaptionSize);
                }

                DrawLabel(layout.Ops, font, group.Definition, group.PartName,
                    x, slotTop - CaptionSize - CaptionGap);
                labelCount++;
            }

            layout.EndGroup();
        }

        // ---- write the PDF -------------------------------------------------
        File.WriteAllBytes(filePath, BuildPdf(layout.Pages));
        return labelCount;
    }

    private sealed record LabelGroup(string PartName, ChipPartDefinition Definition, int Count);

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

        public void EndGroup() => x += GapBetweenGroups - GapInsideGroup;

        private void StartShelf()
        {
            shelfTop -= shelfHeight + ShelfGap;
            x = Margin;
            shelfHeight = 0;
        }

        private void StartPage()
        {
            Ops = new StringBuilder("0 0 0 rg\n0 0 0 RG\n");
            Pages.Add(Ops);
            x = Margin;
            shelfTop = PageHeight - Margin;
            shelfHeight = 0;
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
        ChipPartDefinition chip, string partName, double x, double yTop)
    {
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
        var namesByNumber = chip.Pins.ToDictionary(p => p.Number, p => p.Name);
        double pinSpan = (rows - 1) * RowPitch;
        double firstRowCentreY = yTop - (height - pinSpan) / 2;

        for (int row = 0; row < rows; row++)
        {
            double centreY = firstRowCentreY - row * RowPitch;
            string left = PrintableName(namesByNumber, row + 1);
            string right = PrintableName(namesByNumber, chip.PinCount - row);
            if (left.Length == 0 && right.Length == 0) continue;

            // Per-row shrink-to-fit.
            double size = PinNameSize;
            while (size > PinNameFloor &&
                   font.MeasureText(left, size) + font.MeasureText(right, size)
                       + 2 * EdgeInset + CentreGap > width)
            {
                size -= PinNameStep;
            }

            // Vertical centring, then the end-row border clamp: cap height
            // above the baseline, slash descender below, plus clearance.
            double baseline = centreY - size * LabelVectorFont.CapHeightEm / 2;
            double capTop = LabelVectorFont.CapHeightEm * size;
            double descender = LabelVectorFont.DescenderEm * size;
            if (baseline + capTop > yTop - BorderClearance)
                baseline = yTop - BorderClearance - capTop;
            if (baseline - descender < yBottom + BorderClearance)
                baseline = yBottom + BorderClearance + descender;

            if (left.Length > 0)
                font.AppendTextOps(ops, left, x + EdgeInset, baseline, size);
            if (right.Length > 0)
                font.AppendTextOps(ops, right,
                    x + width - EdgeInset - font.MeasureText(right, size), baseline, size);
        }
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