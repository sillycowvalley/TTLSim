using System;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Box-shaped IC unit. Body is a plain rectangle; pins are arranged on the
/// left and right sides, with pin numbers on the outside and pin names on
/// the inside. Layout mirrors a physical DIP package: pins 1..N/2 run down
/// the left side top-to-bottom; pins N/2+1..N run up the right side
/// bottom-to-top.
///
/// A chip definition may supply an optional <see cref="ChipPartDefinition.Decorate"/>
/// delegate (drawing cosmetic detail such as miniature gate glyphs inside the
/// box) and an optional <see cref="ChipPartDefinition.ShowPinName"/> filter
/// (suppressing pin names that the decoration makes self-evident). Both are
/// null by default, leaving plain box chips unchanged.
/// </summary>
public sealed class ChipUnit : Unit
{
    /// <summary>Vertical grid-cell pitch between adjacent pins.</summary>
    private const int PinPitch = 2;

    /// <summary>Top and bottom margin on the body, in grid cells.</summary>
    private const int VerticalMargin = 1;

    private readonly ChipPartDefinition definition;

    public ChipUnit(Device device, UnitSpec spec, ChipPartDefinition definition)
        : base(device, spec)
    {
        this.definition = definition;

        int pinsPerSide = (definition.PinCount + 1) / 2;
        int bodyHeight = VerticalMargin * 2 + (pinsPerSide - 1) * PinPitch;
        // Width includes a one-cell stub on each side so pin endpoints
        // (LocalPosition) sit on the bounding-box edge, matching the
        // convention used by gates and passives.
        Size = new Size(definition.BodyWidth + 2, bodyHeight);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        int pinsPerSide = (definition.PinCount + 1) / 2;
        foreach (var cp in definition.Pins)
        {
            (PinDirection dir, int slot) = SlotFor(cp.Number, pinsPerSide);
            int y = VerticalMargin + slot * PinPitch;
            int x = dir == PinDirection.Left ? 0 : Size.Width;
            AddPin(new Pin(cp.Name, cp.Number, new Point(x, y), dir));
        }
    }

