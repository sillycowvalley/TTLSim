using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using TTLSim.Core;
using TTLSim.UI.Model;
using TTLSim.UI.View;

namespace TTLSim.UI.Components;

/// <summary>
/// Net label / bus port: a standalone item whose pins tie into named nets by
/// NAME rather than by a drawn wire. Pin k (1-based) carries bit
/// (StartBit + k - 1) of the net named by <see cref="SchematicItem.Label"/>.
/// Every active label pin with the same (name, bit) anywhere on the schematic
/// is one electrical net -- <see cref="Schematic.NetLabelTiePairs"/> yields the
/// synthetic pin pairs and every net-identity consumer (simulator build input,
/// wire router grouping and retry, canvas coincident-corner detection) unions
/// them exactly as if wires had been drawn.
///
/// <para>
/// Width 1 is a plain net label ("CLK"). Width N is a bus port: N stubs with
/// per-bit annotations, so D[0..7] at the RAM and D[0..3] / D[4..7] elsewhere
/// tie bit-for-bit with no wires between them -- ranges may overlap freely and
/// a width-1 port is a single-bit tap. An EMPTY label ties nothing (drawn with
/// a "?" placeholder), so freshly dropped labels can never silently short.
/// A width-1 label with StartBit 0 displays its bare name ("/CS", never
/// "/CS0"); a width-1 tap of a higher bit keeps the digit ("D4"), and every
/// bus-port pin is always numbered. Display only -- the electrical tie key is
/// always (name, absolute bit).
/// </para>
///
/// <para>
/// Geometry: the bounding box is a fixed <see cref="BoxWidth"/> cells wide and
/// the PINS SIT ON ITS VERTICAL CENTRE COLUMN, so the rotation pivot (the box
/// centre, per <see cref="SchematicItem"/>) always lands on the pin column. A
/// width-1 label therefore rotates exactly about its connection blob; a wider
/// bus port rotates about the middle of its pin column. Height is width + 1
/// rounded up to EVEN (the HeaderOutputUnit rule) so the pivot stays on an
/// integer cell. The stub, bracket, and text extend one side of the pin
/// column; long bit names may overhang the box -- a cosmetic overhang, like
/// the capacitor's plates. RoutingBounds is the default (the visual Bounds),
/// so the router keep-out -- and the debug pink -- hugs the symbol.
/// </para>
///
/// <para>
/// <see cref="Mirrored"/> flips the label across its long axis: the stub,
/// bracket, and text swap to the opposite side of the pin column and the pins'
/// facing direction flips. Because the pins live on the centre column, the
/// mirror toggle NEVER MOVES A PIN'S WORLD POSITION -- the EXISTING Pin
/// objects keep their location (only LocalDirection changes), so Connections
/// and router caches holding Pin references survive the toggle trivially.
/// <see cref="Color"/> renders the stubs, bracket, and bit names in a wire
/// palette colour so a label reads as part of the net it names.
/// </para>
///
/// <para>
/// All text is drawn UPRIGHT at every rotation: bit names render in a
/// screen-aligned pass (outside the rotation transform, like the oscillator's
/// labels), each anchored to its pin's WorldPosition and extending inward
/// past the bracket. The range header ("D[4..7]") is centred above the
/// rotated extent -- which, with the pins on the centre column, means centred
/// on the pin column. Note that at 90/270 the pin columns are one cell apart,
/// so long bit names on a wide rotated port will overlap horizontally --
/// inherent to upright text at that pitch; keep rotated ports' names short or
/// the port at 0/180.
/// </para>
///
/// <para>
/// Pin identity rules (same as the header's Mirrored feature): pins are added,
/// relocated, or kept -- never destroyed while a Connection may reference
/// them. Growing Width appends fresh pins on the centre column. Shrinking
/// Width removes the doomed trailing pins ONLY if none of them is wired; the
/// check runs through <see cref="ConnectionProbe"/>, installed by
/// <see cref="Schematic"/> when the item enters the model. No probe = cannot
/// verify = the shrink is REFUSED (fail safe). A refused property-grid edit
/// reads back unchanged, so the recorded SetPropertyCommand is a harmless
/// no-op.
/// </para>
///
/// <para>
/// COMPATIBILITY NOTE: files written when the box was 6 cells wide with pins
/// on the outer edges load fine (Position semantics are unchanged), but each
/// label's pin blobs move relative to Position -- 2 cells inward for an
/// unmirrored label, 4 cells inward for a mirrored one. Connections are
/// logical (pin references), so wires simply reroute; the labels themselves
/// may want a one-time nudge.
/// </para>
/// </summary>
public sealed class NetLabelItem : SchematicItem
{
    public const int MinWidth = 1;
    public const int MaxWidth = 16;

