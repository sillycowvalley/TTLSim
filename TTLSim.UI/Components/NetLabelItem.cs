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
/// Pin identity rules (same as the header's Mirrored feature): pins are added
/// or kept, never destroyed while a Connection may reference them. Growing
/// Width appends fresh pins. Shrinking Width removes the doomed trailing pins
/// ONLY if none of them is wired; the check runs through
/// <see cref="ConnectionProbe"/>, installed by <see cref="Schematic"/> when the
/// item enters the model. No probe = cannot verify = the shrink is REFUSED
/// (fail safe). A refused property-grid edit reads back unchanged, so the
/// recorded SetPropertyCommand is a harmless no-op.
/// </para>
/// </summary>
public sealed class NetLabelItem : SchematicItem
{
    public const int MinWidth = 1;
    public const int MaxWidth = 16;

    private int width;
    private int startBit;

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

    /// <summary>Absolute bit number carried by the given pin number.</summary>
    public int BitOfPin(int pinNumber) => startBit + pinNumber - 1;

    /// <summary>
    /// Display name for one bit: "D4", or "?4" when the label is empty (an
    /// empty label ties nothing, and the placeholder makes that visible).
    /// </summary>
    public string BitName(int pinNumber) =>
        $"{(string.IsNullOrWhiteSpace(Label) ? "?" : Label)}{BitOfPin(pinNumber)}";

    private static Pin MakePin(int pinNumber) =>
        new(pinNumber.ToString(), pinNumber, new Point(0, pinNumber), PinDirection.Left);

    /// <summary>
    /// Bounding box: one stub cell on the left, text field to the right.
    /// Height is width + 1 rounded up to EVEN, the same rule as
    /// HeaderOutputUnit, so the rotation pivot lands on an integer cell.
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
        int leftX = Position.X * p;              // pin-endpoint column
        int bracketX = leftX + p;                // stub ends at the bracket

        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;
        using var pen = new Pen(color, 1.2f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        using var textBrush = new SolidBrush(ctx.ForegroundColor);

        float dotRadius = p * 0.30f;
        float textInsetX = p * 0.30f;

        for (int pinNumber = 1; pinNumber <= width; pinNumber++)
        {
            int rowY = (Position.Y + pinNumber) * p;

            // Stub from the pin endpoint to the bracket column, terminal dot
            // at the outer end -- the same visual grammar as every other pin.
            g.DrawLine(pen, leftX, rowY, bracketX, rowY);
            g.FillEllipse(pinBrush, leftX - 2, rowY - 2, 4, 4);

            // Per-bit name to the right of the bracket ("D4"). Drawn inside
            // the rotation transform, like the header's pin numbers, so it
            // follows the body.
            string bit = BitName(pinNumber);
            var bitSize = g.MeasureString(bit, ctx.PinFont);
            g.DrawString(bit, ctx.PinFont, textBrush,
                bracketX + textInsetX,
                rowY - bitSize.Height / 2f);

            // Sim-mode per-bit state dot, right of the text field, matching
            // the header's per-pin indicator.
            Signal? sig = ctx.SignalStateProvider?.Invoke(this, pinNumber);
            if (sig is Signal s)
            {
                float dotX = (Position.X + Size.Width) * p - p * 0.55f;
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
            g.DrawLine(pen, bracketX, topRowY - p / 2, bracketX, bottomRowY + p / 2);
            g.DrawLine(pen, bracketX, topRowY - p / 2, bracketX - p / 2, topRowY - p / 2);
            g.DrawLine(pen, bracketX, bottomRowY + p / 2, bracketX - p / 2, bottomRowY + p / 2);

            string name = string.IsNullOrWhiteSpace(Label) ? "?" : Label;
            string range = $"{name}[{startBit}..{startBit + width - 1}]";
            var rangeSize = g.MeasureString(range, ctx.LabelFont);
            g.DrawString(range, ctx.LabelFont, textBrush,
                bracketX - p / 2f,
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