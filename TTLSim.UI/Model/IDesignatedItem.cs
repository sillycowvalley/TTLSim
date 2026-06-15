namespace TTLSim.UI.Model;

/// <summary>
/// A standalone <see cref="SchematicItem"/> that carries an auto-numbered
/// reference designator the way a <see cref="Device"/> does -- e.g. the canned
/// oscillator's "X1", "X2". Items implementing this get a designator assigned
/// on drop (<see cref="Schematic.NextDesignator"/>), kept unique against both
/// devices and other designated items, and round-tripped through save/load.
///
/// <see cref="ReferencePrefix"/> is the letter the running number is appended
/// to ("X"); <see cref="Designator"/> is the full assigned string ("X1").
/// Plain rail markers (VCC, GND) and the abstract clock stimulus (CLK) do NOT
/// implement this -- a designator is meaningless for them.
/// </summary>
public interface IDesignatedItem
{
    /// <summary>Auto-designation prefix, e.g. "X" for oscillator modules.</summary>
    string ReferencePrefix { get; }

    /// <summary>Full reference designator, e.g. "X1". Empty until assigned.</summary>
    string Designator { get; set; }
}