    /// <summary>
    /// Fixed bounding-box width in cells. EVEN, and the pins sit at
    /// x = BoxWidth / 2 -- the box's centre column -- so the rotation pivot
    /// is always on the pin column.
    /// </summary>
    private const int BoxWidth = 4;

    private int width;
    private int startBit;
    private bool mirrored;

    /// <summary>
    /// Installed by <see cref="Schematic"/> when this item is added to the
    /// model (and cleared on removal): returns true when the given pin is an
    /// endpoint of any Connection. Null means "unknown" and makes Width
    /// shrinks refuse rather than risk orphaning a wired pin.
    /// </summary>
    [Browsable(false)]
    public Func<Pin, bool>? ConnectionProbe { get; set; }

    public NetLabelItem()
    {
        Label = "";
        width = 1;
        ApplySizeForWidth();
        AddPin(MakePin(1));
    }

    /// <summary>
    /// Bit carried by pin 1. Pin k carries StartBit + k - 1, so a D[4..7]
    /// port is Label "D", StartBit 4, Width 4. Purely a naming property --
    /// no pin geometry changes.
    /// </summary>
    [Category("Bus")]
    [DefaultValue(0)]
    [Description("Bit number carried by pin 1. Pin k carries StartBit + k - 1, so a D[4..7] port is Label \"D\", StartBit 4, Width 4.")]
    public int StartBit
    {
        get => startBit;
        set => startBit = Math.Max(0, Math.Min(255, value));
    }

    /// <summary>
    /// Number of bits (pins). Growing appends fresh pins. Shrinking removes
    /// the trailing pins only when none of them is wired; otherwise the edit
    /// is refused and the value is unchanged (fail safe when the connection
    /// probe is absent).
    /// </summary>
    [Category("Bus")]
    [DefaultValue(1)]
    [Description("Number of bits (1-16). Width 1 is a plain net label; larger widths are a bus port. Shrinking is refused while a to-be-removed pin is wired.")]
    public int Width
    {
        get => width;
        set
        {
            int target = Math.Max(MinWidth, Math.Min(MaxWidth, value));
            if (target == width) return;

            if (target > width)
            {
                for (int pinNumber = width + 1; pinNumber <= target; pinNumber++)
                    AddPin(MakePin(pinNumber));
                width = target;
                ApplySizeForWidth();
                ApplyPinGeometry();   // new pins onto the centre column, current facing
                return;
            }

            // Shrink: refuse if any doomed pin is wired, or if we cannot
            // verify (no probe installed). Pins are never destroyed while a
            // Connection may reference them.
            var doomed = new List<Pin>();
            foreach (var pin in Pins)
                if (pin.Number > target)
                    doomed.Add(pin);

            foreach (var pin in doomed)
            {
                if (ConnectionProbe is null || ConnectionProbe(pin))
                {
                    System.Media.SystemSounds.Beep.Play();
                    return;
                }
            }

            foreach (var pin in doomed)
                Pins.Remove(pin);
            width = target;
            ApplySizeForWidth();
        }
    }

    /// <summary>
    /// Flip the label across its long axis: the stub, bracket, and text move
    /// to the opposite side of the pin column and the pins face the other
    /// way. Pin order, bit numbering, and -- because the pins live on the
    /// box's centre column -- pin WORLD POSITIONS are all unchanged, so a
    /// label can sit on either side of the thing it taps without disturbing
    /// its wires. Edits go through the property grid's generic
    /// SetPropertyCommand path.
    /// </summary>
    [Category("Layout")]
    [DefaultValue(false)]
    [Description("Flip the label across its long axis so the pins face the opposite side. Pin order, bit numbering, and pin positions are unchanged.")]
    public bool Mirrored
    {
        get => mirrored;
        set
        {
            if (mirrored == value) return;
            mirrored = value;
            ApplyPinGeometry();
        }
    }

