using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.Core;
using TTLSim.UI.Model;
using TTLSim.UI.View;

namespace TTLSim.UI.Components;

/// <summary>
/// Pin-header output: a labelled connector that exposes 2/4/6/8 nets for
/// external observation. Each pin shows the resolved Signal as a coloured
/// dot inside the body, using SignalColors. No chip model is bound; the
/// header is a pure net terminator and listener.
///
/// Layout matches EasyEDA's stock header symbols (so exported parts read
/// the same in both tools):
///   * At R0 the body is vertical and pins exit to the LEFT.
///   * At R180 pins exit to the RIGHT.
///   * At R90 pins exit UPWARD; at R270 they exit DOWNWARD. (TTL Sim's
///     default rotation sense is opposite; the inversion is encapsulated
///     in <see cref="Pin"/>'s SwapR90R270 flag plus a matching extra 180
///     in DrawShape -- both halves must stay consistent or wires won't
///     connect to where the body is drawn.)
///   * Bounding box is symmetric about the body horizontally (StubCells
///     left, FarSpacerCells right). Its vertical height is pinCount + 2
///     (always even, so the rotation pivot lands on an integer cell).
///     The visible body height is pinCount + 1, drawn flush with the
///     bounding box top -- the bottom row of the bounding box is unused
///     padding. Result: one cell of body padding above pin 1 and below
///     pin N, with the pivot still integer-aligned. The trade-off is
///     that the debug routing-bounds rectangle is offset from the body
///     centre by half a cell after rotation -- harmless, since it's a
///     debug annotation only.
///   * Pin numbers sit inside the body, left-aligned just past the pin
///     edge, reading inward.
///   * The designator label is placed to match EasyEDA's H5/H6/H7/H8:
///     above the body for vertical orientations (R0/R180), to the right
///     of the body for horizontal orientations (R90/R270).
///
/// Pin numbering follows the body (pin 1 stays "pin 1" under rotation),
/// achieved for free via Pin.LocalPosition + the standard rotation transform.
///
/// <para>
/// <see cref="Mirrored"/> flips the header across its LONG axis: the pins
/// exit from the opposite edge, while pin order, pin numbering, and the
/// electrical mapping are all unchanged. Because the bounding box is
/// horizontally symmetric (StubCells == FarSpacerCells), the body
/// rectangle occupies the same cells mirrored or not -- only the stub
/// side, the pin-number alignment, and the state-dot side swap. The
/// existing Pin objects are relocated IN PLACE (Pin.LocalPosition /
/// LocalDirection are settable) so Connections, ribbon links, and router
/// caches that hold Pin references stay valid across the toggle. The
/// main use is face-to-face ribbon-linked header pairs: mirror one side
/// instead of rotating it 180, and the strands stay parallel with pin 1
/// aligned to pin 1.
/// </para>
/// </summary>
public sealed class HeaderOutputUnit : Unit
{
    private readonly int pinCount;

    private bool mirrored;

    /// <summary>Body width in grid cells.</summary>
    private const int BodyWidth = 2;

    /// <summary>One cell of stub on the pin side, so pin LocalPosition sits
    /// at the outer edge of the bounding box (matching ChipUnit / gates).</summary>
    private const int StubCells = 1;

    /// <summary>One cell of empty space on the far (non-pin) side, so the
    /// bounding box is horizontally symmetric and the rotation pivot lands
    /// on the body's geometric centre.</summary>
    private const int FarSpacerCells = 1;

    public HeaderOutputUnit(Device device, UnitSpec spec, HeaderPartDefinition definition)
        : base(device, spec)
    {
        pinCount = definition.PinCount;
        // Bounding-box height must be EVEN so the rotation pivot (Size.Height / 2)
        // lands on an integer cell. The visible body is pinCount + 1 tall; round
        // that up to the next even number. For 2/4/6/8 this equals the old
        // pinCount + 2; for an odd count (3) it gives pinCount + 1, keeping the
        // body symmetric and the pivot dead-centre.
        int boxHeight = pinCount + 1;
        if ((boxHeight & 1) != 0) boxHeight++;
        Size = new Size(StubCells + BodyWidth + FarSpacerCells, boxHeight);
        BuildPins(spec);
    }

    /// <summary>
    /// Flip the header across its long axis: pins exit from the opposite
    /// edge of the body. Pin order, numbering, and the electrical mapping
    /// (including ribbon links, which are always A.i &lt;-&gt; B.i) are
    /// unchanged -- this is a drawing-side convenience so a face-to-face
    /// header pair can sit pins-inward without rotating one of them 180.
    /// Edits go through the property grid's generic SetPropertyCommand
    /// path, the same way HeaderLink.Reversed does.
    /// </summary>
    [Category("Layout")]
    [DefaultValue(false)]
    [Description("Flip the header across its long axis so the pins exit from the opposite side. Pin order and numbering are unchanged.")]
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

