using TTLSim.Core;

namespace TTLSim.Chips.Pld;

/// <summary>
/// GAL/PLD evaluator for all three device configurations. Given a device
/// geometry (<see cref="GalDevice"/>) and a fuse array (from a parsed JEDEC
/// map), it evaluates each OLMC and drives the corresponding output pins.
/// The mode is decoded from the SYN/AC0 fuses (galasm MODE1/2/3):
///
///   SIMPLE (SYN=1 AC0=0)
///     Every OLMC is a plain combinational output: 8 product terms ORed,
///     XOR polarity fuse, always enabled. An erased (all-intact) block is a
///     pure input and drives nothing.
///
///   COMPLEX (SYN=1 AC0=1)
///     Row 0 of each OLMC block is its OUTPUT-ENABLE product term; rows 1..7
///     are the logic. An all-blown OE row (a product of nothing) is always
///     enabled; a programmed OE row tristates the pin whenever the term is
///     false. A disabled pin can be driven externally, and its value feeds
///     back through the mode-2 column map (sampled from the net as usual).
///
///   REGISTERED (SYN=0 AC0=1)
///     Pin 1 is the common clock and <see cref="GalDevice.OePin"/> the common
///     active-low /OE for the registered cells. Per-OLMC AC1 selects the cell
///     type: AC1=0 is a registered cell (all 8 rows are logic, latched on the
///     rising edge of CLK, driven while /OE is low); AC1=1 is a combinational
///     cell exactly like a complex-mode one (OE term in row 0, 7 logic rows).
///
/// Registered-cell model (matches the GALasm/BlinkyJED fuse conventions,
/// which program feedback literals as pin-level literals -- validated
/// byte-for-byte against WinCUPL):
///   - The stored state is the PIN-LEVEL value: on a rising clock edge the
///     cell latches (sum-of-products XOR polarity), evaluated from the
///     pre-edge state so simultaneous updates (counters, shifters) are
///     consistent.
///   - Feedback for a registered cell taps the register, not the pin, so it
///     stays correct while /OE holds the pin released or something external
///     drives it. (On silicon the feedback is /Q and the pin buffer inverts Q,
///     so feedback == pin level -- the same invariant.)
///   - Power-up reset per the ATF16V8B/GAL16V8 datasheets: registers reset
///     low, so every registered OUTPUT starts HIGH (and so does its
///     feedback), regardless of the XOR polarity fuse.
///   - Only a Low -> High transition counts as a rising edge; a clock
///     appearing High from an unresolved state does not clock the registers.
///     An Unknown /OE drives Unknown; /OE low drives the state; high releases.
///
/// Timing: combinational outputs use <see cref="PropagationDelayPs"/> (tPD);
/// register outputs after a clock edge use <see cref="ClockToOutputDelayPs"/>
/// (tCO). Combinational cells that consume register feedback re-evaluate one
/// tPD after the registers settle (tCO + tPD from the edge). The factory
/// overrides both from the part's properties when set.
/// </summary>
public sealed class Gal : IChip
{
    public const long PropagationDelayPs = 10_000;    // nominal GAL tPD ~10 ns
    public const long ClockToOutputDelayPs = 10_000;  // nominal GAL tCO ~10 ns

    private enum GalMode { Simple, Complex, Registered }

    private readonly GalDevice device;
    private readonly bool[] fuses;
    private readonly int[] arrayInputPins;   // line -> pin, resolved for the device mode
    private readonly Net[] nets;             // index == position in PinNumbers
    private readonly int[] pinNumbers;
    private readonly Dictionary<int, int> pinIndex = new();       // pin number -> nets index
    private readonly Dictionary<int, Driver> olmcDrivers = new(); // pin number -> driver
    private readonly bool[] isArrayInputPin; // nets index -> pin feeds the AND array in this mode
    private readonly long propagationDelayPs;
    private readonly long clockToOutputDelayPs;

    private readonly GalMode mode;
    private readonly bool[] olmcIsRegistered;              // by OLMC array index
    private readonly Signal[] registerState;               // pin-level latched value
    private readonly Dictionary<int, int> registeredOlmcByPin = new();   // feedback override
    private Signal previousClock = Signal.Unknown;

