namespace TTLSim.Chips;

/// <summary>
/// The one place every part's default propagation/access delay (speed grade)
/// lives, in nanoseconds -- the parallel memories and the GAL/PLD parts.
///
/// <para>
/// Every consumer reads this single table, so the numbers can never drift:
///   • <c>ChipFactory.TryCreateMemory</c> and <c>ChipFactory.TryCreateGal</c>
///     use it for the simulator's delay whenever the device carries no explicit
///     "Propagation Delay (ns)" override.
///   • the property grid (via <c>Device.DefaultDelayNs</c>) displays it, so the
///     user always sees the grade in effect even when they have not overridden it.
///   • the GAL model's own default (<c>Gal.PropagationDelayPs</c>) is derived
///     from <see cref="GalDefaultDelayNs"/> below, so it is this table too.
/// </para>
///
/// Returns <c>null</c> from <see cref="DefaultDelayNs"/> for any identifier with
/// no known default delay.
/// </summary>
public static class PartDelayDefaults
{
    /// <summary>Nominal GAL/PLD tPD in nanoseconds. Exposed as a compile-time
    /// const so the GAL model can use it as its constructor default while the
    /// number still lives only here.</summary>
    public const int GalDefaultDelayNs = 10;

    /// <summary>Default propagation/access delay in nanoseconds for the given
    /// part identifier, or null if it has no known default delay.</summary>
    public static int? DefaultDelayNs(string partIdentifier) => partIdentifier switch
    {
        // 28C-series EEPROM family -- programmed out of band; 250 ns read grade.
        "28C256" or "28C128" or "28C64" or "28C16" => 250,
        // SRAM, by speed grade.
        "62256" => 55,
        "CY7C199" => 15,   // CY7C199-15: 15 ns
        "6116" => 70,   // 6116P-70:  70 ns
        "2114" => 200,  // representative 2114 grade
        // GAL/PLD -- nominal ~10 ns tPD (see GalDefaultDelayNs).
        "GAL16V8" or "GAL20V8" => GalDefaultDelayNs,
        _ => null
    };
}