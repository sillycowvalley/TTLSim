namespace TTLSim.Core;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One message from the build pipeline. Locator fields are optional; when
/// present the UI can pan/select the offending item(s).
///
/// For single-target diagnostics use ItemId/PinNumber/NetId. For multi-
/// target diagnostics (e.g. "wires on this net disagree on colour" → name
/// every connection on the net) use ItemIds and/or ConnectionIds. The
/// single- and multi-target fields are additive: the UI unions them.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,           // stable identifier, e.g. "TTL001", "EDA001"
    string Message,
    string? ItemId = null,
    int? PinNumber = null,
    int? NetId = null,
    IReadOnlyList<string>? ItemIds = null,
    IReadOnlyList<string>? ConnectionIds = null,
    Diagnostic.GridLocation? GridPoint = null)
{
    /// <summary>
    /// A grid-cell locator in TTLSim world coordinates. Used by diagnostics
    /// that don't correspond to a single item or connection (e.g. a router
    /// collision between two wires).
    /// </summary>
    public sealed record GridLocation(int X, int Y);
}