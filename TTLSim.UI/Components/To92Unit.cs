using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// TO-92 / transistor-style symbol for a 3-pin part such as the DS1813 reset
/// supervisor. Drawn as a package outline with a flat bottom edge, short
/// straight sides, and a domed (semicircular) top -- with three leads emerging
/// downward from the flat edge, named left-to-right in ascending pin-number
/// order. The pin names run PARALLEL to their legs (rotated 90 degrees).
///
/// This is a drawing/geometry alternative to <see cref="ChipUnit"/> for the
/// SAME <see cref="ChipPartDefinition"/>: it reads pin numbers, names, and the
/// active-low ('/') convention straight from the definition, so the netlist is
/// identical to the box form -- only the symbol and pin placement differ. A
/// definition opts in via <see cref="ChipPartDefinition.To92"/>, and all three
/// construction paths route through <see cref="DeviceFactory.CreateChipSymbol"/>
/// so placement, load, and paste agree.
///
/// Sizing note: the outline width (<see cref="BodyWidthCells"/>) is fixed, and
/// the legs are centred inside it at a DIP-standard pitch (<see cref="PinPitch"/>),
/// so the leg spacing matches the ICs without changing the body. The straight
/// sides (<see cref="StraightSideCell"/>) push the arc up off the base; the
/// overall depth is <see cref="FlatEdgeCell"/>, perpendicular to the flat edge.
/// </summary>
public sealed class To92Unit : Unit
{
    /// <summary>DIP-style grid-cell pitch between adjacent legs.</summary>
    private const int PinPitch = 2;

    /// <summary>Bounding-box (outline) width in cells. Fixed independently of
    /// the leg pitch so tightening the legs doesn't shrink the body.</summary>
    private const int BodyWidthCells = 8;

    /// <summary>Bounding-box height in cells (even, to keep the pivot centred):
    /// dome depth + leg length.</summary>
    private const int BoxHeight = 6;

    /// <summary>Flat-edge (package bottom) position, in cells from the box top.
    /// The body rises this many cells above the flat edge; the remainder of the
    /// box height is the leg length.</summary>
    private const float FlatEdgeCell = 4.0f;

    /// <summary>Height of the straight vertical sides, in cells, between the
    /// flat edge and where the arc begins. Pushes the arc up off the base.
    /// Must be less than <see cref="FlatEdgeCell"/>.</summary>
    private const float StraightSideCell = 1.5f;

    private readonly ChipPin[] orderedPins;

    public To92Unit(Device device, UnitSpec spec, ChipPartDefinition definition)
        : base(device, spec)
    {
        orderedPins = definition.Pins.OrderBy(pin => pin.Number).ToArray();
        Size = new Size(BodyWidthCells, BoxHeight);
        BuildPins(spec);
    }

    protected override void BuildPins(UnitSpec spec)
    {
        // Legs along the flat (bottom) edge, pointing Down, centred in the body
        // at the DIP pitch, left-to-right in pin-number order. LocalPosition
        // sits on the bounding-box bottom edge so wire endpoints land there,
        // matching the other units' convention.
        int n = orderedPins.Length;
        int span = (n - 1) * PinPitch;
        int firstX = (Size.Width - span) / 2;
        for (int i = 0; i < n; i++)
        {
            int x = firstX + i * PinPitch;
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
        float shoulderY = (Position.Y + FlatEdgeCell - StraightSideCell) * p;
        float arcHeight = shoulderY - topY;

        using var fill = new SolidBrush(ctx.FillColor);
        using var outline = new Pen(Selected ? ctx.SelectedColor : ctx.ForegroundColor, 1.2f);

        // Package outline: flat bottom edge, short straight sides up to the
        // shoulder, then a semicircular (half-ellipse) top. The ellipse's
        // horizontal axis sits on the shoulder line, so AddArc(...,180,180)
        // traces the upper half from the left shoulder, over the top, to the
        // right shoulder.
        using (var path = new GraphicsPath())
        {
            float rectWidth = bodyRightX - bodyLeftX;
            path.AddLine(bodyLeftX, flatY, bodyLeftX, shoulderY);
            path.AddArc(bodyLeftX, shoulderY - arcHeight, rectWidth, arcHeight * 2f, 180f, 180f);
            path.AddLine(bodyRightX, shoulderY, bodyRightX, flatY);
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

        // Pin names, one per leg, rotated 90 degrees CCW so each reads PARALLEL
        // to its leg (bottom-to-top at rotation 0), centred on the leg's column
        // inside the body. Drawing in the body's rotated frame keeps them
        // parallel to the legs in every orientation. Active-low names (leading
        // '/') print without the slash and get an overbar.
        using var textBrush = new SolidBrush(ctx.ForegroundColor);
        using var barPen = new Pen(ctx.ForegroundColor, 0.6f);
        var tightFormat = StringFormat.GenericTypographic;
        float nameCentreY = (Position.Y + FlatEdgeCell - 1.2f) * p;
        foreach (var pin in Pins)
        {
            float px = (Position.X + pin.LocalPosition.X) * p;
            (string display, bool barred) = ParseBarredName(pin.Name);
            SizeF size = g.MeasureString(display, ctx.PinFont, int.MaxValue, tightFormat);

            var state = g.Save();
            g.TranslateTransform(px, nameCentreY);
            g.RotateTransform(-90f);
            g.DrawString(display, ctx.PinFont, textBrush,
                -size.Width / 2f, -size.Height / 2f, tightFormat);
            if (barred)
                g.DrawLine(barPen,
                    -size.Width / 2f, -size.Height / 2f,
                    size.Width / 2f, -size.Height / 2f);
            g.Restore(state);
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