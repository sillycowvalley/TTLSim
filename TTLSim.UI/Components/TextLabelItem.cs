using System;
using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// A cosmetic free-text label drawn behind the schematic -- for titling a
/// block, annotating a region, noting a revision. No pins, no electrical
/// meaning; the router and electrical export ignore it (see
/// <see cref="IBackgroundItem"/>).
///
/// <para>
/// The displayed text is the item's <see cref="SchematicItem.Label"/>, so it
/// edits under "Label" in the property grid. The bounding box (used for
/// selection) is measured from the text at render time and written back to
/// <see cref="SchematicItem.Size"/>, so the hit area always matches what is on
/// screen and tracks edits to the text or font size.
/// </para>
/// </summary>
public sealed class TextLabelItem : SchematicItem, IBackgroundItem
{
    public TextLabelItem()
    {
        Label = "Text";
        // Provisional until the first Draw measures the real extent; keeps
        // drop-centring sane before the first paint.
        Size = new Size(8, 3);
    }

    /// <summary>Font em-size, in the same world units as the rest of the schematic text.</summary>
    [Category("Appearance")]
    [Description("Text size. Larger numbers render bigger text.")]
    public float FontSize { get; set; } = 4.0f;

    /// <summary>Text colour.</summary>
    [Category("Appearance")]
    [Description("Text colour.")]
    public Color TextColor { get; set; } = Color.FromArgb(110, 110, 110);

    // Cosmetic: nothing for the router to avoid.
    public override Rectangle RoutingBounds => Rectangle.Empty;

    public override void Draw(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        // Build the font from the schematic's text family at the chosen size.
        using var font = new Font(ctx.LabelFont.FontFamily, Math.Max(0.5f, FontSize));

        // Measure (a space stands in for empty text so a cleared label keeps a
        // grabbable box) and keep Size in step so hit-testing matches the glyphs.
        string text = string.IsNullOrEmpty(Label) ? " " : Label;
        SizeF measured = g.MeasureString(text, font);
        int wCells = Math.Max(2, (int)Math.Ceiling(measured.Width / p));
        int hCells = Math.Max(2, (int)Math.Ceiling(measured.Height / p));
        if (Size.Width != wCells || Size.Height != hCells)
            Size = new Size(wCells, hCells);

        if (string.IsNullOrEmpty(Label))
            return;   // nothing to draw, but Size above still gives a hit box

        var state = g.Save();
        ApplyRotationTransform(g, ctx);

        using var brush = new SolidBrush(Selected ? ctx.SelectedColor : TextColor);
        g.DrawString(Label, font, brush, Position.X * p, Position.Y * p);

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