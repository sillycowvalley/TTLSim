using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TTLSim.Core;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Simulation-only stimulus source: a standalone symbol like VCC/GND/CLK, but
/// with one named pin per column of a loaded CSV program. During a run the
/// program is stepped one row per period of <see cref="FrequencyHz"/>, driving
/// H, L or Z onto each pin. See <see cref="TestbenchProgram"/> for the file
/// format.
///
/// <para>Visually a box with the pins down the right edge, names inside, the
/// item's Label above it and the row rate below -- the same idiom as
/// <see cref="ClockSource"/>, scaled to N pins.</para>
///
/// <para>NOT EXPORTABLE. This part exists only inside the simulator; it has no
/// physical counterpart, so an EasyEDA export containing a visible testbench
/// fails by design (see EasyEDACatalogue.LookupForStandaloneItem). Put one on
/// its own layer and hide the layer to export the board it was testing.</para>
///
/// <para>PIN IDENTITY. A pin's NUMBER is its permanent identity: connections
/// persist as (item id, pin number), so a number is allocated once when the
/// column first appears and is never reused or reassigned. Reloading a program
/// matches columns to existing pins BY NAME -- so inserting, appending or
/// reordering columns leaves every existing wire attached to the right signal.
/// Because numbers are allocation-ordered while pins are DISPLAYED in column
/// order, the mapping is not derivable from the file and is persisted with the
/// item (see PinMap in the .ttlproj format).</para>
///
/// <para>REMOVING A COLUMN. Following the net label's rule -- pins are never
/// destroyed while a Connection may reference them -- a reload whose file drops
/// a column that still has a wire on it is REFUSED whole, naming the columns at
/// fault. It is also refused when the check cannot be made (no connection probe
/// installed), which fails safe. Delete the wires deliberately, or put the
/// column back.</para>
/// </summary>
public sealed class TestbenchItem : SchematicItem
{
    /// <summary>Body width in grid cells. Wider than a chip box because column
    /// names are free text, not 3-4 character pin names.</summary>
    private const int BodyWidth = 12;

    /// <summary>Vertical cells between adjacent pins, matching ChipUnit so a
    /// testbench lines up with the chips it is wired to.</summary>
    private const int PinPitch = 2;

    /// <summary>Cells of body above the first pin and below the last.</summary>
    private const int VerticalMargin = 2;

    /// <summary>Height used when no program is loaded and there are no pins.</summary>
    private const int EmptyHeight = 6;

    private string? program;
    private TestbenchProgram? parsed;
    private int nextPinNumber = 1;

    public TestbenchItem()
    {
        ApplySizeForPins();
    }

    // ------------------------------------------------------------ properties

    /// <summary>
    /// Row rate: the program advances one row per period. 1 kHz means a new row
    /// of stimulus every millisecond of simulated time. Accepts free-form units
    /// on edit, as the clock source does.
    /// </summary>
    [Category("Signal")]
    [TypeConverter(typeof(FrequencyConverter))]
    [Description("Rate at which the program advances, one row per period. Accepts units like Hz, kHz, MHz.")]
    public double FrequencyHz { get; set; } = 1_000_000.0;

    /// <summary>
    /// The loaded CSV, embedded in the .ttlproj exactly as an EEPROM's Intel HEX
    /// image is, so a project file carries its own stimulus and runs anywhere.
    /// Assign through <see cref="TryLoadProgram"/>; the setter here exists for
    /// the persistence layer, which restores an already-validated program along
    /// with its pin map.
    /// </summary>
    [Browsable(false)]
    public string? Program
    {
        get => program;
        set
        {
            program = value;
            parsed = null;
            if (!string.IsNullOrWhiteSpace(value))
            {
                try { parsed = TestbenchProgram.Parse(value); }
                catch (FormatException) { parsed = null; }
            }
        }
    }

    /// <summary>Where the program was last loaded from. Convenience only -- the
    /// program itself lives in the file, so a missing or moved source path
    /// never stops a project running.</summary>
    [Category("Program")]
    [ReadOnly(true)]
    [Description("File the program was last loaded from. Not saved with the project -- the program itself is, so a moved or missing source file never stops a project running.")]
    public string SourcePath { get; set; } = "";

    [Category("Program")]
    [ReadOnly(true)]
    [Description("Number of columns (pins) in the loaded program.")]
    public int Columns => parsed?.ColumnCount ?? 0;

    [Category("Program")]
    [ReadOnly(true)]
    [Description("Number of stimulus rows in the loaded program.")]
    public int Rows => parsed?.RowCount ?? 0;

    /// <summary>
    /// "Is this pin an endpoint of any Connection in the owning schematic?"
    /// Installed by <see cref="Schematic"/> when the item is added. Null means
    /// the item is not in a schematic (or was added through a path that missed
    /// the install), and every column-removing reload is then refused -- fail
    /// safe, exactly as the net label's width shrink does.
    /// </summary>
    [Browsable(false)]
    public Func<Pin, bool>? ConnectionProbe { get; set; }

