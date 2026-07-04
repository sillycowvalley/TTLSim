using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// A connection point on a schematic item. LocalPosition is in grid units
/// relative to the item's UNROTATED origin (top-left). Direction is the
/// unrotated facing.
///
/// WorldPosition and Direction return the rotated values, so wire endpoints
/// land on the visually-correct location and the router approaches each
/// pin from the right side.
///
/// <para>
/// <see cref="SwapR90R270"/> is an opt-in flag used by parts whose visual
/// rotation sense differs from TTL Sim's default (clockwise) -- e.g. header
/// connectors, which EasyEDA renders with the opposite handedness so that
/// R90 visually matches what TTL Sim would call R270 and vice versa. When
/// the flag is set, both WorldPosition and Direction transparently swap
/// those two rotations, so the router still sees pin world coordinates
/// that match where the body is drawn.
/// </para>
///
/// <para>
/// LocalPosition and LocalDirection are settable: the header's Mirrored
/// toggle relocates the existing pins to the opposite edge IN PLACE.
/// Connections, the router, and the ribbon-link strands all hold Pin
/// object references, so pins must never be rebuilt after construction --
/// relocating them preserves every reference.
/// </para>
/// </summary>
public sealed class Pin
{
    public string Name { get; }
    public int Number { get; }                 // e.g. pin 14 on a 7400 (0 if N/A)
    public Point LocalPosition { get; set; }   // grid-unit offset from unrotated origin
    public PinDirection LocalDirection { get; set; }

    /// <summary>
    /// When true, R90 and R270 are swapped before being applied to this pin.
    /// Default false (standard TTL Sim rotation sense). Header pins set this
    /// to match EasyEDA's rotation sense.
    /// </summary>
    public bool SwapR90R270 { get; }

    public SchematicItem? Owner { get; internal set; }

    public Pin(string name, int number, Point localPosition, PinDirection direction,
        bool swapR90R270 = false)
    {
        Name = name;
        Number = number;
        LocalPosition = localPosition;
        LocalDirection = direction;
        SwapR90R270 = swapR90R270;
    }

    /// <summary>
    /// World position in grid units, taking the owner's rotation into account.
    /// For rotation=0 this is just owner.Position + LocalPosition.
    /// </summary>
    public Point WorldPosition
    {
        get
        {
            if (Owner is null) return LocalPosition;

            // Rotate LocalPosition around the centre of the unrotated bounding
            // box, then offset by owner.Position.
            int cx = Owner.Size.Width / 2;
            int cy = Owner.Size.Height / 2;
            int lx = LocalPosition.X - cx;
            int ly = LocalPosition.Y - cy;

            Rotation effective = EffectiveRotation(Owner.Rotation);
            (int rx, int ry) = effective switch
            {
                Rotation.R0 => (lx, ly),
                Rotation.R90 => (-ly, lx),
                Rotation.R180 => (-lx, -ly),
                Rotation.R270 => (ly, -lx),
                _ => (lx, ly)
            };

            return new Point(Owner.Position.X + cx + rx,
                             Owner.Position.Y + cy + ry);
        }
    }

    /// <summary>
    /// Effective pin direction in world space, taking owner rotation into
    /// account. The router uses this to know which side to approach the pin
    /// from.
    /// </summary>
    public PinDirection Direction
    {
        get
        {
            if (Owner is null) return LocalDirection;
            return RotateDirection(LocalDirection, EffectiveRotation(Owner.Rotation));
        }
    }

    /// <summary>
    /// Apply the SwapR90R270 flag to an owner rotation. When the flag is
    /// false (the default for every part except headers) this is a no-op.
    /// </summary>
    private Rotation EffectiveRotation(Rotation r)
    {
        if (!SwapR90R270) return r;
        return r switch
        {
            Rotation.R90 => Rotation.R270,
            Rotation.R270 => Rotation.R90,
            _ => r
        };
    }

    private static PinDirection RotateDirection(PinDirection dir, Rotation rot)
    {
        // Map each direction through the rotation. Clockwise 90:
        // Left -> Up, Up -> Right, Right -> Down, Down -> Left.
        int turns = (int)rot / 90;
        for (int i = 0; i < turns; i++)
            dir = dir switch
            {
                PinDirection.Left => PinDirection.Up,
                PinDirection.Up => PinDirection.Right,
                PinDirection.Right => PinDirection.Down,
                PinDirection.Down => PinDirection.Left,
                _ => dir
            };
        return dir;
    }
}

public enum PinDirection
{
    Left,
    Right,
    Up,
    Down
}