using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// A cosmetic rectangle drawn behind the schematic -- for grouping a region,
/// shading a functional block, framing a sub-circuit. It has no pins and no
/// electrical meaning; the wire router and any electrical export ignore it
/// (see <see cref="IBackgroundItem"/>).
///
/// <para>
/// Size is edited via <see cref="Width"/> / <see cref="Height"/> in the
/// property grid (grid units). <see cref="SchematicItem.Size"/> itself is
/// protected, so these two properties are the editable surface; they keep the
/// base size in step. Position and rotation use the normal canvas gestures
/// (drag to move, Space to rotate).
/// </para>
/// </summary>
public sealed class RectangleItem : SchematicItem, IBackgroundItem
{
    public RectangleItem()
    {
        // A sensible starting region; the user resizes via the property grid.
        Size = new Size(20, 12);
    }

    /// <summary>Width of the rectangle in grid units. Backs <see cref="SchematicItem.Size"/>.</summary>
    [Category("Layout")]
    [Description("Width of the rectangle in grid cells.")]
    public int Width
    {
        get => Size.Width;
        set => Size = new Size(System.Math.Max(1, value), Size.Height);
    }

    /// <summary>Height of the rectangle in grid units. Backs <see cref="SchematicItem.Size"/>.</summary>
    [Category("Layout")]
    [Description("Height of the rectangle in grid cells.")]
    public int Height
    {
        get => Size.Height;
        set => Size = new Size(Size.Width, System.Math.Max(1, value));
    }

    /// <summary>When true the interior is filled with <see cref="FillColor"/>; otherwise outline only.</summary>
    [Category("Appearance")]
    [Description("Fill the interior (true) or draw the outline only (false).")]
    public bool Filled { get; set; } = true;

    /// <summary>Interior fill colour. Use an alpha &lt; 255 for a translucent region that lets the grid show through.</summary>
    [Category("Appearance")]
    [Description("Interior fill colour. A low alpha gives a translucent region.")]
    public Color FillColor { get; set; } = Color.FromArgb(32, 120, 120, 120);

    /// <summary>Outline colour.</summary>
    [Category("Appearance")]
    [Description("Outline colour.")]
    public Color BorderColor { get; set; } = Color.FromArgb(150, 120, 120, 120);

    // No pins, and nothing for the router to avoid: a cosmetic region must not
    // block wire routing through the area it covers.
    public override Rectangle RoutingBounds => Rectangle.Empty;

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);

        int p = ctx.GridPitch;
        var rect = new Rectangle(
            Position.X * p, Position.Y * p, Size.Width * p, Size.Height * p);

        if (Filled)
        {
            using var brush = new SolidBrush(FillColor);
            g.FillRectangle(brush, rect);
        }

        using var pen = new Pen(Selected ? ctx.SelectedColor : BorderColor, 1.2f);
        g.DrawRectangle(pen, rect);

        g.Restore(state);
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