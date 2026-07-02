using TTLSim.Core;

namespace TTLSim.Chips.Pld;

/// <summary>
/// GAL22V10 / ATF22V10 evaluator, the per-macrocell-configured sibling of
/// <see cref="Gal"/>. Geometry and semantics per <see cref="Gal22V10Device"/>;
/// the model was verified assertion-by-assertion against BlinkyJED fuse maps
/// that are themselves WinCUPL-gold- and silicon-validated.
///
/// Per-OLMC model (no global modes; S0/S1 decode each cell):
///   - Every cell -- registered or combinational -- is gated by its own OE
///     product term (block row 0). All-blown = always enabled; a programmed
///     term tristates the pin whenever it is false; an erased cell's
///     all-intact OE row can never be true, so it never drives -- no special
///     erased-block handling is needed.
///   - Combinational cell (S1=1): sum of its logic rows, S0 polarity, pin
///     feedback (sampled from the net, so a tri-stated cell reads whatever
///     drives it externally -- same machinery as V8 mode-2 feedback).
///   - Registered cell (S1=0): the register stores the LOGICAL value Q (the
///     array is always positive logic; S0 selects Q or /Q at the output
///     buffer). Feedback taps /Q -- polarity-independent, alive while the
///     pin is released. Note this differs from the V8 evaluator, which
///     stores the pin-level value: the two compilers encode feedback
///     literals differently (pin sense on the V8, register sense here), and
///     each simulator matches its compiler's validated convention.
///
/// Registered semantics:
///   - Pin 1 is the clock AND array input line 0, so it may appear in
///     equations; a change on it is both a potential edge and an array
///     re-evaluation. There is no global /OE (pin 13 is a plain input).
///   - Rising edge: AR true holds every register at 0 (dominates); else SP
///     true presets every register to 1; else each cell latches its D (its
///     logic-row sum), all evaluated from the PRE-edge state and committed
///     together so counters and shifters stay consistent.
///   - AR is LEVEL-SENSITIVE: it is re-evaluated on every input change, not
///     just at edges, and resets the registers asynchronously while true.
///     An AR term that depends on an unresolved input is treated as not
///     asserted (a poisoned reset would otherwise wipe known-good state).
///   - Power-up reset is a datasheet guarantee: Q = 0, so active-high
///     registered outputs start LOW and active-low ones HIGH (the opposite
///     of the V8's all-high power-up). The previous-clock sample is taken at
///     Initialize; only a clean Low -> High transition clocks the registers.
///
/// Timing: combinational evaluation uses tPD; register-driven output changes
/// (clock edge or asynchronous reset, the latter approximating tAR) use tCO,
/// with combinational consumers of register feedback one tPD later.
/// </summary>
public sealed class Gal22V10 : IChip
{
    public const long PropagationDelayPs = 10_000;    // nominal tPD ~10 ns
    public const long ClockToOutputDelayPs = 10_000;  // nominal tCO ~10 ns

    private readonly bool[] fuses;
    private readonly Net[] nets;             // index == position in PinNumbers
    private readonly int[] pinNumbers;
    private readonly Dictionary<int, int> pinIndex = new();       // pin number -> nets index
    private readonly Dictionary<int, Driver> olmcDrivers = new(); // pin number -> driver
    private readonly long propagationDelayPs;
    private readonly long clockToOutputDelayPs;

    private readonly bool[] olmcIsRegistered;   // by OLMC index (23 - pin)
    private readonly bool[] olmcIsActiveLow;
    private readonly Signal[] registerQ;        // LOGICAL register value (not pin level)
    private readonly Dictionary<int, int> registeredOlmcByPin = new();   // feedback override
    private Signal previousClock = Signal.Unknown;

