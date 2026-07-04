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
/// </para>
///
/// <para>
/// <see cref="Mirrored"/> flips the label across its long axis, exactly like
/// the header's Mirrored: pins exit from the opposite (right) edge, pin order
/// and bit numbering unchanged, and the EXISTING Pin objects are relocated in
/// place (Pin.LocalPosition / LocalDirection are settable) so Connections and
/// router caches holding Pin references survive the toggle.
/// <see cref="Color"/> renders the stubs, bracket, and bit names in a wire
/// palette colour so a label reads as part of the net it names.
/// </para>
///
/// <para>
/// Pin identity rules (same as the header's Mirrored feature): pins are added,
/// relocated, or kept -- never destroyed while a Connection may reference
/// them. Growing Width appends fresh pins on the current pin edge. Shrinking
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
                ApplyPinGeometry();   // new pins onto the current (mirrored?) edge
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
    /// Flip the label across its long axis: pins exit from the opposite
    /// (right) edge of the body, with the bracket and text mirrored to
    /// match. Pin order and bit numbering are unchanged -- this is a
    /// drawing-side convenience, exactly like the header's Mirrored, so a
    /// label can sit on either side of the thing it taps. Edits go through
    /// the property grid's generic SetPropertyCommand path.
    /// </summary>
    [Category("Layout")]
    [DefaultValue(false)]
    [Description("Flip the label across its long axis so the pins exit from the opposite side. Pin order and bit numbering are unchanged.")]
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
    /// Display name for one bit: "D4", or "?4" when the label is empty (an
    /// empty label ties nothing, and the placeholder makes that visible).
    /// </summary>
    public string BitName(int pinNumber) =>
        $"{(string.IsNullOrWhiteSpace(Label) ? "?" : Label)}{BitOfPin(pinNumber)}";

    private Pin MakePin(int pinNumber) =>
        new(pinNumber.ToString(), pinNumber,
            new Point(mirrored ? Size.Width : 0, pinNumber),
            mirrored ? PinDirection.Right : PinDirection.Left);

    /// <summary>
    /// Place every pin on the edge selected by <see cref="Mirrored"/>:
    /// x = 0 facing Left when unmirrored, x = Size.Width facing Right when
    /// mirrored. Relocates the EXISTING Pin objects (never rebuilds them)
    /// so connections holding Pin references survive the toggle. Row (Y)
    /// placement is untouched -- pin order never changes.
    /// </summary>
    private void ApplyPinGeometry()
    {
        int x = mirrored ? Size.Width : 0;
        PinDirection dir = mirrored ? PinDirection.Right : PinDirection.Left;
        foreach (var pin in Pins)
        {
            pin.LocalPosition = new Point(x, pin.Number);
            pin.LocalDirection = dir;
        }
    }

    /// <summary>
    /// Bounding box: one stub cell on the pin side, text field on the other.
    /// Height is width + 1 rounded up to EVEN, the same rule as
    /// HeaderOutputUnit, so the rotation pivot lands on an integer cell.
    /// The box width is fixed, so mirroring never moves the box -- only
    /// which edge the pins sit on.
    /// </summary>
    private void ApplySizeForWidth()
    {
        int boxHeight = width + 1;
        if ((boxHeight & 1) != 0) boxHeight++;
        Size = new Size(6, boxHeight);
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

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        g.Restore(state);
    }

    private void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int leftX = Position.X * p;
        int rightX = (Position.X + Size.Width) * p;

        // Pin-endpoint side and bracket column depend on Mirrored; the box
        // itself never moves.
        int pinOuterX = mirrored ? rightX : leftX;
        int bracketX = mirrored ? rightX - p : leftX + p;
        // Serifs and the range header extend TOWARD the pins.
        int towardPins = mirrored ? +1 : -1;

        Color ink = Selected ? ctx.SelectedColor : Color.ToColor();
        using var pen = new Pen(ink, 1.2f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        using var textBrush = new SolidBrush(ink);

        float dotRadius = p * 0.30f;
        float textInsetX = p * 0.30f;

        for (int pinNumber = 1; pinNumber <= width; pinNumber++)
        {
            int rowY = (Position.Y + pinNumber) * p;

            // Stub from the pin endpoint to the bracket column, terminal dot
            // at the outer end -- the same visual grammar as every other pin.
            g.DrawLine(pen, pinOuterX, rowY, bracketX, rowY);
            g.FillEllipse(pinBrush, pinOuterX - 2, rowY - 2, 4, 4);

            // Per-bit name on the text side of the bracket ("D4"): to the
            // right of it when unmirrored, right-aligned to its left when
            // mirrored. Drawn inside the rotation transform, like the
            // header's pin numbers, so it follows the body.
            string bit = BitName(pinNumber);
            var bitSize = g.MeasureString(bit, ctx.PinFont);
            float bitX = mirrored
                ? bracketX - textInsetX - bitSize.Width
                : bracketX + textInsetX;
            g.DrawString(bit, ctx.PinFont, textBrush,
                bitX,
                rowY - bitSize.Height / 2f);

            // Sim-mode per-bit state dot on the far (text) side, matching
            // the header's per-pin indicator.
            Signal? sig = ctx.SignalStateProvider?.Invoke(this, pinNumber);
            if (sig is Signal s)
            {
                float dotX = mirrored
                    ? leftX + p * 0.55f
                    : rightX - p * 0.55f;
                using var dotBrush = new SolidBrush(SignalColors.For(s));
                g.FillEllipse(dotBrush,
                    dotX - dotRadius, rowY - dotRadius,
                    dotRadius * 2, dotRadius * 2);
            }
        }

        // Bracket: for a bus port (width > 1), a vertical bar spanning the
        // pin rows with half-cell serifs toward the pins, plus the range
        // header ("D[4..7]") above the bar. A width-1 label needs neither.
        if (width > 1)
        {
            int topRowY = (Position.Y + 1) * p;
            int bottomRowY = (Position.Y + width) * p;
            int serifX = bracketX + towardPins * (p / 2);
            g.DrawLine(pen, bracketX, topRowY - p / 2, bracketX, bottomRowY + p / 2);
            g.DrawLine(pen, bracketX, topRowY - p / 2, serifX, topRowY - p / 2);
            g.DrawLine(pen, bracketX, bottomRowY + p / 2, serifX, bottomRowY + p / 2);

            string name = string.IsNullOrWhiteSpace(Label) ? "?" : Label;
            string range = $"{name}[{startBit}..{startBit + width - 1}]";
            var rangeSize = g.MeasureString(range, ctx.LabelFont);
            float rangeX = mirrored
                ? bracketX + p / 2f - rangeSize.Width
                : bracketX - p / 2f;
            g.DrawString(range, ctx.LabelFont, textBrush,
                rangeX,
                topRowY - p / 2f - rangeSize.Height);
        }
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