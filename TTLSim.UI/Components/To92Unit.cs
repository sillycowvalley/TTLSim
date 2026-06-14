using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// TO-92 / transistor-style symbol for a 3-pin part such as the DS1813 reset
/// supervisor. Drawn as a domed package outline -- a flat bottom edge with a
/// semicircular top -- and three leads emerging downward from the flat edge,
/// named left-to-right in ascending pin-number order.
///
/// This is a drawing/geometry alternative to <see cref="ChipUnit"/> for the
/// SAME <see cref="ChipPartDefinition"/>: it reads pin numbers, names, and the
/// active-low ('/') convention straight from the definition, so the netlist is
/// identical to the box form -- only the symbol and pin placement differ. A
/// definition opts in via <see cref="ChipPartDefinition.To92"/>, and all three
/// construction paths route through <see cref="DeviceFactory.CreateChipSymbol"/>
/// so placement, load, and paste agree.
/// </summary>
public sealed class To92Unit : Unit
{
    /// <summary>Horizontal grid-cell pitch between adjacent legs.</summary>
    private const int PinPitch = 3;

    /// <summary>One cell of margin outside the outer legs.</summary>
    private const int SideMargin = 1;

    /// <summary>Bounding-box height in cells (even, to keep the pivot centred).</summary>
    private const int BoxHeight = 4;

    /// <summary>Flat-edge (package bottom) position, in cells from the box top.</summary>
    private const float FlatEdgeCell = 2.5f;

    private readonly ChipPin[] orderedPins;

    public To92Unit(Device device, UnitSpec spec, ChipPartDefinition definition)
        : base(device, spec)
    {
        orderedPins = definition.Pins.OrderBy(pin => pin.Number).ToArray();

        int span = (orderedPins.Length - 1) * PinPitch;
        Size = new Size(span + SideMargin * 2, BoxHeight);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        // Legs along the flat (bottom) edge, pointing Down, left-to-right in
        // pin-number order. LocalPosition sits on the bounding-box bottom edge
        // so wire endpoints land there, matching the other units' convention.
        for (int i = 0; i < orderedPins.Length; i++)
        {
            int x = SideMargin + i * PinPitch;
            AddPin(new Pin(orderedPins[i].Name, orderedPins[i].Number,
                new Point(x, Size.Height), PinDirection.Down));
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

        float bodyLeftX = (Position.X + 0.5f) * p;
        float bodyRightX = (Position.X + Size.Width - 0.5f) * p;
        float topY = Position.Y * p;
        float flatY = (Position.Y + FlatEdgeCell) * p;
        float domeHeight = flatY - topY;

        using var fill = new SolidBrush(ctx.FillColor);
        using var outline = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        // Domed package outline: flat bottom edge + semicircular (half-ellipse)
        // top. The ellipse's horizontal axis sits on the flat edge, so the top
        // half rises domeHeight above it. AddArc(...,180,180) traces the upper
        // half from the left-middle, over the top, to the right-middle.
        using (var path = new GraphicsPath())
        {
            float rectTop = flatY - domeHeight;
            float rectHeight = domeHeight * 2f;
            float rectWidth = bodyRightX - bodyLeftX;
            path.AddArc(bodyLeftX, rectTop, rectWidth, rectHeight, 180f, 180f);
            path.AddLine(bodyRightX, flatY, bodyLeftX, flatY);
            path.CloseFigure();
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
        }

        // Leads from the flat edge down to each pin endpoint, plus endpoint dots.
        using var leadPen = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);
        using var pinBrush = new SolidBrush(ctx.PinColor);
        foreach (var pin in Pins)
        {
            float px = (Position.X + pin.LocalPosition.X) * p;
            float py = (Position.Y + pin.LocalPosition.Y) * p;
            g.DrawLine(leadPen, px, flatY, px, py);
            g.FillEllipse(pinBrush, px - 2, py - 2, 4, 4);
        }

        // Pin names along the flat edge, one per leg, just inside the dome.
        // Active-low names (leading '/') print without the slash and get an
        // overbar, matching the box symbol.
        using var textBrush = new SolidBrush(ctx.ForegroundColor);
        using var barPen = new Pen(ctx.ForegroundColor, 0.6f);
        var tightFormat = StringFormat.GenericTypographic;
        float nameCentreY = (Position.Y + FlatEdgeCell - 0.75f) * p;
        foreach (var pin in Pins)
        {
            float px = (Position.X + pin.LocalPosition.X) * p;
            (string display, bool barred) = ParseBarredName(pin.Name);
            SizeF size = g.MeasureString(display, ctx.PinFont, int.MaxValue, tightFormat);
            float textX = px - size.Width / 2f;
            float textY = nameCentreY - size.Height / 2f;
            g.DrawString(display, ctx.PinFont, textBrush, textX, textY, tightFormat);
            if (barred)
                g.DrawLine(barPen, textX, textY, textX + size.Width, textY);
        }
    }

    protected override void DrawLabels(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;
        float midX = (b.X + b.Width / 2f) * p;

        using var brush = new SolidBrush(ctx.ForegroundColor);
        var desigSize = g.MeasureString(DisplayDesignator, ctx.LabelFont);
        var partSize = g.MeasureString(Device.FullPartNumber, ctx.PinFont);

        // Two-line block (designator over part number) sitting just above the
        // visual top of the symbol, so it never collides with the legs/names.
        float blockHeight = desigSize.Height + ctx.LineGap + partSize.Height;
        float desigY = b.Y * p - ctx.BodyGap - blockHeight;
        float partY = desigY + desigSize.Height + ctx.LineGap;

        g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
            midX - desigSize.Width / 2f, desigY);
        g.DrawString(Device.FullPartNumber, ctx.PinFont, brush,
            midX - partSize.Width / 2f, partY);
    }

    private static (string Display, bool Barred) ParseBarredName(string name)
    {
        if (!string.IsNullOrEmpty(name) && name[0] == '/')
            return (name.Substring(1), true);
        return (name, false);
    }
}