using System.Collections.Generic;
using System.Drawing;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

/// <summary>
/// Single-bend orthogonal router. For each connection, exits pin A in its
/// facing direction, then turns once to reach pin B. If A and B already
/// share an axis, the result is a straight line with no bend.
///
/// Ignores other components entirely: wires will happily pass through
/// chip bodies. Useful as a control for comparison against more complex
/// routers, not as a real router.
/// </summary>
public sealed class LShapeRouter : IConnectionRouter
{
    public RouteResult RouteAll(Schematic schematic)
    {
        var polylines = new Dictionary<Connection, IReadOnlyList<Point>>(
            schematic.Connections.Count);
        foreach (var c in schematic.Connections)
            polylines[c] = RouteOne(c);
        return new RouteResult(polylines, System.Array.Empty<Point>());
    }

    private static IReadOnlyList<Point> RouteOne(Connection c)
    {
        var a = c.A.WorldPosition;
        var b = c.B.WorldPosition;

        if (a.X == b.X || a.Y == b.Y)
            return new[] { a, b };

        bool horizontalFirst = c.A.Direction == PinDirection.Left
                            || c.A.Direction == PinDirection.Right;

        Point bend = horizontalFirst
            ? new Point(b.X, a.Y)
            : new Point(a.X, b.Y);

        return new[] { a, bend, b };
    }
}