    /// <summary>
    /// Render colour for the stubs, bracket, and bit names, from the wire
    /// palette -- so a label carries the same colour convention as the net
    /// it names. Changes go through the undo stack via the PropertyGrid's
    /// generic SetPropertyCommand path, the same way a wire's Color does.
    /// </summary>
    [Category("Appearance")]
    [DefaultValue(TTLColor.Black)]
    [Description("Render colour for the label's stubs and text, mirroring jumper-wire conventions.")]
    public TTLColor Color { get; set; } = TTLColor.Black;

    /// <summary>Absolute bit number carried by the given pin number.</summary>
    public int BitOfPin(int pinNumber) => startBit + pinNumber - 1;

    /// <summary>
    /// Display name for one bit. A plain width-1 label starting at bit 0 is
    /// just its name ("/CS", never "/CS0"); a width-1 tap of a higher bit
    /// keeps the digit ("D4"); every bus-port pin is numbered ("D0".."D7").
    /// An empty label shows the "?" placeholder (an empty label ties nothing,
    /// and the placeholder makes that visible). Display only -- the
    /// electrical tie key is always (name, absolute bit).
    /// </summary>
    public string BitName(int pinNumber)
    {
        string name = string.IsNullOrWhiteSpace(Label) ? "?" : Label;
        if (width == 1 && startBit == 0) return name;
        return $"{name}{BitOfPin(pinNumber)}";
    }

    /// <summary>Cells from the box's left edge to the pin column: the box's
    /// vertical centre column, which is also the rotation pivot column.</summary>
    private int PinColumn => Size.Width / 2;

    private Pin MakePin(int pinNumber) =>
        new(pinNumber.ToString(), pinNumber,
            new Point(PinColumn, pinNumber),
            mirrored ? PinDirection.Right : PinDirection.Left);

    /// <summary>
    /// Put every pin on the centre column facing the side selected by
    /// <see cref="Mirrored"/>. Relocates the EXISTING Pin objects (never
    /// rebuilds them) so connections holding Pin references survive the
    /// toggle. Row (Y) placement is untouched -- pin order never changes --
    /// and since the column is fixed, a mirror toggle changes only the
    /// facing direction.
    /// </summary>
    private void ApplyPinGeometry()
    {
        PinDirection dir = mirrored ? PinDirection.Right : PinDirection.Left;
        foreach (var pin in Pins)
        {
            pin.LocalPosition = new Point(PinColumn, pin.Number);
            pin.LocalDirection = dir;
        }
    }

    /// <summary>
    /// Bounding box: fixed <see cref="BoxWidth"/> cells wide, pins on the
    /// centre column. Height is width + 1 rounded up to EVEN, the same rule
    /// as HeaderOutputUnit, so the rotation pivot lands on an integer cell --
    /// and, with the pins centred, ON THE PIN COLUMN: a width-1 label pivots
    /// exactly about its connection blob. The box never moves when Mirrored
    /// toggles -- only which side the stub and text sit on.
    /// </summary>
    private void ApplySizeForWidth()
    {
        int boxHeight = width + 1;
        if ((boxHeight & 1) != 0) boxHeight++;
        Size = new Size(BoxWidth, boxHeight);
    }

    // RoutingBounds is deliberately NOT overridden: the default (the visual
    // Bounds) is already the right keep-out. The box spans two cells either
    // side of the pin column, so wires get clearance from the stubs without
    // any extra inflation -- and the debug pink hugs the symbol.

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        g.Restore(state);