    protected override void BuildPins(UnitSpec spec)
    {
        // Pins on the LEFT edge of the bounding box; body starts one cell in.
        // SwapR90R270 = true so this header's pins follow EasyEDA's rotation
        // sense instead of TTL Sim's default. (A mirrored header relocates
        // these pins to the RIGHT edge via ApplyPinGeometry -- the Pin
        // objects themselves are permanent.)
        for (int i = 0; i < pinCount; i++)
        {
            int pinNumber = i + 1;
            AddPin(new Pin(
                pinNumber.ToString(),
                pinNumber,
                new Point(0, pinNumber),
                PinDirection.Left,
                swapR90R270: true));
        }
    }

    /// <summary>
    /// Place every pin on the edge selected by <see cref="Mirrored"/>:
    /// x = 0 facing Left when unmirrored, x = Size.Width facing Right when
    /// mirrored. Relocates the EXISTING Pin objects (never rebuilds them)
    /// so connections and links holding Pin references survive the toggle.
    /// Row (Y) placement is untouched -- pin order never changes.
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

    protected override void DrawShape(Graphics g, RenderContext ctx)
    {
        // The Unit base has already applied a Graphics rotation transform
        // in TTL Sim's default (clockwise) sense. Pins use SwapR90R270 so
        // we must flip the visual rotation for R90/R270 to match -- an
        // extra 180-degree rotation around the body centre does it.
        if (Rotation == Rotation.R90 || Rotation == Rotation.R270)
        {
            int pp = ctx.GridPitch;
            float pivotX = Pivot.X * pp;
            float pivotY = Pivot.Y * pp;
            g.TranslateTransform(pivotX, pivotY);
            g.RotateTransform(180f);
            g.TranslateTransform(-pivotX, -pivotY);
        }

        int p = ctx.GridPitch;
        int leftX = Position.X * p;                      // left outer edge
        int rightX = (Position.X + Size.Width) * p;      // right outer edge
        int bodyLeftX = leftX + StubCells * p;           // where the body rectangle starts
        int bodyW = BodyWidth * p;
        int bodyRightX = bodyLeftX + bodyW;

        // Pin-endpoint side depends on Mirrored. The body rectangle does
        // NOT: because StubCells == FarSpacerCells the box is horizontally
        // symmetric, so the body occupies the same cells either way and
        // only the stub side swaps.
        int pinOuterX = mirrored ? rightX : leftX;
        int pinBodyEdgeX = mirrored ? bodyRightX : bodyLeftX;

        // Bounding box height is pinCount + 2 (even, to keep the rotation
        // pivot on an integer cell). The VISIBLE body is one cell shorter
        // (pinCount + 1) so the pin block is symmetrically padded: one
        // cell above pin 1 and one cell below pin N. The unused cell sits
        // at the BOTTOM of the bounding box (below the visible body) --
        // it doesn't appear on screen, it just provides the extra cell of
        // bounding-box height the integer-pivot calculation requires.
        // Routing bounds still cover the full bounding box, so at
        // R90/R270 the rotated routing rect is offset from the visible
        // body centre by half a cell. That's a debug-only annotation; it
        // doesn't affect actual wire routing.
        int topY = Position.Y * p;
        int bodyH = (pinCount + 1) * p;

        using var bodyFill = new SolidBrush(ctx.FillColor);
        g.FillRectangle(bodyFill, bodyLeftX, topY, bodyW, bodyH);

        using var bodyPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.0f);
        g.DrawRectangle(bodyPen, bodyLeftX, topY, bodyW, bodyH);

        using var pinBrush = new SolidBrush(ctx.PinColor);
        using var stubPen = new Pen(ctx.ForegroundColor, 1.0f);
        using var numberBrush = new SolidBrush(ctx.ForegroundColor);

        float dotRadius = p * 0.30f;
        float numberInsetX = p * 0.30f;