    /// <param name="fuses">Parsed fuse array; only fuses 0..5827 are read, so
    /// both QF5828 and QF5892 (UES-bearing) images work.</param>
    /// <param name="netByPin">Net attached to each signal pin (power pins excluded).</param>
    /// <param name="propagationDelayPs">Combinational propagation delay (tPD).</param>
    /// <param name="clockToOutputDelayPs">Registered clock-to-output delay (tCO).</param>
    public Gal22V10(bool[] fuses, IReadOnlyDictionary<int, Net> netByPin,
                    long propagationDelayPs = PropagationDelayPs,
                    long clockToOutputDelayPs = ClockToOutputDelayPs)
    {
        this.fuses = fuses;
        this.propagationDelayPs = propagationDelayPs;
        this.clockToOutputDelayPs = clockToOutputDelayPs;

        pinNumbers = netByPin.Keys.OrderBy(p => p).ToArray();
        nets = new Net[pinNumbers.Length];
        for (int i = 0; i < pinNumbers.Length; i++)
        {
            nets[i] = netByPin[pinNumbers[i]];
            pinIndex[pinNumbers[i]] = i;
        }

        // One driver per OLMC output pin that is actually present on the net map.
        foreach (int pin in Gal22V10Device.OlmcOutputPins)
        {
            if (pinIndex.TryGetValue(pin, out int idx))
                olmcDrivers[pin] = new Driver(nets[idx], DriveStrength.Strong);
        }

        // Classify each cell straight from its S bits. An erased cell reads
        // S1=0 (registered); harmless -- its all-intact OE row never drives,
        // and its feedback is the /Q of a permanently-reset register, exactly
        // as on silicon. Registers power up RESET (Q low), per the datasheet.
        olmcIsRegistered = new bool[Gal22V10Device.OlmcCount];
        olmcIsActiveLow = new bool[Gal22V10Device.OlmcCount];
        registerQ = new Signal[Gal22V10Device.OlmcCount];
        for (int o = 0; o < Gal22V10Device.OlmcCount; o++)
        {
            olmcIsActiveLow[o] = !SafeFuse(Gal22V10Device.S0Fuse(o));
            olmcIsRegistered[o] = !SafeFuse(Gal22V10Device.S1Fuse(o));
            registerQ[o] = Signal.Low;
            if (olmcIsRegistered[o])
                registeredOlmcByPin[Gal22V10Device.OlmcOutputPins[o]] = o;
        }
    }