        DrawText(g, ctx);
    }

    /// <summary>
    /// Geometry only (stubs, terminal dots, bracket, sim-state dots) --
    /// drawn inside the rotation transform. Text renders upright in
    /// <see cref="DrawText"/> after the transform is restored.
    /// </summary>
    private void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;

        // Pins sit on the box's centre column. The bracket column sits one
        // cell INWARD of it -- on the text side, selected by Mirrored -- and
        // the bracket serifs extend back TOWARD the pins.
        int pinX = (Position.X + PinColumn) * p;
        int inward = mirrored ? -1 : +1;
        int bracketX = pinX + inward * p;
        int towardPins = -inward;

        Color ink = Selected ? ctx.SelectedColor : Color.ToColor();
        using var pen = new Pen(ink, 1.2f);
        using var pinBrush = new SolidBrush(ctx.PinColor);

        float dotRadius = p * 0.30f;

        for (int pinNumber = 1; pinNumber <= width; pinNumber++)
        {
            int rowY = (Position.Y + pinNumber) * p;

            // Stub from the pin endpoint to the bracket column, terminal dot
            // at the pin endpoint -- the same visual grammar as every other
            // pin.
            g.DrawLine(pen, pinX, rowY, bracketX, rowY);
            g.FillEllipse(pinBrush, pinX - 2, rowY - 2, 4, 4);

            // Sim-mode per-bit state dot drawn ON the pin blob, so the state
            // reads exactly where the wire lands (it fully covers the black
            // terminal dot while the sim is running).
            Signal? sig = ctx.SignalStateProvider?.Invoke(this, pinNumber);
            if (sig is Signal s)
            {
                using var dotBrush = new SolidBrush(SignalColors.For(s));
                g.FillEllipse(dotBrush,
                    pinX - dotRadius, rowY - dotRadius,
                    dotRadius * 2, dotRadius * 2);
            }
        }

        // Bracket: for a bus port (width > 1), a vertical bar spanning the
        // pin rows with half-cell serifs toward the pins. A width-1 label
        // needs neither. The range header renders upright in DrawText.
        if (width > 1)
        {
            int topRowY = (Position.Y + 1) * p;
            int bottomRowY = (Position.Y + width) * p;
            int serifX = bracketX + towardPins * (p / 2);
            g.DrawLine(pen, bracketX, topRowY - p / 2, bracketX, bottomRowY + p / 2);
            g.DrawLine(pen, bracketX, topRowY - p / 2, serifX, topRowY - p / 2);
            g.DrawLine(pen, bracketX, bottomRowY + p / 2, serifX, bottomRowY + p / 2);
        }
    }

    /// <summary>
    /// All text, drawn upright (screen-aligned, outside the rotation
    /// transform): one bit name per pin, anchored inward of the pin's
    /// WorldPosition just past the bracket, and, for a bus port, the range
    /// header centred above the rotated extent -- which, with the pins on
    /// the centre column, is centred on the pin column.
    /// </summary>
    private void DrawText(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        Color ink = Selected ? ctx.SelectedColor : Color.ToColor();
        using var textBrush = new SolidBrush(ink);

        // Bit names: one cell of stub plus a 0.3-cell inset past the
        // bracket, extending inward (opposite each pin's world direction).
        float textInset = p + p * 0.30f;
        foreach (var pin in Pins)
        {
            string bit = BitName(pin.Number);
            var bitSize = g.MeasureString(bit, ctx.PinFont);
            var origin = UprightTextOrigin(
                pin.WorldPosition, pin.Direction, textInset, bitSize, p);
            g.DrawString(bit, ctx.PinFont, textBrush, origin.X, origin.Y);
        }

        // Range header for a bus port, centred above the rotated visual
        // extent -- the oscillator-designator pattern, readable at every
        // rotation.
        if (width > 1)
        {
            string name = string.IsNullOrWhiteSpace(Label) ? "?" : Label;
            string range = $"{name}[{startBit}..{startBit + width - 1}]";
            var rangeSize = g.MeasureString(range, ctx.LabelFont);
            var b = Bounds;   // rotated extent, grid units
            float cx = (b.X + b.Width / 2f) * p;
            float topY = b.Y * p;
            g.DrawString(range, ctx.LabelFont, textBrush,
                cx - rangeSize.Width / 2f, topY - rangeSize.Height);
        }
    }

    /// <summary>
    /// Origin for an upright (screen-aligned) text box anchored to a pin:
    /// the box sits <paramref name="insetPx"/> pixels INWARD from the pin
    /// endpoint (opposite the pin's outward world direction) and is
    /// centred across the pin row/column.
    /// </summary>
    private static PointF UprightTextOrigin(
        Point world, PinDirection outward, float insetPx, SizeF text, int p)
    {
        float wx = world.X * p, wy = world.Y * p;
        return outward switch
        {
            PinDirection.Left => new PointF(wx + insetPx, wy - text.Height / 2f),
            PinDirection.Right => new PointF(wx - insetPx - text.Width, wy - text.Height / 2f),
            PinDirection.Up => new PointF(wx - text.Width / 2f, wy + insetPx),
            _ => new PointF(wx - text.Width / 2f, wy - insetPx - text.Height),
        };
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