        for (int i = 0; i < pinCount; i++)
        {
            int pinNumber = i + 1;
            int rowY = (Position.Y + pinNumber) * p;

            // Pin stub: from the outer pin endpoint inward to the body edge.
            g.DrawLine(stubPen, pinOuterX, rowY, pinBodyEdgeX, rowY);

            // Pin terminal dot at the outer endpoint.
            g.FillEllipse(pinBrush, pinOuterX - 2, rowY - 2, 4, 4);

            // Pin number on the pin-side half of the body: left-aligned just
            // inside the left body edge when unmirrored, right-aligned just
            // inside the right body edge when mirrored.
            string numText = pinNumber.ToString();
            var numSize = g.MeasureString(numText, ctx.PinFont);
            float numX = mirrored
                ? bodyRightX - numberInsetX - numSize.Width
                : bodyLeftX + numberInsetX;
            g.DrawString(numText, ctx.PinFont, numberBrush,
                numX,
                rowY - numSize.Height / 2f);

            // State indicator on the far (non-pin) half of the body.
            Signal? sig = ctx.SignalStateProvider?.Invoke(this, pinNumber);
            float dotX = mirrored
                ? bodyLeftX + p * 0.55f
                : bodyLeftX + bodyW - p * 0.55f;
            if (sig is Signal s)
            {
                using var dotBrush = new SolidBrush(SignalColors.For(s));
                g.FillEllipse(dotBrush,
                    dotX - dotRadius, rowY - dotRadius,
                    dotRadius * 2, dotRadius * 2);
            }
            else
            {
                using var dotPen = new Pen(ctx.ForegroundColor, 0.8f);
                g.DrawEllipse(dotPen,
                    dotX - dotRadius, rowY - dotRadius,
                    dotRadius * 2, dotRadius * 2);
            }
        }
    }

    /// <summary>
    /// Designator placement matches EasyEDA's header symbols:
    ///   * Vertical body (R0 / R180): designator sits ABOVE the body,
    ///     left edge aligned with the body's left edge (like H5 and H7).
    ///   * Horizontal body (R90 / R270): designator sits to the RIGHT of
    ///     the body, top edge aligned with the body's top edge (like H6
    ///     and H8).
    /// "Above" / "right" / "top" are all in screen space; the label text
    /// itself is screen-axis-aligned (Unit.Draw resets the rotation
    /// transform before DrawLabels runs). Mirroring does not move the
    /// designator: the body occupies the same cells either way.
    /// </summary>
    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;

        // Effective rotation: swap R90/R270 to match the header's visual
        // flip in DrawShape, so the corners we compute below land where
        // the body is actually drawn.
        Rotation eff = Rotation switch
        {
            Rotation.R90 => Rotation.R270,
            Rotation.R270 => Rotation.R90,
            _ => Rotation,
        };

        // Visible body extent (grid units) after rotation.
        var corners = GetRotatedBodyCorners(eff);
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            if (c.X < minX) minX = c.X;
            if (c.X > maxX) maxX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.Y > maxY) maxY = c.Y;
        }

        using var brush = new SolidBrush(ctx.ForegroundColor);
        var desigSize = g.MeasureString(DisplayDesignator, ctx.LabelFont);

        bool verticalBody = Rotation == Rotation.R0 || Rotation == Rotation.R180;
        float x, y;
        if (verticalBody)
        {
            // Above the body, left edge aligned with body's left edge.
            x = minX * p;
            y = minY * p - desigSize.Height;
        }
        else
        {
            // Right of the body, top aligned with body's top edge.
            x = maxX * p + ctx.BodyGap;
            y = minY * p;
        }

        g.DrawString(DisplayDesignator, ctx.LabelFont, brush, x, y);

        // If the user has set a Label, draw it immediately after the
        // designator on the same line, matching the EasyEDA layout
        // (designator and Name share a baseline). Same font as the
        // designator so they read as one stacked unit.
        if (!string.IsNullOrEmpty(Label))
        {
            float labelX = x + desigSize.Width + ctx.BodyGap;
            g.DrawString(Label, ctx.LabelFont, brush, labelX, y);
        }
    }

    /// <summary>
    /// Return the four corners of the body rectangle, rotated by
    /// <paramref name="eff"/> around the unit pivot, in grid units.
    /// </summary>
    private PointF[] GetRotatedBodyCorners(Rotation eff)
    {
        float l = Position.X + StubCells;
        float r = Position.X + StubCells + BodyWidth;
        float t = Position.Y;                          // visible body top
        float b = Position.Y + pinCount + 1;           // visible body bottom
        var raw = new[]
        {
            new PointF(l, t), new PointF(r, t),
            new PointF(r, b), new PointF(l, b),
        };
        float pivotX = Pivot.X, pivotY = Pivot.Y;
        var result = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            float lx = raw[i].X - pivotX;
            float ly = raw[i].Y - pivotY;
            (float rx, float ry) = eff switch
            {
                Rotation.R90 => (-ly, lx),
                Rotation.R180 => (-lx, -ly),
                Rotation.R270 => (ly, -lx),
                _ => (lx, ly),
            };
            result[i] = new PointF(pivotX + rx, pivotY + ry);
        }
        return result;
    }
}