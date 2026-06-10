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

    public override string ToString() => $"{Name}={Value}";
}

/// <summary>Identifies a specific pin on a specific item.</summary>
public readonly record struct PinRef(string ItemId, int PinNumber)
{
    public override string ToString() => $"{ItemId}.{PinNumber}";
}