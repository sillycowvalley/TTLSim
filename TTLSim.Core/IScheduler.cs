namespace TTLSim.Core;

/// <summary>
/// What a chip sees of the simulator. Lets the chip schedule future changes
/// to its own output drivers.
/// </summary>
public interface IScheduler
{
    /// <summary>Current simulated tick (picoseconds since the simulator started).</summary>
    long CurrentTick { get; }

    /// <summary>
    /// Schedule the given driver to output a new value at CurrentTick + delay.
    /// The net containing the driver re-resolves when the change applies.
    /// </summary>
    void Schedule(long delay, Driver driver, Signal newOutput);
}