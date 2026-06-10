namespace TTLSim.Core;

public interface IChipFactory
{
    IChip? CreateForDevice(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet);

    IChip? CreateForItem(BuildItem item, IReadOnlyDictionary<int, Net> pinToNet);

    /// <summary>
    /// Create one or more IChips for the device's units. powerNets maps each
    /// power-rail net id to its constant value (High for VCC, Low for GND),
    /// so passives can decide pull-up/pull-down vs series-wire behaviour.
    /// </summary>
    IEnumerable<IChip> CreateForUnits(
        BuildDevice device,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, Net>> unitPinMaps,
        IReadOnlyDictionary<int, Signal> powerNets);

    /// <summary>
    /// True if this factory has a simulation model for the given device, or
    /// if the device is a visual-only part with no electrical effect (LEDs,
    /// capacitors, crystals). False for placeable parts that the factory has
    /// no model for yet -- the builder turns those into a TTL020 error so
    /// the user is not silently handed an incomplete simulator.
    /// </summary>
    bool IsSimulated(BuildDevice device);
}