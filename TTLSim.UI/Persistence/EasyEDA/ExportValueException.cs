using System;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// A part's VALUE is missing or unparseable at EasyEDA export time. This is a
/// different failure class from <see cref="NotImplementedException"/> (which
/// the catalogue reserves for parts with no export mapping at all): the part
/// is fully supported, the user just needs to fix its Value in the property
/// grid. MainForm shows the two under different headlines.
///
/// The exporter aggregates every value failure in the schematic into ONE of
/// these (messages separated by blank lines), so one dialog names every
/// offending part instead of failing at the first.
/// </summary>
public sealed class ExportValueException : Exception
{
    public ExportValueException(string message) : base(message) { }
}