    /// <summary>The parsed program, or null when none is loaded.</summary>
    [Browsable(false)]
    public TestbenchProgram? Parsed => parsed;

    /// <summary>
    /// Pins in PROGRAM COLUMN ORDER. <see cref="SchematicItem.Pins"/> is kept
    /// in this order by <see cref="TryLoadProgram"/>, so column i of the
    /// program belongs to Pins[i] -- which is what lets the build input hand
    /// the simulator a plain list of pin numbers.
    /// </summary>
    [Browsable(false)]
    public IReadOnlyList<Pin> ColumnPins => Pins;

    // ------------------------------------------------------------ program load

    /// <summary>
    /// Validate and adopt a program, reconciling pins by column name.
    /// Returns false with a human-readable <paramref name="error"/> and leaves
    /// the item COMPLETELY unchanged when the text is malformed, or when the
    /// new column set would destroy a pin that still carries a wire.
    /// </summary>
    public bool TryLoadProgram(string csv, string? sourcePath, out string error)
    {
        TestbenchProgram next;
        try
        {
            next = TestbenchProgram.Parse(csv);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        // Columns that exist now but not in the new file. Destroying one of
        // these pins would leave a Connection pointing at a pin that is no
        // longer on any item.
        var keep = new HashSet<string>(next.ColumnNames, StringComparer.Ordinal);
        var doomed = Pins.Where(p => !keep.Contains(p.Name)).ToList();

        var blocked = new List<string>();
        foreach (var pin in doomed)
            if (ConnectionProbe is null || ConnectionProbe(pin))
                blocked.Add(pin.Name);

        if (blocked.Count > 0)
        {
            error = ConnectionProbe is null
                ? "This testbench is not part of a schematic yet, so its wiring cannot be "
                  + "checked. Place it on the canvas before loading a program."
                : "The new program has no column for these wired pins: "
                  + string.Join(", ", blocked)
                  + ".\n\nRemove those wires first, or keep the columns in the file. "
                  + "Nothing has been changed.";
            return false;
        }

        // Past this point the load succeeds; mutate.
        foreach (var pin in doomed)
            Pins.Remove(pin);

        // Existing pins keep their numbers; genuinely new columns get fresh
        // ones. Pins are then reordered to match the file so column i is
        // Pins[i], and re-laid-out down the body.
        var byName = Pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var ordered = new List<Pin>(next.ColumnCount);
        foreach (string name in next.ColumnNames)
        {
            if (byName.TryGetValue(name, out Pin? existing))
            {
                ordered.Add(existing);
                continue;
            }

            var fresh = new Pin(name, nextPinNumber++, new Point(BodyWidth, 0), PinDirection.Right);
            AddPin(fresh);
            ordered.Add(fresh);
        }

        Pins.Clear();
        Pins.AddRange(ordered);

        program = csv;
        parsed = next;
        if (sourcePath is not null) SourcePath = sourcePath;

        ApplySizeForPins();
        ApplyPinGeometry();
        error = "";
        return true;
    }

    /// <summary>
    /// Restore pins from persisted state: the saved name-to-number map, in the
    /// saved order. Used by the loader ONLY -- it bypasses the wired-pin check
    /// because it is reconstructing an item, not editing one, and it preserves
    /// the numbers the connections in the same file refer to.
    /// </summary>
    public void RestorePins(IEnumerable<(string Name, int Number)> map)
    {
        Pins.Clear();
        nextPinNumber = 1;
        foreach (var (name, number) in map)
        {
            AddPin(new Pin(name, number, new Point(BodyWidth, 0), PinDirection.Right));
            if (number >= nextPinNumber) nextPinNumber = number + 1;
        }
        ApplySizeForPins();
        ApplyPinGeometry();
    }

    /// <summary>Name-to-number map in display (column) order, for persistence.</summary>
    public IEnumerable<(string Name, int Number)> PinMap() =>
        Pins.Select(p => (p.Name, p.Number));

    // ------------------------------------------------------------ geometry

    private void ApplySizeForPins()
    {
        int height = Pins.Count == 0
            ? EmptyHeight
            : VerticalMargin * 2 + (Pins.Count - 1) * PinPitch;
        Size = new Size(BodyWidth + 1, Math.Max(EmptyHeight, height));
    }

    /// <summary>
    /// Put every pin on the right edge, evenly pitched from the top margin
    /// down, in list (column) order. Relocates the EXISTING Pin objects rather
    /// than rebuilding them, so Connections holding pin references survive a
    /// reload that reorders columns.
    /// </summary>
    private void ApplyPinGeometry()
    {
        for (int i = 0; i < Pins.Count; i++)
        {
            Pins[i].LocalPosition = new Point(Size.Width, VerticalMargin + i * PinPitch);
            Pins[i].LocalDirection = PinDirection.Right;
        }
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            var unrotated = new Rectangle(
                Position.X - 1, Position.Y - 1,
                Size.Width + 2, Size.Height + 2);

            if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
                return unrotated;

            int cx = Position.X + Size.Width / 2;
            int cy = Position.Y + Size.Height / 2;
            int w = unrotated.Height;
            int h = unrotated.Width;
            return new Rectangle(cx - w / 2, cy - h / 2, w, h);
        }
    }