    /// <param name="device">Device geometry.</param>
    /// <param name="fuses">Parsed fuse array (length device.FuseCount).</param>
    /// <param name="netByPin">Net attached to each signal pin (power pins excluded).</param>
    /// <param name="propagationDelayPs">Combinational propagation delay (tPD) in
    /// picoseconds. Defaults to <see cref="PropagationDelayPs"/>; the factory
    /// passes the part's explicit Propagation Delay when one is set.</param>
    /// <param name="clockToOutputDelayPs">Registered clock-to-output delay (tCO)
    /// in picoseconds. Defaults to <see cref="ClockToOutputDelayPs"/>; the
    /// factory passes the part's explicit Clock to Output when one is set.</param>
    public Gal(GalDevice device, bool[] fuses, IReadOnlyDictionary<int, Net> netByPin,
               long propagationDelayPs = PropagationDelayPs,
               long clockToOutputDelayPs = ClockToOutputDelayPs)
    {
        this.device = device;
        this.fuses = fuses;
        this.propagationDelayPs = propagationDelayPs;
        this.clockToOutputDelayPs = clockToOutputDelayPs;

        // The column->pin routing is mode-dependent; the mode is encoded by the
        // SYN and AC0 fuses (galasm MODE1/2/3). SYN=0 AC0=0 is invalid and
        // falls back to simple, matching ColumnMapForMode.
        bool syn = device.SynFuse < fuses.Length && fuses[device.SynFuse];
        bool ac0 = device.Ac0Fuse < fuses.Length && fuses[device.Ac0Fuse];
        mode = !syn && ac0 ? GalMode.Registered
             : syn && ac0 ? GalMode.Complex
             : GalMode.Simple;
        arrayInputPins = device.ColumnMapForMode(syn, ac0);

        pinNumbers = netByPin.Keys.OrderBy(p => p).ToArray();
        nets = new Net[pinNumbers.Length];
        for (int i = 0; i < pinNumbers.Length; i++)
        {
            nets[i] = netByPin[pinNumbers[i]];
            pinIndex[pinNumbers[i]] = i;
        }

        // Which of our pins feed the AND array in this mode. This includes OLMC
        // pins with feedback: a change on such a pin (even one we drive) must
        // re-evaluate, or combinational feedback (mode 2) goes stale.
        isArrayInputPin = new bool[pinNumbers.Length];
        foreach (int pin in arrayInputPins)
            if (pin != 0 && pinIndex.TryGetValue(pin, out int idx))
                isArrayInputPin[idx] = true;

        // One driver per OLMC output pin that is actually present on the net map.
        foreach (int pin in device.OlmcOutputPins)
        {
            if (pinIndex.TryGetValue(pin, out int idx))
                olmcDrivers[pin] = new Driver(nets[idx], DriveStrength.Strong);
        }

        // Registered mode: classify each OLMC from its AC1 fuse (0 = registered,
        // 1 = combinational/spare). AC1 for the OLMC on pin P sits at
        // Ac1FuseBase + (7 - (P - FirstOlmcPin)) -- the same addressing
        // GalPinModel uses. Registers power up with the OUTPUT high (datasheet
        // power-up reset), so the stored pin-level state starts High.
        olmcIsRegistered = new bool[device.OlmcCount];
        registerState = new Signal[device.OlmcCount];
        if (mode == GalMode.Registered)
        {
            for (int o = 0; o < device.OlmcCount; o++)
            {
                int pin = device.OlmcOutputPins[o];
                int ac1Addr = device.Ac1FuseBase + (7 - (pin - device.FirstOlmcPin));
                bool ac1 = ac1Addr >= 0 && ac1Addr < fuses.Length && fuses[ac1Addr];
                if (!ac1)
                {
                    olmcIsRegistered[o] = true;
                    registerState[o] = Signal.High;
                    registeredOlmcByPin[pin] = o;
                }
            }
        }
    }

