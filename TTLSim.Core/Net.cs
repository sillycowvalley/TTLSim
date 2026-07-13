namespace TTLSim.Core;

/// <summary>
/// A wired-together set of pins sharing a common logic value.
/// Built by NetTable from connections; driven by Driver contributions.
/// </summary>
public sealed class Net
{
    private readonly List<Driver> drivers = new();
    private readonly List<PinRef> listenerPins = new();
    private readonly List<PinRef> pins = new();

    public IReadOnlyList<PinRef> Pins => pins;

    internal void AddPin(PinRef pin) => pins.Add(pin);

    public Net(int id, string? name = null)
    {
        Id = id;
        Name = name ?? $"N{id}";
    }

    public int Id { get; }

    public string Name { get; }

    /// <summary>Current resolved value on this net.</summary>
    public Signal Value { get; internal set; } = Signal.Unknown;

    /// <summary>Simulated tick at which Value last changed. -1 if never.</summary>
    public long LastChangeTick { get; internal set; } = -1;

    /// <summary>
    /// Current electrical fault on this net, or null if the net is healthy.
    /// Maintained by the Simulator, which re-runs <see cref="DetectFault"/>
    /// every time a driver on this net changes and raises its
    /// ElectricalFault event on the rising edge of the faulted state.
    /// </summary>
    public NetFault? Fault { get; internal set; }

    public IReadOnlyList<Driver> Drivers => drivers;

    public IReadOnlyList<PinRef> ListenerPins => listenerPins;

    internal void AddDriver(Driver d) => drivers.Add(d);

    internal void AddListenerPin(PinRef pin) => listenerPins.Add(pin);

    /// <summary>
    /// Resolve the net's value from all driver contributions.
    /// Strong drivers decide; if none, weak drivers decide; if none, HighZ.
    /// Conflict among same-strength active drivers -> Unknown.
    /// Pass <paramref name="exclude"/> to resolve as if that driver weren't
    /// contributing -- used by pass-through contacts (buttons, switches) to
    /// sample the opposite net without seeing their own drive.
    /// </summary>
    public Signal Resolve(Driver? exclude = null)
        => Resolve(out _, exclude);

    /// <summary>
    /// Resolve as <see cref="Resolve(Driver?)"/>, and also report the
    /// strength of the tier that decided the value. Pass-through contacts
    /// use this to mirror not just the value but the strength of the net
    /// they're passing -- a closed contact relaying a weak pull-up must
    /// itself drive weakly, or it would wrongly beat a strong driver on
    /// the far side.
    ///
    /// When the result is HighZ (nothing driving) the reported strength is
    /// Weak; when the result is Unknown (a conflict) the strength is that
    /// of the conflicting tier, but callers should not lean on it.
    /// </summary>
    public Signal Resolve(out DriveStrength strength, Driver? exclude = null)
    {
        Signal strong = ResolveTier(DriveStrength.Strong, exclude);
        if (strong != Signal.HighZ)
        {
            strength = DriveStrength.Strong;
            return strong;
        }

        Signal medium = ResolveTier(DriveStrength.Medium, exclude);
        if (medium != Signal.HighZ)
        {
            strength = DriveStrength.Medium;
            return medium;
        }

        strength = DriveStrength.Weak;
        return ResolveTier(DriveStrength.Weak, exclude);
    }

    private Signal ResolveTier(DriveStrength tier, Driver? exclude)
    {
        Signal result = Signal.HighZ;
        bool seen = false;

        foreach (Driver d in drivers)
        {
            if (ReferenceEquals(d, exclude)) continue;
            if (d.Strength != tier) continue;
            if (d.Output == Signal.HighZ) continue;

            if (!seen)
            {
                result = d.Output;
                seen = true;
            }
            else if (d.Output != result)
            {
                return Signal.Unknown;
            }
        }

        return result;
    }

    /// <summary>Tiers in the order <see cref="Resolve(Driver?)"/> consults them.</summary>
    private static readonly DriveStrength[] Tiers =
    {
        DriveStrength.Strong, DriveStrength.Medium, DriveStrength.Weak
    };

    /// <summary>
    /// Scan the drivers for a hard electrical contention: two or more drivers
    /// in the *deciding* tier asserting opposite definite levels (one High,
    /// one Low). Returns null when the net is electrically sound.
    ///
    /// Deciding tier: the highest-strength tier with any driver out of HighZ,
    /// exactly as <see cref="Resolve(Driver?)"/> picks it. A strong output
    /// beating an opposing pull-up is not a fault (that's what pull resistors
    /// are for); two opposing pull-ups with nothing strong on the net is.
    ///
    /// Drivers whose Output is <see cref="Signal.Unknown"/> are counted as
    /// occupying the tier but never as a conflicting level. An Unknown output
    /// means "this chip doesn't know yet" (its own inputs are unresolved), not
    /// "this chip is pulling the other way" -- treating it as a conflict would
    /// fire a spurious fault on every tri-state bus during power-up and on
    /// every OE handoff.
    ///
    /// This is deliberately capability-free: it detects contention that is
    /// actually happening rather than guessing from part numbers, so it works
    /// on tri-state buses where a static check cannot. The static counterpart
    /// is SchematicBuilder's TTL005, which catches two totem-pole outputs wired
    /// together before the simulation is even built.
    /// </summary>
    public NetFault? DetectFault()
    {
        foreach (DriveStrength tier in Tiers)
        {
            int active = 0, high = 0, low = 0;

            foreach (Driver d in drivers)
            {
                if (d.Strength != tier) continue;
                if (d.Output == Signal.HighZ) continue;

                active++;
                if (d.Output == Signal.High) high++;
                else if (d.Output == Signal.Low) low++;
            }

            if (active == 0) continue;   // nothing in this tier -- fall through

            // This tier decides the net. It's a fault only if it's internally
            // contradictory; either way we stop here, because lower tiers are
            // overridden and their disagreements are electrically irrelevant.
            return high > 0 && low > 0
                ? new NetFault(Id, tier, high, low)
                : null;
        }

        return null;   // nothing driving at all -- HighZ, not a fault
    }

    public override string ToString() => $"{Name}={Value}";
}

/// <summary>
/// An electrical contention on a net: within one drive-strength tier, some
/// drivers are pulling High while others pull Low. On real silicon this is a
/// short from VCC to GND through two output stages -- tens of milliamps, a
/// logic level somewhere in the forbidden band, and eventually dead chips.
/// In the simulator the net resolves to <see cref="Signal.Unknown"/>, which
/// on its own is silent (Unknown is also the initial value), so this record
/// exists to make the condition observable rather than merely inferable.
/// </summary>
/// <param name="NetId">Net the contention is on.</param>
/// <param name="Strength">The tier that is fighting itself.</param>
/// <param name="HighDrivers">How many drivers in that tier assert High.</param>
/// <param name="LowDrivers">How many drivers in that tier assert Low.</param>
public sealed record NetFault(
    int NetId,
    DriveStrength Strength,
    int HighDrivers,
    int LowDrivers);

/// <summary>Identifies a specific pin on a specific item.</summary>
public readonly record struct PinRef(string ItemId, int PinNumber)
{
    public override string ToString() => $"{ItemId}.{PinNumber}";
}