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
/// bus-port pin is always numbered. The per-bit names are the ONLY text drawn
/// -- there is no range-header designator. Display only -- the electrical tie
/// key is always (name, absolute bit).
/// </para>
///
/// <para>
/// Geometry: the bounding box is a fixed <see cref="BoxWidth"/> cells wide and
/// the PINS SIT ON ITS VERTICAL CENTRE COLUMN, so the rotation pivot (the box
/// centre, per <see cref="SchematicItem"/>) always lands on the pin column.
/// Height is EXACTLY width + 1: pins on rows 1..width with the standard one
/// unit of margin at each end. No even-rounding -- the pivot row H / 2 =
/// (width + 1) / 2 is an integer for every width and always lands on a pin
/// row, so a width-1 label rotates exactly about its connection blob and a
/// wider port rotates about the middle pin of its column. (The
/// HeaderOutputUnit even-height rule is NOT needed here: it exists to centre
/// a pivot on a box whose pins hug one edge; with the pins already on the
/// centre column, rounding would only add a phantom extra pin row on
/// even widths.) The stub and text extend one side of the pin column; long
/// bit names may overhang the box -- a cosmetic overhang, like the
/// capacitor's plates.
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
/// past the bracket. Note that at 90/270 the pin columns are one cell apart,
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

    /// <summary>
    /// R0/R180 router keep-out on the wire-approach side of the pin column:
    /// one cell keeps foreign wires off the blobs without wasting space
    /// (the connecting wire's own corridor is carved by the router).
    /// </summary>
    private const int PinSideCells = 1;

    /// <summary>
    /// R0/R180 router keep-out on the TEXT side of the pin column: the stub
    /// plus room for bit names up to about three characters (names start
    /// 1.3 cells inward of the pin).
    /// </summary>
    private const int TextSideCells = 3;

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
    /// centre column. Height is EXACTLY width + 1 -- pins on rows 1..width
    /// with one unit of margin at each end, no even-rounding. The pivot row
    /// (width + 1) / 2 is always an integer and always a pin row: a width-1
    /// label pivots exactly about its connection blob. (Rounding the height
    /// up to even, as HeaderOutputUnit does, would add a phantom extra pin
    /// row on every EVEN width -- the header needs it because its pins hug
    /// one edge of the box; ours sit on the pivot column already.) The box
    /// never moves when Mirrored toggles -- only which side the stub and
    /// text sit on.
    /// </summary>
    private void ApplySizeForWidth()
    {
        Size = new Size(BoxWidth, width + 1);
    }

    /// <summary>
    /// Router keep-out, anchored to the pin column rather than the box. At
    /// R0/R180 it spans <see cref="PinSideCells"/> on the wire-approach side
    /// and <see cref="TextSideCells"/> on the text side (sides selected by
    /// Mirrored) -- tight against the blobs, just enough for three-character
    /// names. At R90/R270 the upright names form a single text row beside
    /// the pins, already inside the box's own margin, so the plain box is
    /// the whole footprint. The R0/R180 rect is ASYMMETRIC about the pivot,
    /// so R180 must map it through the pivot rather than reuse the unrotated
    /// rect; <see cref="RotateCell"/> matches Pin.WorldPosition's convention
    /// exactly.
    /// </summary>
    public override Rectangle RoutingBounds
    {
        get
        {
            bool sideways = Rotation == Rotation.R90 || Rotation == Rotation.R270;

            Rectangle unrotated;
            if (sideways)
            {
                unrotated = new Rectangle(
                    Position.X, Position.Y, Size.Width, Size.Height);
            }
            else
            {
                int pinX = Position.X + PinColumn;
                int left = mirrored
                    ? pinX - TextSideCells
                    : pinX - PinSideCells;
                unrotated = new Rectangle(
                    left, Position.Y,
                    PinSideCells + TextSideCells, Size.Height);
            }

            if (Rotation == Rotation.R0) return unrotated;

            Point a = RotateCell(unrotated.Left, unrotated.Top);
            Point b = RotateCell(unrotated.Right, unrotated.Bottom);
            return Rectangle.FromLTRB(
                Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
    }

    /// <summary>
    /// Rotate one grid point about the pivot by the item's current rotation,
    /// using the same clockwise convention as <see cref="Pin.WorldPosition"/>.
    /// </summary>
    private Point RotateCell(int x, int y)
    {
        int dx = x - Pivot.X;
        int dy = y - Pivot.Y;
        return Rotation switch
        {
            Rotation.R90 => new Point(Pivot.X - dy, Pivot.Y + dx),
            Rotation.R180 => new Point(Pivot.X - dx, Pivot.Y - dy),
            Rotation.R270 => new Point(Pivot.X + dy, Pivot.Y - dx),
            _ => new Point(x, y)
        };
    }

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
        // needs neither.
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
    /// WorldPosition just past the bracket. The per-bit names are the only
    /// text a label carries -- no range-header designator is drawn.
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