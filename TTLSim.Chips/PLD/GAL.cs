using TTLSim.Core;

namespace TTLSim.Chips.Pld;

/// <summary>
/// Combinational GAL/PLD evaluator. Given a device geometry (<see cref="GalDevice"/>)
/// and a fuse array (from a parsed JEDEC map), it computes each OLMC output as
/// a sum of products with XOR output polarity, and drives the corresponding
/// output pins. Inputs are sampled from the array-input pins; an OLMC pin that
/// the fuse map leaves as a pure input (or that has no active product terms)
/// is driven high-Z.
///
/// Scope: combinational only. Registered OLMC modes (clock, feedback state)
/// are not modelled yet -- the same staged approach the register chips took.
/// That is sufficient for decode/glue, which is all the mini machine asks of a
/// GAL today.
///
/// Validation: this evaluates the geometry in <see cref="GalDevice"/>. To check
/// a real device, load a WinCUPL-produced .jed, drive the input pins across the
/// relevant input space, and confirm the outputs match the .pld's intent. For
/// pcload_decode that is: sweep OP0..OP3 (pins 2..5) over 0..15 and confirm the
/// output (pin 19) is high only for 0111.
/// </summary>
public sealed class Gal : IChip
{
    public const long PropagationDelayPs = 10_000;   // nominal GAL tPD ~10 ns

    private readonly GalDevice device;
    private readonly bool[] fuses;
    private readonly int[] arrayInputPins;   // line -> pin, resolved for the device mode
    private readonly Net[] nets;            // index == position in PinNumbers
    private readonly int[] pinNumbers;
    private readonly Dictionary<int, int> pinIndex = new();   // pin number -> nets index
    private readonly Dictionary<int, Driver> olmcDrivers = new(); // pin number -> driver
    private readonly bool[] isOutputPin;
    private readonly long propagationDelayPs;

    /// <param name="device">Device geometry.</param>
    /// <param name="fuses">Parsed fuse array (length device.FuseCount).</param>
    /// <param name="netByPin">Net attached to each signal pin (power pins excluded).</param>
    /// <param name="propagationDelayPs">Output propagation delay in picoseconds.
    /// Defaults to <see cref="PropagationDelayPs"/> (the nominal ~10 ns grade);
    /// the factory passes the part's explicit Propagation Delay when one is set.</param>
    public Gal(GalDevice device, bool[] fuses, IReadOnlyDictionary<int, Net> netByPin,
               long propagationDelayPs = PropagationDelayPs)
    {
        this.device = device;
        this.fuses = fuses;
        this.propagationDelayPs = propagationDelayPs;

        // The column->pin routing is mode-dependent; the mode is encoded by the
        // SYN and AC0 fuses (galasm MODE1/2/3).
        bool syn = device.SynFuse < fuses.Length && fuses[device.SynFuse];
        bool ac0 = device.Ac0Fuse < fuses.Length && fuses[device.Ac0Fuse];
        arrayInputPins = device.ColumnMapForMode(syn, ac0);

        pinNumbers = netByPin.Keys.OrderBy(p => p).ToArray();
        nets = new Net[pinNumbers.Length];
        isOutputPin = new bool[pinNumbers.Length];
        for (int i = 0; i < pinNumbers.Length; i++)
        {
            nets[i] = netByPin[pinNumbers[i]];
            pinIndex[pinNumbers[i]] = i;
        }

        // One driver per OLMC output pin that is actually present on the net map.
        foreach (int pin in device.OlmcOutputPins)
        {
            if (pinIndex.TryGetValue(pin, out int idx))
            {
                olmcDrivers[pin] = new Driver(nets[idx], DriveStrength.Strong);
                isOutputPin[idx] = true;
            }
        }
    }

    public IReadOnlyList<int> PinNumbers => pinNumbers;
    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler) => Evaluate(scheduler);

    public void OnInputChanged(int pinIndexChanged, IScheduler scheduler)
    {
        // Output-pin changes are our own drive; only inputs matter.
        if (isOutputPin[pinIndexChanged]) return;
        Evaluate(scheduler);
    }

    private void Evaluate(IScheduler scheduler)
    {
        for (int o = 0; o < device.OlmcCount; o++)
        {
            int pin = device.OlmcOutputPins[o];
            if (!olmcDrivers.TryGetValue(pin, out Driver? driver)) continue;

            Signal value = EvaluateOlmc(o);
            scheduler.Schedule(propagationDelayPs, driver, value);
        }
    }

    private Signal EvaluateOlmc(int olmc)
    {
        int firstRow = olmc * device.ProductTermsPerOlmc;

        // Output vs input: an erased / input-configured OLMC has an all-intact
        // block (every fuse 0, the erased state). Any OLMC configured to drive
        // has at least one blown fuse. (Conditional tristate / OE product terms
        // are not modelled; a configured combinational output is treated as
        // always enabled, which is what decode/glue logic uses.)
        if (!BlockHasBlownFuse(firstRow)) return Signal.HighZ;

        bool sum = false;
        bool unknown = false;

        for (int t = 0; t < device.ProductTermsPerOlmc; t++)
        {
            (bool used, bool value, bool termUnknown) = EvaluateProductTerm(firstRow + t);
            if (!used) continue;                 // all-blown row (e.g. always-on OE term)
            if (termUnknown) { unknown = true; continue; }
            if (value) { sum = true; break; }    // OR: one true term is enough
        }

        // Nothing pulled the OR true, but a deciding input was unresolved.
        if (!sum && unknown) return Signal.Unknown;

        bool polarity = fuses[device.XorFuseBase + olmc];   // 1 = active high, 0 = active low
        return (sum ^ !polarity) ? Signal.High : Signal.Low;
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

    private Signal SamplePin(int pin)
    {
        if (pin != 0 && pinIndex.TryGetValue(pin, out int idx))
        {
            Signal s = nets[idx].Value;
            return s == Signal.HighZ ? Signal.Unknown : s;
        }
        return Signal.Unknown;
    }
}