    public IReadOnlyList<int> PinNumbers => pinNumbers;
    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        previousClock = SampleNet(Gal22V10Device.ClockPin);
        CheckAsyncReset(scheduler);
        EvaluateAll(scheduler, propagationDelayPs);
    }

    public void OnInputChanged(int pinIndexChanged, IScheduler scheduler)
    {
        // Every signal pin feeds the array on this family (all 22 lines are
        // routed, including the clock and the macrocell pins -- whose changes
        // must re-evaluate combinational feedback even when we drive them).
        int pin = pinNumbers[pinIndexChanged];

        if (pin == Gal22V10Device.ClockPin) { OnClockChanged(scheduler); return; }

        CheckAsyncReset(scheduler);
        EvaluateAll(scheduler, propagationDelayPs);
    }

    // ---- Registered machinery ------------------------------------------------

    private void OnClockChanged(IScheduler scheduler)
    {
        Signal now = SampleNet(Gal22V10Device.ClockPin);
        Signal prev = previousClock;
        previousClock = now;

        // Only a clean Low -> High transition is a rising edge. A clock coming
        // out of an unresolved state does not clock the registers.
        bool edge = prev == Signal.Low && now == Signal.High;

        if (edge)
        {
            // AR (level, dominates) and SP (at the edge) are global; each D is
            // evaluated from the PRE-edge register state, then all cells
            // commit together -- counters and shifters depend on this.
            (bool arUsed, bool arValue, bool arUnknown) = EvaluateProductTerm(Gal22V10Device.ArRow);
            (bool spUsed, bool spValue, bool spUnknown) = EvaluateProductTerm(Gal22V10Device.SpRow);
            bool arTrue = arUsed && arValue && !arUnknown;
            bool spTrue = spUsed && spValue && !spUnknown;
            bool spPoisons = spUsed && spUnknown;   // unresolved preset -> state unknown

            var next = new Signal[Gal22V10Device.OlmcCount];
            for (int o = 0; o < Gal22V10Device.OlmcCount; o++)
            {
                if (!olmcIsRegistered[o]) continue;
                if (arTrue) { next[o] = Signal.Low; continue; }
                if (spPoisons) { next[o] = Signal.Unknown; continue; }
                if (spTrue) { next[o] = Signal.High; continue; }
                (bool sum, bool unknown) = EvaluateSum(
                    Gal22V10Device.BlockStartRow[o] + 1, Gal22V10Device.BlockTermCount[o]);
                next[o] = !sum && unknown ? Signal.Unknown
                        : sum ? Signal.High : Signal.Low;
            }
            for (int o = 0; o < Gal22V10Device.OlmcCount; o++)
                if (olmcIsRegistered[o]) registerQ[o] = next[o];
        }

        // AR is level-sensitive and the clock is also array line 0, so a clock
        // change without an edge still re-evaluates everything.
        CheckAsyncReset(scheduler);
        long delay = edge ? clockToOutputDelayPs : propagationDelayPs;
        EvaluateAll(scheduler, delay, combinationalExtraPs: edge ? propagationDelayPs : 0);
    }

    // Level-sensitive asynchronous reset: while the AR term is true, every
    // register is held at 0. An AR that depends on an unresolved input is
    // treated as not asserted. Register-output updates use tCO (approximating
    // tAR); combinational consumers follow one tPD later.
    private void CheckAsyncReset(IScheduler scheduler)
    {
        (bool used, bool value, bool unknown) = EvaluateProductTerm(Gal22V10Device.ArRow);
        if (!used || !value || unknown) return;

        bool changed = false;
        for (int o = 0; o < Gal22V10Device.OlmcCount; o++)
        {
            if (!olmcIsRegistered[o] || registerQ[o] == Signal.Low) continue;
            registerQ[o] = Signal.Low;
            changed = true;
        }
        if (changed)
            EvaluateAll(scheduler, clockToOutputDelayPs, combinationalExtraPs: propagationDelayPs);
    }

    // Drive every OLMC from the current state: registered cells from their
    // OE-gated, polarity-adjusted register; combinational cells evaluated.
    private void EvaluateAll(IScheduler scheduler, long delayPs, long combinationalExtraPs = 0)
    {
        for (int o = 0; o < Gal22V10Device.OlmcCount; o++)
        {
            if (!olmcDrivers.TryGetValue(Gal22V10Device.OlmcOutputPins[o], out Driver? driver))
                continue;
            long delay = delayPs + (olmcIsRegistered[o] ? 0 : combinationalExtraPs);
            scheduler.Schedule(delay, driver, EvaluateOlmc(o));
        }
    }

    private Signal EvaluateOlmc(int olmc)
    {
        // The OE row gates registered and combinational cells alike. An
        // all-blown row always enables; an all-intact (erased) row can never
        // be true, so an unprogrammed cell is released with no special case.
        (bool oeUsed, bool oeValue, bool oeUnknown) =
            EvaluateProductTerm(Gal22V10Device.BlockStartRow[olmc]);
        if (oeUsed)
        {
            if (oeUnknown) return Signal.Unknown;
            if (!oeValue) return Signal.HighZ;
        }

        if (olmcIsRegistered[olmc])
            return PolarityAdjusted(olmc, registerQ[olmc]);

        (bool sum, bool unknown) = EvaluateSum(
            Gal22V10Device.BlockStartRow[olmc] + 1, Gal22V10Device.BlockTermCount[olmc]);
        if (!sum && unknown) return Signal.Unknown;
        return PolarityAdjusted(olmc, sum ? Signal.High : Signal.Low);
    }

    // ---- Array evaluation ------------------------------------------------------

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

    // Returns (term used at all, term value, term depends on an unresolved input).
    private (bool Used, bool Value, bool Unknown) EvaluateProductTerm(int row)
    {
        int baseIdx = row * Gal22V10Device.Cols;
        bool used = false;
        bool unknown = false;

        for (int line = 0; line < Gal22V10Device.LineToPin.Length; line++)
        {
            bool trueIntact = !SafeFuse(baseIdx + Gal22V10Device.TrueColumn(line));
            bool compIntact = !SafeFuse(baseIdx + Gal22V10Device.ComplementColumn(line));
            if (!trueIntact && !compIntact) continue;   // input not used in this term
            used = true;

            // Both polarities connected -> literal AND its complement -> 0.
            // (This is how an erased row -- including an erased OE row --
            // reads as constant 0.)
            if (trueIntact && compIntact) return (true, false, false);

            Signal s = SamplePin(Gal22V10Device.LineToPin[line]);

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

    private Signal PolarityAdjusted(int olmc, Signal logical)
    {
        if (logical != Signal.High && logical != Signal.Low) return Signal.Unknown;
        bool high = logical == Signal.High;
        return (high ^ olmcIsActiveLow[olmc]) ? Signal.High : Signal.Low;
    }

    // Sample an array-input line. A registered OLMC's feedback taps the
    // register's /Q -- polarity-independent (S0 sits after the tap) and alive
    // while the pin is released or externally driven. Everything else,
    // including a combinational OLMC's feedback, is the pin itself.
    private Signal SamplePin(int pin)
    {
        if (registeredOlmcByPin.TryGetValue(pin, out int olmc))
        {
            Signal q = registerQ[olmc];
            return q == Signal.High ? Signal.Low
                 : q == Signal.Low ? Signal.High
                 : Signal.Unknown;
        }
        return SampleNet(pin);
    }

    private Signal SampleNet(int pin)
    {
        if (pinIndex.TryGetValue(pin, out int idx))
        {
            Signal s = nets[idx].Value;
            return s == Signal.HighZ ? Signal.Unknown : s;
        }
        return Signal.Unknown;
    }

    private bool SafeFuse(int address) => address < fuses.Length && fuses[address];
}