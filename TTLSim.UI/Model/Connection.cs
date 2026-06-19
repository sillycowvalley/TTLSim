using System;
using System.ComponentModel;
using TTLSim.UI.Components;

namespace TTLSim.UI.Model;

/// <summary>
/// A logical electrical connection between two pins. Carries no geometry --
/// the rendered wire is derived from the two pins' world positions at draw
/// time. Connection identity persists across moves and rotations of the
/// owning items; nothing about a Connection becomes invalid when its
/// endpoints move.
/// </summary>
public sealed class Connection
{
    [Browsable(false)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Browsable(false)]
    public Pin A { get; }

    [Browsable(false)]
    public Pin B { get; }

    [Browsable(false)]
    public bool Selected { get; set; }

    /// <summary>
    /// Render colour for this wire. Mirrors the standard breadboard
    /// jumper palette so power/ground/data lines can carry the same
    /// visual conventions as physical builds. Changes go through the
    /// undo stack via the PropertyGrid's generic SetPropertyCommand path.
    /// </summary>
    [DefaultValue(TTLColor.Black)]
    [Description("Render colour for this wire, mirroring jumper-wire conventions.")]
    public TTLColor Color { get; set; } = TTLColor.Black;

    public Connection(Pin a, Pin b)
    {
        A = a ?? throw new ArgumentNullException(nameof(a));
        B = b ?? throw new ArgumentNullException(nameof(b));
    }
}