using System.Collections.Generic;
using TTLSim.Core;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// Outcome of an EasyEDA Pro export pass. Carries any non-fatal diagnostics
/// (wire-colour mismatches within a net, router/exporter pin-position
/// mismatches, etc.) for the UI to surface in the output panel.
///
/// Hard failures (unsupported parts, IO errors) still throw — this record
/// is only for "the file was produced, but you should know about these".
/// </summary>
public sealed record EasyEDAExportResult(
    IReadOnlyList<Diagnostic> Diagnostics);