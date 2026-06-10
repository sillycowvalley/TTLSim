namespace TTLSim.Core;

public sealed class BuildResult
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public NetTable? NetTable { get; }

    /// <summary>Constructed simulator instance. Null when Succeeded is false.</summary>
    public Simulator? Simulator { get; }

    public bool Succeeded => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public int ErrorCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

    internal BuildResult(
        IReadOnlyList<Diagnostic> diagnostics,
        NetTable? netTable,
        Simulator? simulator)
    {
        Diagnostics = diagnostics;
        NetTable = netTable;
        Simulator = simulator;
    }
}