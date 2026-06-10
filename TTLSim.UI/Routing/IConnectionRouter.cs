using System.Collections.Generic;
using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Routing;

/// <summary>
/// Output of a routing pass: the polyline for every connection plus any
/// junction points (cells where 3+ wire segments of the same net meet).
/// Both are transient -- recomputed on every schematic change, cached
/// only for the duration of the next paint/hit-test.
/// </summary>
public sealed record RouteResult(
    IReadOnlyDictionary<Connection, IReadOnlyList<Point>> Polylines,
    IReadOnlyList<Point> Junctions);

/// <summary>
/// Computes the rendered geometry for every connection in a schematic.
/// The router is consulted whenever the schematic changes; its output is
/// cached by the canvas and used for both painting and hit-testing.
///
/// Implementations are stateless: the canvas owns the cache, the router
/// just produces geometry.
/// </summary>
public interface IConnectionRouter
{
    /// <summary>
    /// Produce a polyline for every connection in the schematic, plus a
    /// list of junction points for blob rendering.
    ///
    /// Polyline endpoint contract: each polyline's two endpoints come
    /// from the set { conn.A.WorldPosition, conn.B.WorldPosition, a
    /// junction cell on the same electrical net }. ORDER IS NOT
    /// GUARANTEED -- a trunk leg of a star comes back commonPin-first
    /// regardless of which side of the Connection commonPin is on, and a
    /// branch leg ends at a junction cell on the trunk rather than at
    /// the other pin. Consumers that need to associate a specific end of
    /// the polyline with conn.A vs conn.B must decide by proximity, not
    /// by index. See EasyEdaSheetWriter.BuildEasyEdaPolyline for the
    /// canonical example.
    ///
    /// Intermediate points are bends; segments are axis-aligned.
    /// Implementations must always return a polyline for every
    /// connection -- fall back to a straight line rather than failing if
    /// no preferred route is found.
    /// </summary>
    RouteResult RouteAll(Schematic schematic);
}