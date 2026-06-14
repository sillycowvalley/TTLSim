using TTLSim.Core;

namespace TTLSim.Chips.Passives;

/// <summary>
/// SPDT (single-pole double-throw) latching switch, "on-on": the common
/// terminal is always tied to exactly one of the two throws. Modelled as the
/// <see cref="SwitchInput"/> contact applied between COM and whichever throw is
/// selected -- each of those two pins is driven to the value its opposite net
/// resolves to (excluding this device's own contribution), so the contact
/// passes a signal either way and tracks live changes. The unselected throw
/// drives nothing (HighZ).
///
/// Pin numbering (shared with the 3-pin jumper renderer):
///   1 = throw A   2 = COM (common)   3 = throw B
///
/// <paramref name="initialThrowB"/> false selects throw A (COM-A closed),
/// true selects throw B (COM-B closed). It latches: the part powers on in
/// whatever position was saved with the schematic and stays there until
/// toggled.
/// </summary>
public sealed class SpdtSwitchInput : IChip
{
    private readonly Net[] nets;       // [0]=throw A (pin 1), [1]=COM (pin 2), [2]=throw B (pin 3)
    private readonly Driver driverA;   // drives throw A's net
    private readonly Driver driverCom; // drives COM's net
    private readonly Driver driverB;   // drives throw B's net

    private bool throwB;
    private readonly bool initialThrowB;

    public SpdtSwitchInput(Net throwANet, Net comNet, Net throwBNet, bool initialThrowB)
    {
        nets = new[] { throwANet, comNet, throwBNet };
        driverA = new Driver(throwANet, DriveStrength.Strong);
        driverCom = new Driver(comNet, DriveStrength.Strong);
        driverB = new Driver(throwBNet, DriveStrength.Strong);
        this.initialThrowB = initialThrowB;
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 1, 2, 3 };

    public IReadOnlyList<Net> Nets => nets;

    /// <summary>Select which throw COM connects to. false = throw A, true = throw B.</summary>
    public void SetThrowB(bool value, IScheduler scheduler)
    {
        if (throwB == value) return;
        throwB = value;
        Apply(scheduler);
    }

    public void Initialize(IScheduler scheduler)
    {
        throwB = initialThrowB;
        Apply(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // The selected contact always conducts, so re-resolve on any change.
        // The exclude-own-driver resolve in Apply() keeps this from looping.
        Apply(scheduler);
    }

    private void Apply(IScheduler scheduler)
    {
        Net comNet = nets[1];
        Net activeNet = throwB ? nets[2] : nets[0];
        Driver activeDrv = throwB ? driverB : driverA;
        Driver inactiveDrv = throwB ? driverA : driverB;

        // Short COM <-> the selected throw. Each driver mirrors the opposite
        // net's value AND strength, excluding its own contribution -- the same
        // fixed-point trick SwitchInput/ButtonInput use to settle in one round.
        Signal comVal = activeNet.Resolve(out DriveStrength comStr, activeDrv);
        Signal actVal = comNet.Resolve(out DriveStrength actStr, driverCom);

        driverCom.Strength = comStr;
        activeDrv.Strength = actStr;
        scheduler.Schedule(0, driverCom, comVal);
        scheduler.Schedule(0, activeDrv, actVal);

        // The unselected throw is open.
        scheduler.Schedule(0, inactiveDrv, Signal.HighZ);
    }
}