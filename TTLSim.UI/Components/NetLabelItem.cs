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
/// "/CS0") -- unless another label in the schematic carries the same name
/// with a higher bit, in which case the digit stays (a lone A0 tap reads
/// "A0" while A[0..15] exists elsewhere; see <see cref="HigherBitsProbe"/>).
/// A width-1 tap of a higher bit keeps the digit ("D4"), and every
/// bus-port pin is always numbered. The per-bit names are the ONLY text drawn
/// -- there is no range-header designator. Display only -- the electrical tie
/// key is always (name, absolute bit).
/// </para>
///
/// <para>
/// Geometry: the drawn box is a fixed <see cref="BoxWidth"/> cells wide and
/// the PINS SIT ON ITS VERTICAL CENTRE COLUMN, so the rotation pivot (the box
/// centre, per <see cref="SchematicItem"/>) always lands on the pin column.
/// Height is EXACTLY width + 1: pins on rows 1..width with one unit of margin
/// at each end so the pivot row (width + 1) / 2 is an integer and lands on a
/// pin row (a width-1 label pivots about its blob; a wider port about its
/// middle pin). Nothing draws that box outline -- it exists only to size the
/// pivot -- so <see cref="Bounds"/> (hit-test) and <see cref="RoutingBounds"/>
/// (router keep-out) are built from the actual ink (blobs, stubs, bracket, and
/// the measured bit-name text) rather than the box, and so carry none of the
/// box's empty end margins. Long names extend the bounds to their real length.
/// </para>
///
/// <para>
/// <see cref="Mirrored"/> flips the label across its long axis: the stub,
/// bracket, and text move to the opposite side of the pin column and the pins'
/// facing direction flips. Because the pins live on the centre column, the
/// mirror toggle NEVER MOVES A PIN'S WORLD POSITION -- the EXISTING Pin
/// objects keep their location (only LocalDirection changes), so Connections
/// and router caches holding Pin references survive the toggle trivially.
/// <see cref="Color"/> renders the stubs, bracket, and bit names in a wire
/// palette colour so a label reads as part of the net it names.
/// </para>
///
/// <para>
/// Text rendering (all outside the rotation transform, anchored per pin to
/// WorldPosition): width-1 labels draw upright at every rotation, like the
/// oscillator's labels. Multi-bit ports draw upright names at 0/180 (one
/// name per pin row) but at 90/270 -- where the pin columns are one cell
/// apart and upright names can only collide -- each name draws ROTATED to
/// run along its stub, reading bottom-to-top on both facings.
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
    /// Fixed box width in cells. EVEN, and the pins sit at x = BoxWidth / 2 --
    /// the box's centre column -- so the rotation pivot is always on the pin
    /// column.
    /// </summary>
    private const int BoxWidth = 4;

    /// <summary>Connection-blob radius in cells (the 4-logical-unit terminal
    /// dot drawn at each pin), used to seed the hit-test bounds.</summary>
    private const float BlobRadiusCells = 0.4f;

    /// <summary>
    /// Cells a bit name is inset inward from its pin endpoint before the first
    /// glyph -- matches DrawText's <c>textInset = p + p * 0.30f</c> (one stub
    /// cell plus a 0.3-cell gap past the bracket), expressed in cells.
    /// </summary>
    private const float TextInsetCells = 1.30f;

    /// <summary>
    /// Logical units per grid cell. Must match the canvas grid pitch
    /// (<see cref="RenderContext.GridPitch"/>); text is measured in logical
    /// units and divided by this to get cells.
    /// </summary>
    private const float GridPitchCells = 5f;

    // Off-screen measuring context so bit-name extents can be computed in the
    // MODEL (Bounds / RoutingBounds), not just during paint -- the router then
    // sees text-aware bounds even before the first repaint. The font must
    // match RenderContext.PinFont, which is what DrawText renders bit names in.
    private static readonly Bitmap MeasureBitmap = new(1, 1);
    private static readonly Graphics MeasureGraphics = Graphics.FromImage(MeasureBitmap);
    private static readonly Font BitNameFont = new("Segoe UI", 2.75f);

    // Cached bit-name extents (in cells), indexed by pin number - 1, rebuilt
    // whenever anything that changes a name changes (see EnsureMetrics).
    private string? metricsSignature;
    private (float W, float H)[] bitCellSizes = Array.Empty<(float, float)>();

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

    /// <summary>
    /// Installed by <see cref="Schematic"/> alongside ConnectionProbe (and
    /// cleared with it): returns true when ANY net label in the schematic
    /// carries the given name with a top bit above 0 (StartBit + Width - 1
    /// &gt; 0). <see cref="BitName"/> consults it so a lone "/WE" tap shows
    /// its bare name while an "A0" tap keeps its digit whenever A1 or above
    /// exists anywhere under the same name. The scan deliberately IGNORES
    /// layer visibility: hiding the layer holding A[8..15] must not rename
    /// every A0 tap (and silently change exported net-name strings) --
    /// naming intent is schematic-wide even when ties are activity-filtered.
    /// Null (an item not yet in any schematic) suppresses the digit,
    /// matching the standalone default.
    /// </summary>
    [Browsable(false)]
    public Func<string, bool>? HigherBitsProbe { get; set; }

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
    /// Display name for one bit. A plain width-1 label starting at bit 0
    /// shows just its name ("/CS", never "/CS0") -- UNLESS another label in
    /// the schematic carries the same name with a higher bit, in which case
    /// the digit stays: a standalone A0 tap reads "A0" while A[0..15] exists
    /// elsewhere, so every rendering of the net (and every exported NET
    /// string, which EasyEDA fuses by name) agrees. A width-1 tap of a
    /// higher bit always keeps the digit ("D4"); every bus-port pin is
    /// always numbered ("D0".."D7"). An empty label shows the "?"
    /// placeholder (an empty label ties nothing, and the placeholder makes
    /// that visible). Display only -- the electrical tie key is always
    /// (name, absolute bit).
    /// </summary>
    public string BitName(int pinNumber)
    {
        string name = string.IsNullOrWhiteSpace(Label) ? "?" : Label;
        if (width == 1 && startBit == 0
            && (string.IsNullOrWhiteSpace(Label)
                || HigherBitsProbe is null
                || !HigherBitsProbe(Label)))
            return name;
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
    /// Box: fixed <see cref="BoxWidth"/> cells wide, pins on the centre
    /// column, height EXACTLY width + 1 (one unit of margin at each end so the
    /// pivot row stays an integer pin row -- see the class remarks). The box
    /// itself is never drawn and never bounds anything; <see cref="Bounds"/>
    /// and <see cref="RoutingBounds"/> measure the ink instead.
    /// </summary>
    private void ApplySizeForWidth()
    {
        Size = new Size(BoxWidth, width + 1);
    }

    // ------------------------------------------------------------------ bounds

    /// <summary>
    /// Rebuild the cached per-pin bit-name extents (in cells) when any input
    /// that affects a name changes. BitName(1) folds in the HigherBitsProbe
    /// decision, so it goes in the signature: if the probe flips a lone "A0"
    /// between "A" and "A0", the measured width follows.
    /// </summary>
    private void EnsureMetrics()
    {
        string signature = $"{Label}|{width}|{startBit}|{BitName(1)}";
        if (signature == metricsSignature && bitCellSizes.Length == width)
            return;

        metricsSignature = signature;
        bitCellSizes = new (float, float)[width];
        for (int n = 1; n <= width; n++)
        {
            SizeF px = MeasureGraphics.MeasureString(BitName(n), BitNameFont);
            bitCellSizes[n - 1] = (px.Width / GridPitchCells, px.Height / GridPitchCells);
        }
    }

    /// <summary>
    /// Hit-test rectangle: the tight extent of the DRAWN ink -- each pin's
    /// connection blob plus its bit name -- with no reliance on the pivot box,
    /// so it carries none of the box's empty end margins. Names are placed the
    /// same way <see cref="DrawText"/> places them (anchored to the pin's world
    /// position, inset inward along its facing), so the box tracks the text
    /// through every rotation and mirror. The one non-upright path (a multi-bit
    /// port at 90/270, whose names run rotated along the stub) is handled
    /// explicitly, matching DrawText.
    /// </summary>
    public override Rectangle Bounds
    {
        get
        {
            EnsureMetrics();

            float left = float.MaxValue, top = float.MaxValue;
            float right = float.MinValue, bottom = float.MinValue;

            void Include(float x0, float y0, float x1, float y1)
            {
                if (x0 < left) left = x0;
                if (y0 < top) top = y0;
                if (x1 > right) right = x1;
                if (y1 > bottom) bottom = y1;
            }

            foreach (var pin in Pins)
            {
                float gx = pin.WorldPosition.X;
                float gy = pin.WorldPosition.Y;

                // Connection blob at the pin endpoint.
                Include(gx - BlobRadiusCells, gy - BlobRadiusCells,
                        gx + BlobRadiusCells, gy + BlobRadiusCells);

                // Bit name, laid out exactly as DrawText places it.
                (float bw, float bh) = bitCellSizes[pin.Number - 1];
                bool vertical = pin.Direction is PinDirection.Up or PinDirection.Down;

                if (width > 1 && vertical)
                {
                    // Name runs ROTATED along the stub: bw tall, bh wide,
                    // centred across the pin column, on the inward side.
                    if (pin.Direction == PinDirection.Down)   // wire below, text above
                        Include(gx - bh / 2f, gy - TextInsetCells - bw,
                                gx + bh / 2f, gy - TextInsetCells);
                    else                                       // Up: wire above, text below
                        Include(gx - bh / 2f, gy + TextInsetCells,
                                gx + bh / 2f, gy + TextInsetCells + bw);
                }
                else
                {
                    // Upright name: extends inward from the pin, centred across
                    // the pin row/column -- the UprightTextOrigin cases.
                    switch (pin.Direction)
                    {
                        case PinDirection.Left:
                            Include(gx + TextInsetCells, gy - bh / 2f,
                                    gx + TextInsetCells + bw, gy + bh / 2f);
                            break;
                        case PinDirection.Right:
                            Include(gx - TextInsetCells - bw, gy - bh / 2f,
                                    gx - TextInsetCells, gy + bh / 2f);
                            break;
                        case PinDirection.Up:
                            Include(gx - bw / 2f, gy + TextInsetCells,
                                    gx + bw / 2f, gy + TextInsetCells + bh);
                            break;
                        default: // Down
                            Include(gx - bw / 2f, gy - TextInsetCells - bh,
                                    gx + bw / 2f, gy - TextInsetCells);
                            break;
                    }
                }
            }

            int L = (int)Math.Floor(left);
            int T = (int)Math.Floor(top);
            int R = (int)Math.Ceiling(right);
            int B = (int)Math.Ceiling(bottom);
            return Rectangle.FromLTRB(L, T, R, B);
        }
    }

    /// <summary>
    /// Router keep-out: the text-aware <see cref="Bounds"/> plus one cell of
    /// clearance on the wire-approach side (the way the pins face), so foreign
    /// wires don't hug the connection blobs. Every pin faces the same way, so
    /// pin 1's direction is the label's. No end margins -- the wire side is the
    /// only side widened past the ink.
    /// </summary>
    public override Rectangle RoutingBounds
    {
        get
        {
            var b = Bounds;
            if (Pins.Count == 0) return b;

            return Pins[0].Direction switch
            {
                PinDirection.Left => Rectangle.FromLTRB(b.Left - 1, b.Top, b.Right, b.Bottom),
                PinDirection.Right => Rectangle.FromLTRB(b.Left, b.Top, b.Right + 1, b.Bottom),
                PinDirection.Up => Rectangle.FromLTRB(b.Left, b.Top - 1, b.Right, b.Bottom),
                _ => Rectangle.FromLTRB(b.Left, b.Top, b.Right, b.Bottom + 1),
            };
        }
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
    /// All text, drawn screen-aligned outside the rotation transform: one
    /// bit name per pin, anchored inward of the pin's WorldPosition just
    /// past the bracket. On a MULTI-BIT port whose pins face Up/Down (the
    /// 90/270 orientations) each name is drawn ROTATED to run along its
    /// stub, reading bottom-to-top -- upright names at one-cell pin pitch
    /// can only collide. Width-1 labels keep upright text at every
    /// rotation, and Left/Right-facing ports were never at risk (one name
    /// per row). The per-bit names are the only text a label carries -- no
    /// range-header designator is drawn.
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
            bool vertical = pin.Direction is PinDirection.Up or PinDirection.Down;

            if (width > 1 && vertical)
            {
                // Along-the-stub name on a sideways bus port. Rotate the
                // graphics -90 about the pin so the baseline runs UP the
                // screen (text reads bottom-to-top, both facings, matching
                // the vertical-label convention EasyEDA itself uses). In
                // the rotated frame the offsets are exactly the upright
                // Left/Right cases: text extends inward from the inset,
                // centred across the stub.
                //   Pin faces Up   (wire above, text below): local x from
                //     -(inset + width) to -inset -- nearest character ends
                //     at the inset below the pin.
                //   Pin faces Down (wire below, text above): local x from
                //     +inset upward.
                float tx = pin.Direction == PinDirection.Up
                    ? -textInset - bitSize.Width
                    : textInset;

                var state = g.Save();
                g.TranslateTransform(pin.WorldPosition.X * p, pin.WorldPosition.Y * p);
                g.RotateTransform(-90f);
                g.DrawString(bit, ctx.PinFont, textBrush, tx, -bitSize.Height / 2f);
                g.Restore(state);
                continue;
            }

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