    /// <summary>
    /// For pin number n on a DIP with pinsPerSide pins per side, return the
    /// side and the vertical slot (0 = top of that side, going down).
    /// </summary>
    private static (PinDirection Direction, int Slot) SlotFor(int pinNumber, int pinsPerSide)
    {
        if (pinNumber <= pinsPerSide)
        {
            // Left side: pin 1 at top, growing downward.
            return (PinDirection.Left, pinNumber - 1);
        }
        else
        {
            // Right side: pin N/2+1 at the BOTTOM, growing upward (DIP mirror).
            int slotFromBottom = pinNumber - pinsPerSide - 1;
            int slot = pinsPerSide - 1 - slotFromBottom;
            return (PinDirection.Right, slot);
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
        int p = ctx.GridPitch;
        currentGridPitch = p;   // captured for PinLocalEndpoint lookups below
        int leftX = Position.X * p;
        int rightX = (Position.X + Size.Width) * p;
        int topY = Position.Y * p;
        int bottomY = (Position.Y + Size.Height) * p;

        // Body is inset by one cell on each side -- the outer cell is the pin stub.
        int bodyLeftX = leftX + p;
        int bodyRightX = rightX - p;

        using var fill = new SolidBrush(ctx.FillColor);
        using var outline = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        // Body path: rectangle with a semicircular notch cut into the top edge,
        // centred horizontally. The notch is ~2 cells wide.
        float notchDiameter = p * 1.5f;
        float notchLeft = (bodyLeftX + bodyRightX) / 2 - notchDiameter / 2;

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddLine(bodyLeftX, topY, notchLeft, topY);
        path.AddArc(notchLeft, topY - notchDiameter / 2,
                    notchDiameter, notchDiameter, 180, -180);
        path.AddLine(notchLeft + notchDiameter, topY, bodyRightX, topY);
        path.AddLine(bodyRightX, topY, bodyRightX, bottomY);
        path.AddLine(bodyRightX, bottomY, bodyLeftX, bottomY);
        path.CloseFigure();

        g.FillPath(fill, path);

        // Cosmetic decoration drawn on the body fill but BEHIND the black
        // outline, pin stubs, names, and designator that follow. The decorator
        // runs in a LOCAL coordinate frame where (0,0) is the top-centre of
        // the body (see ChipDecoration). We translate the Graphics here so the
        // decorator's arithmetic doesn't have to carry the chip's screen
        // position, and PinLocalEndpoint returns coordinates in that same
        // local frame.
        if (definition.Decorate is { } decorate)
        {
            float bodyCxPx = (bodyLeftX + bodyRightX) / 2f;
            float bodyHalfWidth = (bodyRightX - bodyLeftX) / 2f;
            float bodyHeightPx = bottomY - topY;
            var localBody = RectangleF.FromLTRB(
                -bodyHalfWidth, 0, +bodyHalfWidth, bodyHeightPx);

            var decoState = g.Save();
            g.TranslateTransform(bodyCxPx, topY);
            var deco = new ChipDecoration(g, ctx, localBody, PinLocalEndpoint);
            decorate(deco);
            g.Restore(decoState);
        }

        g.DrawPath(outline, path);

#if DEBUG
        DrawDebugGrid(g, p, Position, Size, bodyLeftX, bodyRightX, topY, bottomY);
#endif

        // Pin stubs + endpoint dots. The pin's LocalPosition is at the OUTER
        // stub endpoint (matching gate/passive convention); the stub runs
        // from there inward to the body edge.
        using var pinPen = new Pen(ctx.PinColor, 1f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        foreach (var pin in Pins)
        {
            int px = (Position.X + pin.LocalPosition.X) * p;
            int py = (Position.Y + pin.LocalPosition.Y) * p;
            int innerX = pin.LocalDirection == PinDirection.Left ? bodyLeftX : bodyRightX;

            g.DrawLine(pinPen, px, py, innerX, py);
            g.FillEllipse(pinBrush, px - 2, py - 2, 4, 4);
        }

        // Pin names inside the body, hard against the edge. Use the smaller
        // PinFont (rather than LabelFont) to leave room in the centre for
        // opposite-side names on narrow chips. Active-low pins (leading '/'
        // in the definition) get a thin bar drawn above the barred letters.
        // A chip with a ShowPinName filter draws only the pins it returns true
        // for (e.g. VCC/GND on a gate box whose glyphs make I/O pins obvious).
        using var textBrush = new SolidBrush(ctx.ForegroundColor);
        using var barPen = new Pen(ctx.ForegroundColor, 0.5f);
        // GenericTypographic skips the padding that GenericDefault adds around
        // the string, so MeasureString returns the true glyph extent and the
        // bar tracks the letters exactly.
        var tightFormat = StringFormat.GenericTypographic;
        const float nameInset = 0.2f;
        foreach (var pin in Pins)
        {
            if (!ShouldShowName(pin)) continue;

            int py = (Position.Y + pin.LocalPosition.Y) * p;
            int innerX = pin.LocalDirection == PinDirection.Left ? bodyLeftX : bodyRightX;

            (string displayName, int barLetterCount) = ParseBarredName(pin.Name);
            // Measure and draw with the same tight format so the bar -- which
            // is measured tight -- aligns to the visible glyph extent.
            var nameSize = g.MeasureString(displayName, ctx.PinFont, int.MaxValue, tightFormat);
            float nameX = pin.LocalDirection == PinDirection.Left
                ? innerX + nameInset * p
                : innerX - nameInset * p - nameSize.Width;
            float nameY = py - nameSize.Height / 2;
            g.DrawString(displayName, ctx.PinFont, textBrush, nameX, nameY, tightFormat);

            if (barLetterCount > 0)
            {
                string barred = displayName.Substring(0, barLetterCount);
                var barredSize = g.MeasureString(barred, ctx.PinFont, int.MaxValue, tightFormat);
                // The bar sits just above the measured-text rectangle's top
                // edge -- a small fraction of the font size above nameY.
                float barY = nameY + ctx.PinFont.Size * 0.15f;
                g.DrawLine(barPen, nameX, barY, nameX + barredSize.Width, barY);
            }
        }
    }

#if DEBUG
    /// <summary>
    /// Draw a coordinate grid clipped to the chip body, for visual alignment
    /// checks against decorator geometry. Minor cells get a single dot; major
    /// intersections (every 5 cells from the chip origin) get a small
    /// crosshair and the world-grid coordinate as a label. Coordinates are
    /// WORLD-grid (chip-relative + Position), in the same frame as
    /// Pin.LocalPosition + Position arithmetic.
    /// </summary>
    private static void DrawDebugGrid(
        System.Drawing.Graphics g,
        int gridPitch,
        System.Drawing.Point chipPosition,
        System.Drawing.Size chipSize,
        int bodyLeftPx, int bodyRightPx, int bodyTopPx, int bodyBottomPx)
    {
        using var dotBrush = new System.Drawing.SolidBrush(
            System.Drawing.Color.FromArgb(200, System.Drawing.Color.DeepPink));
        using var majorPen = new System.Drawing.Pen(
            System.Drawing.Color.FromArgb(230, System.Drawing.Color.DeepPink), 1.2f);
        using var labelFont = new System.Drawing.Font(
            System.Drawing.FontFamily.GenericMonospace,
            Math.Max(2f, gridPitch * 0.7f),
            System.Drawing.FontStyle.Bold);
        using var labelBrush = new System.Drawing.SolidBrush(
            System.Drawing.Color.FromArgb(230, System.Drawing.Color.DeepPink));

        int gxMin = chipPosition.X;
        int gxMax = chipPosition.X + chipSize.Width;
        int gyMin = chipPosition.Y;
        int gyMax = chipPosition.Y + chipSize.Height;

        for (float gx = gxMin; gx <= gxMax; gx += 0.5f)
        {
            for (float gy = gyMin; gy <= gyMax; gy += 0.5f)
            {
                float px = gx * gridPitch;
                float py = gy * gridPitch;
                if (px < bodyLeftPx || px > bodyRightPx ||
                    py < bodyTopPx || py > bodyBottomPx)
                    continue;

                bool major = ((gx - gxMin) % 5 == 0) && ((gy - gyMin) % 5 == 0);
                if (major)
                {
                    g.DrawLine(majorPen, px - 2, py, px + 2, py);
                    g.DrawLine(majorPen, px, py - 2, px, py + 2);
                    g.DrawString($"{gx},{gy}", labelFont, labelBrush, px + 2, py + 1);
                }
                else
                {
                    g.FillRectangle(dotBrush, px - 0.25f, py - 0.25f, 0.5f, 0.5f);
                }
            }
        }
    }
#endif

    /// <summary>
    /// Outer stub endpoint of a pin, by pin number, in the chip-LOCAL pixel
    /// frame: (0,0) is the top-centre of the body, +x right, +y down. Called
    /// by ChipDecoration helpers to anchor glyphs and leads to pin geometry
    /// without needing the chip's screen position.
    /// </summary>
    private PointF PinLocalEndpoint(int pinNumber)
    {
        int p = currentGridPitch;
        // Body's horizontal centre column in chip-cell coords. Size.Width is
        // BodyWidth + 2 (one stub cell each side), so the geometric centre
        // lands at Size.Width / 2 regardless of whether BodyWidth is even or odd.
        float bodyCxCells = Size.Width / 2f;

        foreach (var pin in Pins)
        {
            if (pin.Number != pinNumber) continue;
            // Pin.LocalPosition is in chip-cell coords from the chip's top-left
            // corner. The body's top edge sits at chip-cell y=0, so the y
            // mapping is direct; x is measured outward from the body centreline.
            float xCellsFromBodyCx = pin.LocalPosition.X - bodyCxCells;
            float yCellsFromBodyTop = pin.LocalPosition.Y;
            return new PointF(xCellsFromBodyCx * p, yCellsFromBodyTop * p);
        }
        return PointF.Empty;
    }

    // Grid pitch captured at the top of DrawShape, where the decorator runs.
    // PinLocalEndpoint lookups happen later in that same call, so this is
    // always current by the time they fire.
    private int currentGridPitch = 5;

    private bool ShouldShowName(Pin pin)
    {
        if (definition.ShowPinName is null) return true;
        // Match the pin back to its ChipPin definition to apply the filter.
        foreach (var cp in definition.Pins)
            if (cp.Number == pin.Number)
                return definition.ShowPinName(cp);
        return true;
    }

    /// <summary>
    /// Parse a pin name written with the active-low convention. A leading
    /// '/' marks the whole name as active-low; the slash is stripped from
    /// the displayed string and the caller draws a bar above every character
    /// of the displayed string. Examples:
    ///   "/RESET"  -> ("RESET",  5)
    ///   "/TRIG1"  -> ("TRIG1",  5)
    ///   "/WE"     -> ("WE",     2)
    ///   "A14"     -> ("A14",    0)   // no leading '/'
    /// </summary>
    private static (string Display, int BarLetterCount) ParseBarredName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] != '/')
            return (name, 0);

