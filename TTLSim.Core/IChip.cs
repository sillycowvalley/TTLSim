namespace TTLSim.Core;

/// <summary>
/// A simulation model for a chip, passive, or signal source. The engine
/// notifies the chip whenever a net it listens on changes; the chip
/// updates its internal state and schedules its outputs through the
/// IScheduler.
/// </summary>
public interface IChip
{
    /// <summary>
    /// Pin numbers this chip cares about, in arbitrary stable order.
    /// The engine uses this list as the index into <see cref="Nets"/>.
    /// </summary>
    IReadOnlyList<int> PinNumbers { get; }

    /// <summary>
    /// Nets attached to each pin, in the same order as PinNumbers.
    /// Populated by the engine during chip binding (Step 5 onward).
    /// </summary>
    IReadOnlyList<Net> Nets { get; }

    /// <summary>
    /// Called once at simulation start (tick 0) so the chip can establish
    /// its initial outputs. Power sources drive their rails here; other
    /// chips can leave their outputs at HighZ until inputs settle.
    /// </summary>
    void Initialize(IScheduler scheduler);

    /// <summary>
    /// Called when one of the chip's input nets has just changed value.
    /// The chip recomputes its outputs and schedules any resulting changes
    /// through the scheduler.
    /// </summary>
    /// <param name="pinIndex">Index into PinNumbers/Nets of the pin whose net changed.</param>
    void OnInputChanged(int pinIndex, IScheduler scheduler);
}