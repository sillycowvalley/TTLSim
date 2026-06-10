using System.Drawing;
using TTLSim.Core;

namespace TTLSim.UI.Model;

/// <summary>Styling and context passed to each item's Draw method.</summary>
public sealed class RenderContext
{
    public int GridPitch { get; init; }           // logical units per grid cell (5)
    public float Zoom { get; init; }              // current zoom factor
    public Color ForegroundColor { get; init; } = Color.Black;
    public Color SelectedColor { get; init; } = Color.FromArgb(220, 40, 180);
    public static readonly Color DefaultSelected = Color.FromArgb(220, 40, 180);

    public Color PinColor { get; init; } = Color.Black;
    public Color FillColor { get; init; } = Color.White;

    /// <summary>Font for the primary identifier (R1, U1a, VCC).</summary>
    public Font LabelFont { get; init; } = new Font("Segoe UI", 2.0f);

    /// <summary>Font for the secondary line (74LS00 under a gate; 10K under a passive).</summary>
    public Font PinFont { get; init; } = new Font("Segoe UI", 2.75f);

    /// <summary>Pixel gap between the two stacked text lines.</summary>
    public float LineGap { get; init; } = -1f;

    /// <summary>Pixel gap between the text block and the body of the symbol.</summary>
    public float BodyGap { get; init; } = 1f;

    /// <summary>
    /// Sim-mode lookup: given a SchematicItem (a 7-seg display unit), return its
    /// segments[a..g] + dp state. Null in Edit mode; the unit then renders unlit.
    /// </summary>
    public System.Func<SchematicItem, (bool[] Segments, bool Dp)?>? SegmentProvider { get; init; }

    /// <summary>
    /// Sim-mode lookup: given a SchematicItem (a LED unit), return true if it is
    /// currently lit (anode high, cathode low). Null in Edit mode; the LED then
    /// renders unlit.
    /// </summary>
    public System.Func<SchematicItem, bool>? LedStateProvider { get; init; }

    /// <summary>
    /// Sim-mode lookup: resolved Signal on a specific (item, pin number).
    /// Used by header pin units to colour their per-pin state indicators.
    /// Null in Edit mode; consumers render an "unknown" appearance instead.
    /// </summary>
    public System.Func<SchematicItem, int, Signal?>? SignalStateProvider { get; init; }
}