    public IReadOnlyList<int> PinNumbers => pinNumbers;
    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        if (mode == GalMode.Registered)
            previousClock = SampleNet(device.ClockPin);
        EvaluateAll(scheduler);
    }

    public void OnInputChanged(int pinIndexChanged, IScheduler scheduler)
    {
        int pin = pinNumbers[pinIndexChanged];

        if (mode == GalMode.Registered)
        {
            if (pin == device.ClockPin) { OnClockChanged(scheduler); return; }
            if (pin == device.OePin) { DriveRegisteredOutputs(scheduler, propagationDelayPs); return; }
            if (!isArrayInputPin[pinIndexChanged]) return;
            EvaluateCombinationalOlmcs(scheduler, propagationDelayPs);
            return;
        }

        // Simple/complex: re-evaluate only when a pin that feeds the array
        // changed. Output-only pins (no feedback line in this mode) are skipped.
        if (!isArrayInputPin[pinIndexChanged]) return;
        EvaluateAll(scheduler);
    }

    private void EvaluateAll(IScheduler scheduler)
    {
        if (mode == GalMode.Registered)
        {
            DriveRegisteredOutputs(scheduler, propagationDelayPs);
            EvaluateCombinationalOlmcs(scheduler, propagationDelayPs);
            return;
        }

        for (int o = 0; o < device.OlmcCount; o++)
            ScheduleOlmc(o, EvaluateCombinationalOlmc(o), scheduler, propagationDelayPs);
    }

    // ---- Registered mode ---------------------------------------------------

    private void OnClockChanged(IScheduler scheduler)
    {
        Signal now = SampleNet(device.ClockPin);
        Signal prev = previousClock;
        previousClock = now;

        // Only a clean Low -> High transition is a rising edge. A clock coming
        // out of an unresolved state does not clock the registers.
        if (prev != Signal.Low || now != Signal.High) return;

        // Latch: every D is evaluated from the PRE-edge register state, then
        // all cells commit together -- counters and shifters depend on this.
        Signal[] next = new Signal[device.OlmcCount];
        for (int o = 0; o < device.OlmcCount; o++)
            if (olmcIsRegistered[o])
                next[o] = PolarityAdjusted(o, EvaluateSum(
                    o * device.ProductTermsPerOlmc, device.ProductTermsPerOlmc));
        for (int o = 0; o < device.OlmcCount; o++)
            if (olmcIsRegistered[o])
                registerState[o] = next[o];

        DriveRegisteredOutputs(scheduler, clockToOutputDelayPs);

        // Combinational cells that consume register feedback see the new state
        // one array pass after the registers settle.
        EvaluateCombinationalOlmcs(scheduler, clockToOutputDelayPs + propagationDelayPs);
    }

    // Drive every registered cell from its stored state, gated by the common
    // active-low /OE: low = drive, high = release, unresolved = Unknown.
    private void DriveRegisteredOutputs(IScheduler scheduler, long delayPs)
    {
        Signal oe = SampleNet(device.OePin);
        for (int o = 0; o < device.OlmcCount; o++)
        {
            if (!olmcIsRegistered[o]) continue;
            Signal value = oe == Signal.Low ? registerState[o]
                         : oe == Signal.High ? Signal.HighZ
                         : Signal.Unknown;
            ScheduleOlmc(o, value, scheduler, delayPs);
        }
    }

    private void EvaluateCombinationalOlmcs(IScheduler scheduler, long delayPs)
    {
        for (int o = 0; o < device.OlmcCount; o++)
            if (!olmcIsRegistered[o])
                ScheduleOlmc(o, EvaluateCombinationalOlmc(o), scheduler, delayPs);
    }

    private void ScheduleOlmc(int olmc, Signal value, IScheduler scheduler, long delayPs)
    {
        if (olmcDrivers.TryGetValue(device.OlmcOutputPins[olmc], out Driver? driver))
            scheduler.Schedule(delayPs, driver, value);
    }

    // ---- Combinational evaluation -------------------------------------------

    private Signal EvaluateCombinationalOlmc(int olmc)
    {
        int firstRow = olmc * device.ProductTermsPerOlmc;

        // Output vs input: an erased / input-configured OLMC has an all-intact
        // block (every fuse 0, the erased state). Any OLMC configured to drive
        // has at least one blown fuse.
        if (!BlockHasBlownFuse(firstRow)) return Signal.HighZ;

        int logicFirst = firstRow;
        int logicCount = device.ProductTermsPerOlmc;

        // Complex mode -- and a combinational cell inside registered mode --
        // give row 0 to the output-enable term. An all-blown OE row is a
        // product of nothing and always enables; a programmed one tristates
        // the pin whenever the term is false.
        if (mode != GalMode.Simple)
        {
            (bool used, bool value, bool oeUnknown) = EvaluateProductTerm(firstRow);
            if (used)
            {
                if (oeUnknown) return Signal.Unknown;
                if (!value) return Signal.HighZ;
            }
            logicFirst = firstRow + 1;
            logicCount--;
        }

        return PolarityAdjusted(olmc, EvaluateSum(logicFirst, logicCount));
    }

    // OR the used product terms in [firstRow, firstRow + rowCount).
    private (bool Sum, bool Unknown) EvaluateSum(int firstRow, int rowCount)
    {
        bool unknown = false;
        for (int t = 0; t < rowCount; t++)
        {
            (bool used, bool value, bool termUnknown) = EvaluateProductTerm(firstRow + t);
            if (!used) continue;                    // all-blown row: no literals
            if (termUnknown) { unknown = true; continue; }
            if (value) return (true, false);        // OR: one true term is enough
        }
        return (false, unknown);   // nothing true; unknown if a deciding term was unresolved
    }

    private Signal PolarityAdjusted(int olmc, (bool Sum, bool Unknown) result)
    {
        if (!result.Sum && result.Unknown) return Signal.Unknown;
        bool polarity = fuses[device.XorFuseBase + olmc];   // 1 = active high, 0 = active low
        return (result.Sum ^ !polarity) ? Signal.High : Signal.Low;
    }

    // An OLMC drives its pin only if some fuse in its row block is blown. An
    // all-intact block is the erased/input state and leaves the pin released.
    private bool BlockHasBlownFuse(int firstRow)
    {
        int start = firstRow * device.Cols;
        int end = start + device.ProductTermsPerOlmc * device.Cols;
        for (int i = start; i < end; i++)
            if (fuses[i]) return true;
        return false;
    }

    // Returns (term used at all, term value, term depends on an unresolved input).
    private (bool Used, bool Value, bool Unknown) EvaluateProductTerm(int row)
    {
        int baseIdx = row * device.Cols;
        bool used = false;
        bool unknown = false;

        for (int line = 0; line < device.Cols / 2; line++)
        {
            bool trueIntact = !fuses[baseIdx + device.TrueColumn(line)];
            bool compIntact = !fuses[baseIdx + device.ComplementColumn(line)];
            if (!trueIntact && !compIntact) continue;   // input not used in this term
            used = true;

            // Both polarities connected -> literal AND its complement -> 0.
            // (This is how an erased/unused product term reads as constant 0.)
            if (trueIntact && compIntact) return (true, false, false);

            int pin = line < arrayInputPins.Length ? arrayInputPins[line] : 0;
            Signal s = SamplePin(pin);

            if (trueIntact)
            {
                if (s == Signal.Low) return (true, false, false);   // literal false -> AND is 0
                if (s != Signal.High) unknown = true;
            }
            if (compIntact)
            {
                if (s == Signal.High) return (true, false, false);  // !literal false -> AND is 0
                if (s != Signal.Low) unknown = true;
            }
        }

        if (!used) return (false, false, false);        // all-blown row: no literals -> skipped
        return (true, !unknown, unknown);               // all connected literals satisfied
    }

    // Sample an array-input line. A registered OLMC's feedback taps its
    // register, not its pin -- the pin may be released (/OE high) or driven
    // externally while the internal state marches on.
    private Signal SamplePin(int pin)
    {
        if (pin == 0) return Signal.Unknown;
        if (registeredOlmcByPin.TryGetValue(pin, out int olmc))
            return registerState[olmc];
        return SampleNet(pin);
    }

    private Signal SampleNet(int pin)
    {
        if (pin != 0 && pinIndex.TryGetValue(pin, out int idx))
        {
            Signal s = nets[idx].Value;
            return s == Signal.HighZ ? Signal.Unknown : s;
        }
        return Signal.Unknown;
    }
}