using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>
/// 0/90/180/270 degree rotation, clockwise. Stored as an enum so it can be
/// edited via the property grid; underlying values are degrees.
/// </summary>
public enum Rotation
{
    R0 = 0,
    R90 = 90,
    R180 = 180,
    R270 = 270
}

/// <summary>
/// Base class for anything that lives on the canvas. Position is in grid units;
/// the canvas applies the grid pitch and zoom when rendering.
///
/// Rotation is applied at draw time around the unrotated bounding box's centre.
/// Position always refers to the top-left of the *unrotated* bounding box.
/// Bounds returns the rotated visual extent (used for hit-testing).
/// </summary>
public abstract class SchematicItem
{
    [Browsable(false)]
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

    [Category("Identity")]
    public string Label { get; set; } = "";

    /// <summary>Top-left of the UNROTATED bounding box, in grid units.</summary>
    [Category("Layout")]
    [Browsable(false)]
    public Point Position { get; set; }

    /// <summary>Unrotated bounding box size in grid units. Does not change with rotation.</summary>
    [Category("Layout")]
    [ReadOnly(true)]
    [Browsable(false)]
    public Size Size { get; protected set; }

    /// <summary>Clockwise rotation applied at draw time.</summary>
    [Category("Layout")]
    [Browsable(false)]
    public Rotation Rotation { get; set; } = Rotation.R0;

    /// <summary>
    /// Index into <see cref="Schematic.Layers"/> of the layer this item belongs
    /// to. 0 is the always-visible Default layer. Visibility lives on the layer,
    /// not here -- this stores only WHICH layer the item is on. An out-of-range
    /// value is treated as Default (visible) by the active rule, so old files
    /// and freshly placed items (which default to 0) stay active.
    /// </summary>
    [Browsable(false)]
    public int LayerId { get; set; }

    /// <summary>
    /// Footprint the router treats as blocked. Defaults to the visual bounds;
    /// components can inflate this to keep wires from running right up against
    /// the body or overlapping pin stubs.
    /// </summary>
    [Browsable(false)]
    public virtual Rectangle RoutingBounds => Bounds;

    [Browsable(false)]
    public List<Pin> Pins { get; } = new();

    [Browsable(false)]
    public bool Selected { get; set; }

    protected SchematicItem()
    {
    }

    protected void AddPin(Pin pin)
    {
        pin.Owner = this;
        Pins.Add(pin);
    }

    /// <summary>
    /// Visual bounding rectangle in grid units, taking rotation into account.
    /// For 90/270 rotations the width/height are swapped and the rectangle
    /// is repositioned so its centre matches the unrotated centre.
    ///
    /// Virtual so an item whose drawn extent is not captured by Size alone
    /// (a net label, whose bit names overhang the box) can widen the hit-test
    /// rectangle to match what it actually draws.
    /// </summary>
    [Browsable(false)]
    public virtual Rectangle Bounds
    {
        get
        {
            if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
                return new Rectangle(Position, Size);

            // 90 or 270: width and height swap. The visual centre must equal
            // the unrotated centre, so the rotated top-left shifts by half
            // the difference between width and height.
            int cx = Position.X + Size.Width / 2;
            int cy = Position.Y + Size.Height / 2;
            int w = Size.Height;
            int h = Size.Width;
            return new Rectangle(cx - w / 2, cy - h / 2, w, h);
        }
    }

    /// <summary>
    /// Centre of the unrotated bounding box, in grid units. This is the
    /// rotation pivot.
    /// </summary>
    [Browsable(false)]
    public Point Pivot => new(Position.X + Size.Width / 2,
                              Position.Y + Size.Height / 2);

    /// <summary>
    /// Render the item. Coordinates passed to Graphics are in grid units --
    /// the canvas has already applied scale and pan transforms.
    /// </summary>
    public abstract void Draw(Graphics g, RenderContext ctx);
}