        string display = name.Substring(1);
        return (display, display.Length);
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;
        float midY = (b.Y + b.Height / 2f) * p;

        using var brush = new SolidBrush(ctx.ForegroundColor);

        var desigSize = g.MeasureString(DisplayDesignator, ctx.LabelFont);
        var partSize = g.MeasureString(Device.FullPartNumber, ctx.PinFont);

        bool hasLabel = !string.IsNullOrEmpty(Label);
        // User Label drawn larger than the designator -- use ctx.LabelFont
        // bumped up. Fall back gracefully if Label is empty.
        using var userLabelFont = hasLabel
            ? new Font(ctx.LabelFont.FontFamily, ctx.LabelFont.Size * 1.6f, FontStyle.Bold)
            : null;
        SizeF userLabelSize = hasLabel
            ? g.MeasureString(Label, userLabelFont!)
            : SizeF.Empty;

        // Total stack height: designator + line gap + part number
        //                   (+ body gap + user label, if present).
        float stackHeight = desigSize.Height + ctx.LineGap + partSize.Height;
        if (hasLabel) stackHeight += ctx.BodyGap + userLabelSize.Height;

        float desigY = midY - stackHeight / 2;

        float partY = desigY + desigSize.Height + ctx.LineGap;
        float userLabelY = partY + partSize.Height + ctx.BodyGap;

        g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
            midX - desigSize.Width / 2, desigY);
        g.DrawString(Device.FullPartNumber, ctx.PinFont, brush,
            midX - partSize.Width / 2, partY);

        if (hasLabel)
        {
            g.DrawString(Label, userLabelFont!, brush,
                midX - userLabelSize.Width / 2, userLabelY);
        }
    }
}