using TTLSim.Core;

namespace TTLSim.Chips.Sources;

/// <summary>
/// Dallas/Maxim DS1813 "5V EconoReset with Pushbutton" -- a 3-pin reset
/// supervisor. Pin 1 = /RST, pin 2 = VCC, pin 3 = GND.
///
/// /RST is open-drain with an internal ~5.5 kOhm pull-up, and the same pin
/// doubles as a pushbutton-reset sense node. This model represents that with
/// two drivers on the /RST net:
///   - a permanent WEAK High driver (the internal pull-up), and
///   - a STRONG driver that pulls /RST Low while a reset is asserted and goes
///     HighZ (released) otherwise (the open-drain output transistor).
///
/// Behaviour modelled:
///   * Power-on reset. This four-state engine presents VCC as valid from tick
///     0 (there is no supply ramp), so "power good" == simulation start: /RST
///     is driven Low at tick 0 and released ~150 ms later.
///   * Pushbutton. While /RST is released, an external strong Low on the pin
///     (e.g. a pushbutton to GND) is sensed; when that external Low is
///     removed, /RST is held Low for a further ~150 ms, matching the part's
///     "reset on button release" behaviour.
///
/// The expiry of a timed Low is detected by time, not by a dedicated event:
/// the scheduled release lets the pull-up win, the net rises, and the engine
/// calls OnInputChanged on this chip's own pin -- the same self-notification
/// the ClockSource relies on to re-arm.
///
/// Known limitation: a button held continuously across the power-on window
/// and released afterwards does not get its own post-release hold. Sensing an
/// external Low while we are ourselves driving Low produces no net-value
/// change, and so no event in this engine. Every ordinary use (power-on, or a
/// button pressed during normal running) is modelled correctly.
///
/// VCC brown-out is not modelled; VCC never drops in this simulator.
/// </summary>
public sealed class Ds1813 : IChip
{
    /// <summary>Reset active-Low hold time. Datasheet: ~150 ms.</summary>
    public const long ResetHoldPs = 150_000_000_000L; // 150 ms in picoseconds

    private readonly Net rst;
    private readonly Driver pullUp;    // internal ~5.5k pull-up: weak, always High
    private readonly Driver resetOut;  // open-drain transistor: strong Low / HighZ

    // Tick at which the current timed assertion releases; -1 when not in a
    // timed hold (idle / waiting on a button).
    private long holdEndsAt = -1;

    // True once an external Low (button) has been sensed and we are waiting
    // for it to be released before starting the post-release hold.
    private bool buttonLatched;

    public Ds1813(Net rst)
    {
        this.rst = rst;
        pullUp = new Driver(rst, DriveStrength.Weak);
        resetOut = new Driver(rst, DriveStrength.Strong);
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 1 };

    public IReadOnlyList<Net> Nets => new[] { rst };

    public void Initialize(IScheduler scheduler)
    {
        // Internal pull-up is always present.
        scheduler.Schedule(0, pullUp, Signal.High);

        // Power-on reset: assert now, release after the hold time.
        holdEndsAt = scheduler.CurrentTick + ResetHoldPs;
        buttonLatched = false;
        scheduler.Schedule(0, resetOut, Signal.Low);
        scheduler.Schedule(ResetHoldPs, resetOut, Signal.HighZ);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // /RST is our only pin.
        if (pinIndex != 0) return;

        if (holdEndsAt >= 0)
        {
            // We are in a timed Low assertion. The meaningful event is its
            // expiry, detected by time: our scheduled release fires, the
            // pull-up wins, the net rises, and we are called back here.
            if (scheduler.CurrentTick >= holdEndsAt)
            {
                holdEndsAt = -1;
                // If a button is *still* holding the pin Low at release time,
                // latch it so its own release starts a fresh hold.
                buttonLatched = ExternalLow();
            }
            return;
        }

        // Released (resetOut HighZ). Watch the pin for a pushbutton.
        bool externalLow = ExternalLow();

        if (externalLow && !buttonLatched)
        {
            // Button just pulled the pin Low; it holds the line itself.
            buttonLatched = true;
        }
        else if (!externalLow && buttonLatched)
        {
            // Button released: hold reset Low for the post-release time.
            buttonLatched = false;
            holdEndsAt = scheduler.CurrentTick + ResetHoldPs;
            scheduler.Schedule(0, resetOut, Signal.Low);
            scheduler.Schedule(ResetHoldPs, resetOut, Signal.HighZ);
        }
    }

    // True when something other than our own drivers is pulling /RST strongly
    // Low. We exclude our open-drain driver; the weak pull-up never reaches
    // the Strong tier, so it can't be mistaken for an external pull.
    private bool ExternalLow()
    {
        Signal s = rst.Resolve(out DriveStrength strength, exclude: resetOut);
        return s == Signal.Low && strength == DriveStrength.Strong;
    }
}