    // ------------------------------------------------------------ rendering

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        g.Restore(state);

        DrawLabels(g, ctx);
    }

    private void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int x = Position.X * p;
        int y = Position.Y * p;
        int w = Size.Width * p;
        int h = Size.Height * p;

        // Rightmost cell is the pin stub area; the body fills the rest.
        int bodyW = w - p;

        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;
        using var pen = new Pen(color, 1.2f);
        using var fillBrush = new SolidBrush(ctx.FillColor);
        using var pinBrush = new SolidBrush(color);
        using var textBrush = new SolidBrush(ctx.ForegroundColor);

        g.FillRectangle(fillBrush, x, y, bodyW, h);
        g.DrawRectangle(pen, x, y, bodyW, h);

        var tightFormat = StringFormat.GenericTypographic;
        const float nameInset = 0.3f;

        // A testbench with no program has no pins; say so rather than drawing
        // an empty box that looks like a rendering failure.
        if (Pins.Count == 0)
        {
            const string empty = "no program";
            var size = g.MeasureString(empty, ctx.PinFont, int.MaxValue, tightFormat);
            g.DrawString(empty, ctx.PinFont, textBrush,
                x + (bodyW - size.Width) / 2f, y + (h - size.Height) / 2f, tightFormat);
            return;
        }

        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;

        foreach (var pin in Pins)
        {
            int py = (Position.Y + pin.LocalPosition.Y) * p;

            // Stub from the body's right edge out to the pin dot.
            g.DrawLine(pen, x + bodyW, py, x + w, py);
            g.FillEllipse(pinBrush, x + w - 2, py - 2, 4, 4);

            // Name inside the body, hard against the pin's edge. Drawn here
            // (inside DrawShape) so the rotation transform carries it with the
            // body, as ChipUnit and ClockSource both do.
            var nameSize = g.MeasureString(pin.Name, ctx.PinFont, int.MaxValue, tightFormat);
            float nameX = x + bodyW - nameInset * p - nameSize.Width;
            float nameY = py - nameSize.Height / 2;
            g.DrawString(pin.Name, ctx.PinFont, textBrush, nameX, nameY, tightFormat);
        }

        g.SmoothingMode = prev;
    }

    private void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        using var brush = new SolidBrush(ctx.ForegroundColor);

        string title = string.IsNullOrWhiteSpace(Label) ? "TESTBENCH" : Label;
        string detail = parsed is null
            ? "no program"
            : $"{parsed.RowCount} rows @ {ClockSource.FormatFrequency(FrequencyHz)}";

        var titleSize = g.MeasureString(title, ctx.LabelFont);
        var detailSize = g.MeasureString(detail, ctx.LabelFont);

        // R0/R180: body horizontal, wires leave the side -- stack the two
        // lines above the body. R90/R270: body vertical, wires leave top or
        // bottom -- move the text to the left, clear of them.
        float tx, ty, dx, dy;
        if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
        {
            tx = b.X * p + (b.Width * p - titleSize.Width) / 2f;
            ty = b.Y * p - titleSize.Height - detailSize.Height - ctx.BodyGap;
            dx = b.X * p + (b.Width * p - detailSize.Width) / 2f;
            dy = b.Y * p - detailSize.Height - ctx.BodyGap;
        }
        else
        {
            tx = b.X * p - titleSize.Width - ctx.BodyGap;
            ty = b.Y * p + (b.Height * p - titleSize.Height) / 2f - titleSize.Height;
            dx = b.X * p - detailSize.Width - ctx.BodyGap;
            dy = b.Y * p + (b.Height * p - detailSize.Height) / 2f;
        }

        g.DrawString(title, ctx.LabelFont, brush, tx, ty);
        g.DrawString(detail, ctx.LabelFont, brush, dx, dy);
    }

    private void ApplyRotationTransform(Graphics g, RenderContext ctx)
    {
        if (Rotation == Rotation.R0) return;
        int p = ctx.GridPitch;
        float pivotX = Pivot.X * p;
        float pivotY = Pivot.Y * p;
        g.TranslateTransform(pivotX, pivotY);
        g.RotateTransform((float)(int)Rotation);
        g.TranslateTransform(-pivotX, -pivotY);
    }
}