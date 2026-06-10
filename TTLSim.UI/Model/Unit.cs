using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>
/// One placeable, draggable, selectable unit of a Device. Each gate of a
/// 7400 is a Unit; the four NAND units share a single Device. Passives are
/// also Units (a single one) of their respective Device.
///
/// Unit is a SchematicItem so it slots straight into Schematic.Items and
/// participates in selection, dragging, deletion, and routing exactly like
/// any other item. The Device back-reference supplies its designator and
/// part-level metadata.
///
/// Draw is sealed at this level. Subclasses override DrawShape (the body
/// and pin stubs) and DrawLabels (the designator / value / part number).
/// Rotation is applied as a Graphics transform around the unrotated centre
/// before DrawShape runs; the transform is reset before DrawLabels so
/// labels stay screen-axis-aligned.
/// </summary>
public abstract class Unit : SchematicItem
{
    // DevicePropertyFilter (not the plain ExpandableObjectConverter) so the
    // nested Device node in the property grid shows only the properties that
    // apply to the specific part. A property-level [TypeConverter] overrides
    // the class-level one on Device, so the filter MUST be named here -- the
    // attribute on the Device class alone has no effect for this nested node.
    [Category("Identity")]
    [TypeConverter(typeof(DevicePropertyFilter))]
    public Device Device { get; }

    /// <summary>
    /// 'a', 'b', 'c', ... for multi-unit devices. '\0' for single-unit parts
    /// (passives) where the designator alone suffices. '?' for power units.
    /// </summary>
    [Browsable(false)]
    public char UnitLetter { get; }

    [Browsable(false)]
    public bool IsSchmitt { get; }

    /// <summary>
    /// "U1a" for multi-unit devices, "R3" for single-unit parts, "U2?" for
    /// power units. This is what gets drawn on the canvas beside the symbol.
    /// </summary>
    [Category("Identity")]
    [ReadOnly(true)]
    [Browsable(false)]
    public string DisplayDesignator =>
        UnitLetter == '\0' ? Device.Designator :
        UnitLetter == '?' ? $"{Device.Designator}?" :
                             $"{Device.Designator}{UnitLetter}";

    protected Unit(Device device, UnitSpec spec)
    {
        Device = device;
        UnitLetter = spec.Letter;
        IsSchmitt = spec.IsSchmitt;
        // NOTE: BuildPins is NOT called here -- the base constructor runs
        // before the subclass body, so Size would still be (0,0). Each
        // subclass calls BuildPins(spec) explicitly after setting Size.
    }

    /// <summary>
    /// Add pins for this unit based on its spec. Must be called by the
    /// concrete subclass constructor after it has set Size.
    /// </summary>
    protected abstract void BuildPins(UnitSpec spec);

    // ---------------------------------------------------------- draw orchestration

    public sealed override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        g.Restore(state);

        DrawLabels(g, ctx);
    }

    /// <summary>
    /// Draw the body and pin stubs of this unit in UNROTATED coordinates.
    /// The Graphics has already had a rotation transform applied if needed.
    /// </summary>
    protected abstract void DrawShape(Graphics g, RenderContext ctx);

    /// <summary>
    /// Draw labels in screen-axis-aligned coordinates (transform reset).
    /// Default: nothing. Gate / passive subclasses override to position
    /// designators and values appropriately.
    /// </summary>
    protected virtual void DrawLabels(Graphics g, RenderContext ctx) { }

    /// <summary>
    /// Apply the rotation transform for this unit's current rotation,
    /// pivoting around the unrotated bounding-box centre in screen pixels.
    /// </summary>
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

    // ---------------------------------------------------------- shared rendering helpers

    /// <summary>
    /// Distribute spec.InputPins evenly down the left side and place the
    /// output pin centred vertically on the right. Standard layout for
    /// NAND/NOR/AND/OR/XOR gates of any input count.
    /// </summary>
    protected void BuildLeftInputsRightOutput(UnitSpec spec)
    {
        int n = spec.InputPins.Length;
        for (int i = 0; i < n; i++)
        {
            int y = 1 + i * 2;  // input pins on rows 1, 3, 5, ...
            AddPin(new Pin($"{spec.InputPins[i]}", spec.InputPins[i],
                new Point(0, y), PinDirection.Left));
        }
        AddPin(new Pin($"{spec.OutputPin}", spec.OutputPin,
            new Point(Size.Width, Size.Height / 2), PinDirection.Right));
    }

    /// <summary>
    /// Draw the designator on one line and (if Label is set) the label on a
    /// second line beneath it. Both lines stack ABOVE <paramref name="bodyTopY"/>
    /// so they don't overlap the body. Centred horizontally on
    /// <paramref name="midX"/>. Coordinates in screen-space pixels.
    /// </summary>
    protected void DrawDesignatorAndValue(Graphics g, RenderContext ctx, float midX, float bodyTopY)
    {
        using var brush = new SolidBrush(ctx.ForegroundColor);
        var desigSize = g.MeasureString(DisplayDesignator, ctx.LabelFont);

        bool hasLabel = !string.IsNullOrEmpty(Label);
        float labelLineHeight = hasLabel
            ? g.MeasureString(Label, ctx.PinFont).Height
            : 0f;

        // Stack upward from bodyTopY: label sits just above the body,
        // designator above the label.
        float labelY = bodyTopY - labelLineHeight - ctx.BodyGap;
        float desigY = hasLabel
            ? labelY - desigSize.Height - ctx.LineGap
            : bodyTopY - desigSize.Height - ctx.BodyGap;

        g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
            midX - desigSize.Width / 2, desigY);

        if (hasLabel)
        {
            var labelSize = g.MeasureString(Label, ctx.PinFont);
            g.DrawString(Label, ctx.PinFont, brush,
                midX - labelSize.Width / 2, labelY);
        }
    }

    /// <summary>
    /// Draw the designator on top and the full part number underneath, both
    /// centred horizontally on midX, with the top of the designator at topY
    /// (screen-space pixels). Used by gate units.
    /// </summary>
    protected void DrawDesignatorAndPartNumber(Graphics g, RenderContext ctx, float midX, float topY)
    {
        using var brush = new SolidBrush(ctx.ForegroundColor);
        var desigSize = g.MeasureString(DisplayDesignator, ctx.LabelFont);
        g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
            midX - desigSize.Width / 2, topY);

        var partSize = g.MeasureString(Device.FullPartNumber, ctx.PinFont);
        g.DrawString(Device.FullPartNumber, ctx.PinFont, brush,
            midX - partSize.Width / 2, topY + desigSize.Height + ctx.LineGap);
    }

    /// <summary>
    /// Draw the designator and value for a 2-pin passive, choosing label
    /// placement based on rotation. At R0/R180 the body is horizontal and
    /// labels stack ABOVE it (no wire collision since wires come in from
    /// the sides). At R90/R270 the body is vertical with pins/wires on top
    /// and bottom, so labels are moved LEFT of the body and vertically
    /// centred to avoid the wires.
    /// <para>
    /// <paramref name="bodyTopY"/> is the screen-Y of the body's top in
    /// the unrotated orientation; <paramref name="bodyBottomY"/> is the
    /// bottom. <paramref name="bodyLeftX"/> is the unrotated body's left
    /// edge. <paramref name="midX"/> / <paramref name="midY"/> are the
    /// rotation-aware visual centres (caller computes these from Bounds).
    /// </para>
    /// </summary>
    protected void DrawPassiveLabels(Graphics g, RenderContext ctx,
        float midX, float midY,
        float bodyTopY, float bodyBottomY, float bodyLeftX)
    {
        if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
        {
            DrawDesignatorAndValue(g, ctx, midX, bodyTopY);
            return;
        }

        // R90 / R270: body is vertical. Anchor label block to the LEFT of
        // the visual body, vertically centred on midY. Each line is
        // right-aligned so its right edge sits a small gap from the body.
        using var brush = new SolidBrush(ctx.ForegroundColor);
        var desigSize = g.MeasureString(DisplayDesignator, ctx.LabelFont);

        bool hasLabel = !string.IsNullOrEmpty(Label);
        SizeF labelSize = hasLabel
            ? g.MeasureString(Label, ctx.PinFont)
            : SizeF.Empty;

        float lineGap = hasLabel ? ctx.LineGap : 0f;
        float totalH = desigSize.Height + lineGap + labelSize.Height;

        // Right edge of the text block: a small gap left of the body.
        // Use the rotated visual left edge (caller provides bodyLeftX as
        // the screen-X of the left side of the visual Bounds, NOT the
        // unrotated body edge -- see passive DrawLabels callers).
        float rightEdge = bodyLeftX - ctx.BodyGap;

        float topY = midY - totalH / 2f;

        g.DrawString(DisplayDesignator, ctx.LabelFont, brush,
            rightEdge - desigSize.Width, topY);

        if (hasLabel)
        {
            g.DrawString(Label, ctx.PinFont, brush,
                rightEdge - labelSize.Width,
                topY + desigSize.Height + lineGap);
        }
    }

    /// <summary>
    /// Draw the designator and full part number as a two-line block centred
    /// on (midX, midY) in screen-space pixels.
    /// </summary>
    protected void DrawDesignatorAndPartNumberCentred(Graphics g, RenderContext ctx, float midX, float midY)
    {
        float h = MeasureGateLabelHeight(g, ctx);
        DrawDesignatorAndPartNumber(g, ctx, midX, midY - h / 2);
    }

    /// <summary>
    /// Total height of the two-line gate label block in screen-space pixels.
    /// Used to position the label above the rotated bounding box.
    /// </summary>
    protected float MeasureGateLabelHeight(Graphics g, RenderContext ctx)
    {
        return g.MeasureString(DisplayDesignator, ctx.LabelFont).Height
             + ctx.LineGap
             + g.MeasureString(Device.FullPartNumber, ctx.PinFont).Height;
    }
}