using TTLSim.Chips.Alu;
using TTLSim.Chips.Buffers;
using TTLSim.Chips.Counters;
using TTLSim.Chips.Decoders;
using TTLSim.Chips.Displays;
using TTLSim.Chips.Gates;
using TTLSim.Chips.Memory;
using TTLSim.Chips.Multiplexers;
using TTLSim.Chips.Passives;
using TTLSim.Chips.Pld;
using TTLSim.Chips.Registers;
using TTLSim.Chips.Sources;
using TTLSim.Chips.Comparators;
using TTLSim.Core;

using Microsoft.Extensions.Logging;

namespace TTLSim.Chips;

public sealed class ChipFactory : IChipFactory
{
    private readonly Microsoft.Extensions.Logging.ILogger? logger;

    public ChipFactory(Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        this.logger = logger;
    }
    public IChip? CreateForDevice(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Stub: device-level dispatch lives here. Per-unit dispatch happens in
        // CreateForUnits below; the builder calls that instead for now.
        return null;
    }

    public IChip? CreateForItem(BuildItem item, IReadOnlyDictionary<int, Net> pinToNet)
    {
        if (item.PinNumbers.Count == 0) return null;
        int pin = item.PinNumbers[0];
        if (!pinToNet.TryGetValue(pin, out Net? net) || net is null) return null;

        return item.Kind switch
        {
            BuildItemKind.Vcc => new VccDriver(net),
            BuildItemKind.Gnd => new GndDriver(net),
            BuildItemKind.ClockSource when item.ClockPeriodPicoseconds is long period
                => new ClockSource(net, period),
            _ => null
        };
    }

    public IEnumerable<IChip> CreateForUnits(
    BuildDevice device,
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, Net>> unitPinMaps,
    IReadOnlyDictionary<int, Signal> powerNets)
    {
        if (device.PartIdentifier == "393")
        {
            foreach (var (_, pinMap) in unitPinMaps)
                foreach (IChip chip in CreateHc393Halves(device, pinMap))
                    yield return chip;
            yield break;
        }
        if (device.PartIdentifier == "390")
        {
            foreach (var (_, pinMap) in unitPinMaps)
                foreach (IChip chip in CreateHc390Halves(device, pinMap))
                    yield return chip;
            yield break;
        }

        if (device.PartIdentifier is "NE555" or "NE556")
        {
            // One core for the 555, two for the 556 -- like the dual counters,
            // a single box that yields more than one IChip.
            foreach (var (_, pinMap) in unitPinMaps)
                foreach (IChip chip in CreateTimerCores(device, pinMap))
                    yield return chip;
            yield break;
        }

        if (device.PartIdentifier is "00" or "02" or "04" or "08" or "10"
            or "14" or "20" or "30" or "32" or "86" or "125" or "126")
        {
            foreach (var (_, pinMap) in unitPinMaps)
                foreach (IChip chip in CreateGateChip(device, pinMap))
                    yield return chip;
            yield break;
        }

        if (IsMemoryPart(device.PartIdentifier))
        {
            foreach (var (_, pinMap) in unitPinMaps)
            {
                IChip? mem = TryCreateMemory(device, pinMap);
                if (mem is not null) yield return mem;
            }
            yield break;
        }

        foreach (BuildUnit unit in device.Units)
        {
            if (!unitPinMaps.TryGetValue(unit.UnitId, out var pinMap)) continue;

            if (device.PartIdentifier == "resistor")
            {
                IChip? pull = TryCreatePullResistor(pinMap, powerNets);
                if (pull is not null) yield return pull;
                continue;
            }

            if (device.PartIdentifier == "resnet-sip9")
            {
                foreach (IChip resnet in CreateResistorNetwork(pinMap, powerNets))
                    yield return resnet;
                continue;
            }

            if (device.PartIdentifier == "button")
            {
                IChip? btn = TryCreateButton(pinMap);
                if (btn is not null) yield return btn;
                continue;
            }
            if (device.PartIdentifier == "button-4")
            {
                // Pins 1=2 and 3=4 are unioned as device-internal nets by
                // SchematicBuildInput, so each terminal is reachable via either leg.
                pinMap.TryGetValue(1, out Net? a1);
                pinMap.TryGetValue(2, out Net? a2);
                pinMap.TryGetValue(3, out Net? b1);
                pinMap.TryGetValue(4, out Net? b2);
                Net? termA = a1 ?? a2;
                Net? termB = b1 ?? b2;
                // Distinct-net check as in TryCreateButton: terminals bridged
                // onto one net make the contact a no-op and the mirror-driver
                // model a zero-delay oscillator when pressed.
                if (termA is not null && termB is not null
                    && !ReferenceEquals(termA, termB))
                    yield return new ButtonInput(termA, termB);
                continue;
            }
            if (device.PartIdentifier is "switch" or "jumper-2pin")
            {
                IChip? sw = TryCreateSwitch(device, unit, pinMap);
                if (sw is not null) yield return sw;
                continue;
            }
            if (device.PartIdentifier is "spdt-switch" or "jumper-3pin")
            {
                IChip? sp = TryCreateSpdtSwitch(device, unit, pinMap);
                if (sp is not null) yield return sp;
                continue;
            }
            if (device.PartIdentifier == "diode")
            {
                IChip? d = TryCreateDiode(pinMap);
                if (d is not null) yield return d;
                continue;
            }


            IChip? chip = CreateForUnit(device, unit, pinMap);
            if (chip is not null) yield return chip;
        }
    }

    private static IChip? TryCreateSwitch(
        BuildDevice device, BuildUnit unit, IReadOnlyDictionary<int, Net> pinToNet)
    {
        pinToNet.TryGetValue(1, out Net? p1);
        pinToNet.TryGetValue(2, out Net? p2);
        if (p1 is null || p2 is null) return null;
        // Both terminals on one net: a no-op contact, and the mirror-driver
        // model would zero-delay oscillate (see TryCreatePullResistor).
        // TTL014 reports the part as absent.
        if (ReferenceEquals(p1, p2)) return null;
        return new SwitchInput(p1, p2, unit.SwitchClosed);
    }

    private static IChip? TryCreateSpdtSwitch(
    BuildDevice device, BuildUnit unit, IReadOnlyDictionary<int, Net> pinToNet)
    {
        pinToNet.TryGetValue(1, out Net? a);
        pinToNet.TryGetValue(2, out Net? com);
        pinToNet.TryGetValue(3, out Net? b);
        if (a is null || com is null || b is null) return null;
        return new SpdtSwitchInput(a, com, b, unit.SwitchClosed);
    }

    private static IChip? TryCreateButton(IReadOnlyDictionary<int, Net> pinToNet)
    {
        pinToNet.TryGetValue(1, out Net? p1);
        pinToNet.TryGetValue(2, out Net? p2);
        if (p1 is null || p2 is null) return null;
        // Both terminals on one net: no-op, and the mirror-driver model
        // would zero-delay oscillate when pressed (see TryCreatePullResistor).
        if (ReferenceEquals(p1, p2)) return null;
        return new ButtonInput(p1, p2);
    }

    private static IChip? TryCreateDiode(IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Diode pins: 1 = anode, 2 = cathode (per DiodeUnit.BuildPins).
        pinToNet.TryGetValue(1, out Net? anode);
        pinToNet.TryGetValue(2, out Net? cathode);
        if (anode is null || cathode is null) return null;
        // Anode and cathode on one net: the diode does nothing, and the
        // resolve-and-redrive model can feed itself (see TryCreatePullResistor).
        if (ReferenceEquals(anode, cathode)) return null;
        return new DiodeContact(anode, cathode);
    }

    /// <summary>
    /// A resistor with one end on a power rail becomes a weak pull on the other
    /// end's net. A resistor with neither end on a rail is, for now, left
    /// unmodelled (no series-resistor circuits in scope yet).
    /// </summary>
    private static IChip? TryCreatePullResistor(
        IReadOnlyDictionary<int, Net> pinToNet,
        IReadOnlyDictionary<int, Signal> powerNets)
    {
        // Resistor pins are numbered 1 and 2.
        pinToNet.TryGetValue(1, out Net? net1);
        pinToNet.TryGetValue(2, out Net? net2);
        if (net1 is null || net2 is null) return null;

        // If pin 1 is on a rail, pull pin 2's net to that rail's value, and vice versa.
        if (powerNets.TryGetValue(net1.Id, out Signal v1))
            return new PullDriver(net2, v1);

        if (powerNets.TryGetValue(net2.Id, out Signal v2))
            return new PullDriver(net1, v2);

        // Both ends on the SAME net (e.g. a series resistor bridged by a
        // header link that loops the signal back): the resistor is
        // electrically a no-op, and a ResistorContact would be a zero-delay
        // oscillator -- its exclude-own-driver resolve assumes two distinct
        // nets, so with both mirror drivers on one net the fixed point never
        // forms and Apply re-triggers itself forever at the same tick.
        if (ReferenceEquals(net1, net2))
            return null;

        // Neither end on a rail -> series resistor, unmodelled for now.
        return new ResistorContact(net1, net2);
    }

    /// <summary>
    /// Bussed SIP-9 resistor network: pin 1 is the common bus, pins 2..9 are
    /// the eight resistor ends. Each element behaves exactly like a single
    /// resistor between the common net and that element's net -- so it reuses
    /// TryCreatePullResistor, which decides pull-up/pull-down (when one end is
    /// on a rail) versus a transparent series contact (when neither is).
    /// </summary>
    private static IEnumerable<IChip> CreateResistorNetwork(
        IReadOnlyDictionary<int, Net> pinToNet,
        IReadOnlyDictionary<int, Signal> powerNets)
    {
        if (!pinToNet.TryGetValue(1, out Net? common) || common is null)
            yield break;

        for (int elementPin = 2; elementPin <= 9; elementPin++)
        {
            if (!pinToNet.TryGetValue(elementPin, out Net? element) || element is null)
                continue;

            // Present each element to the single-resistor model as pins {1, 2}
            // = {common, element}, so behaviour is identical by construction.
            var pair = new Dictionary<int, Net> { [1] = common, [2] = element };
            IChip? chip = TryCreatePullResistor(pair, powerNets);
            if (chip is not null) yield return chip;
        }
    }

    // Each gate is (inputPins[], outputPin) in
    // physical pin numbers; `make` turns the resolved input nets + output
    // net into the right gate class. Covers every fan-in:
    //   1-input  inverters  (04 hex, 14 hex Schmitt)
    //   2-input  quad gates  (00 NAND, 02 NOR, 08 AND, 32 OR, 86 XOR)
    //   3-input  triple NAND (10)
    //   4-input  dual NAND   (20)
    //   8-input  single NAND (30)
    // The 7402 has the output-first pinout (a:2,3->1 etc.); the rest follow
    // the conventional 1,2->3 layout. Propagation delay is resolved per
    // (part, family) and injected into every gate so an LS part and an HC
    // part of the same gate simulate at their real, different speeds.
    private static IEnumerable<IChip> CreateGateChip(
        BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        long delayPs = TtlTiming.ResolvePs(device);

        (int[] inputs, int output)[] gates;
        Func<IReadOnlyList<Net>, Net, IChip> make;
        switch (device.PartIdentifier)
        {
            case "00":
                gates = new[] { (new[] {1,2}, 3), (new[] {4,5}, 6),
                                (new[] {9,10}, 8), (new[] {12,13}, 11) };
                make = (i, y) => new Hc00(i[0], i[1], y, delayPs);
                break;
            case "02":
                gates = new[] { (new[] {2,3}, 1), (new[] {5,6}, 4),
                                (new[] {8,9}, 10), (new[] {11,12}, 13) };
                make = (i, y) => new Hc02(i[0], i[1], y, delayPs);
                break;
            case "08":
                gates = new[] { (new[] {1,2}, 3), (new[] {4,5}, 6),
                                (new[] {9,10}, 8), (new[] {12,13}, 11) };
                make = (i, y) => new Hc08(i[0], i[1], y, delayPs);
                break;
            case "32":
                gates = new[] { (new[] {1,2}, 3), (new[] {4,5}, 6),
                                (new[] {9,10}, 8), (new[] {12,13}, 11) };
                make = (i, y) => new Hc32(i[0], i[1], y, delayPs);
                break;
            case "86":
                gates = new[] { (new[] {1,2}, 3), (new[] {4,5}, 6),
                                (new[] {9,10}, 8), (new[] {12,13}, 11) };
                make = (i, y) => new Hc86(i[0], i[1], y, delayPs);
                break;
            case "125":
                // Quad 3-state buffer, independent active-LOW enables.
                // i[0] = /OE, i[1] = A. Sections whose Y is unwired are
                // skipped by the loop below, like any unused gate.
                gates = new[] { (new[] {1,2}, 3), (new[] {4,5}, 6),
                                (new[] {10,9}, 8), (new[] {13,12}, 11) };
                make = (i, y) => new Hc125(i[0], i[1], y, enableActiveLow: true, delayPs: delayPs);
                break;
            case "126":
                // The '125 with active-HIGH enables; same section layout.
                gates = new[] { (new[] {1,2}, 3), (new[] {4,5}, 6),
                                (new[] {10,9}, 8), (new[] {13,12}, 11) };
                make = (i, y) => new Hc125(i[0], i[1], y, enableActiveLow: false, delayPs: delayPs);
                break;
            case "04":
                gates = new[] { (new[] {1}, 2), (new[] {3}, 4), (new[] {5}, 6),
                                (new[] {9}, 8), (new[] {11}, 10), (new[] {13}, 12) };
                make = (i, y) => new Hc04(i[0], y, delayPs);
                break;
            case "14":
                gates = new[] { (new[] {1}, 2), (new[] {3}, 4), (new[] {5}, 6),
                                (new[] {9}, 8), (new[] {11}, 10), (new[] {13}, 12) };
                make = (i, y) => new Hc14(i[0], y, delayPs);
                break;
            case "10":
                gates = new[] { (new[] {1,2,13}, 12), (new[] {3,4,5}, 6),
                                (new[] {9,10,11}, 8) };
                make = (i, y) => new Hc10(i[0], i[1], i[2], y, delayPs);
                break;
            case "20":
                gates = new[] { (new[] { 1, 2, 4, 5 }, 6), (new[] { 9, 10, 12, 13 }, 8) };
                make = (i, y) => new Hc20(i[0], i[1], i[2], i[3], y, delayPs);
                break;
            case "30":
                gates = new[] { (new[] { 1, 2, 3, 4, 5, 6, 11, 12 }, 8) };
                make = (i, y) => new Hc30(i[0], i[1], i[2], i[3],
                                          i[4], i[5], i[6], i[7], y, delayPs);
                break;
            default:
                yield break;
        }

        foreach (var (inputs, output) in gates)
        {
            if (!pinToNet.TryGetValue(output, out Net? ny) || ny is null) continue;

            var nets = new Net[inputs.Length];
            bool ok = true;
            for (int k = 0; k < inputs.Length; k++)
            {
                if (!pinToNet.TryGetValue(inputs[k], out Net? n) || n is null)
                { ok = false; break; }
                nets[k] = n;
            }
            if (ok) yield return make(nets, ny);
        }
    }

    // ----------------------------------------------------------------- timers

    // Representative output propagation delay for a Schmitt-mode 555/556 core.
    // The part isn't a 74-series family member, so there's no family grade to
    // resolve; ~100 ns is a typical bipolar NE555 output delay and only sets
    // the sim edge timing.
    private const long TimerSchmittDelayPs = 100_000;

    /// <summary>
    /// Builds the timer core(s) for a 555 (one) or 556 (two). The 555 ties
    /// THR(6)/TRG(2) and outputs on pin 3; the 556's two cores are
    /// THR(2)/TRG(6)->OUT(5) and THR(12)/TRG(8)->OUT(9). Each core's role
    /// (Schmitt vs Astable) and astable frequency come from the device.
    /// </summary>
    private static IEnumerable<IChip> CreateTimerCores(
        BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        if (device.PartIdentifier == "NE555")
        {
            IChip? core = TryCreateTimerCore(
                pinToNet, device.Function1, device.FrequencyHz1,
                thr: 6, trg: 2, outPin: 3);
            if (core is not null) yield return core;
        }
        else // NE556
        {
            IChip? t1 = TryCreateTimerCore(
                pinToNet, device.Function1, device.FrequencyHz1,
                thr: 2, trg: 6, outPin: 5);
            if (t1 is not null) yield return t1;

            IChip? t2 = TryCreateTimerCore(
                pinToNet, device.Function2, device.FrequencyHz2,
                thr: 12, trg: 8, outPin: 9);
            if (t2 is not null) yield return t2;
        }
    }

    private static IChip? TryCreateTimerCore(
        IReadOnlyDictionary<int, Net> pinToNet,
        TimerFunction? function, double? frequencyHz,
        int thr, int trg, int outPin)
    {
        // OUT must be wired -- a core whose output drives nothing has no effect.
        if (!pinToNet.TryGetValue(outPin, out Net? outNet) || outNet is null)
            return null;

        if (function == TimerFunction.Astable)
        {
            double hz = frequencyHz is double f && f > 0 ? f : 1000.0;
            long periodPs = (long)(1e12 / hz);
            return new Ne555(outNet, periodPs);
        }

        // Schmitt (default): the THR/TRG node is the input. They're tied
        // externally, so prefer THR and fall back to TRG.
        Net? inNet = null;
        if (pinToNet.TryGetValue(thr, out Net? t) && t is not null) inNet = t;
        else if (pinToNet.TryGetValue(trg, out Net? g) && g is not null) inNet = g;
        if (inNet is null) return null;

        return new Ne555(inNet, outNet, TimerSchmittDelayPs);
    }



    private IChip? CreateForUnit(BuildDevice device, BuildUnit unit, IReadOnlyDictionary<int, Net> pinToNet)
    {
        return device.PartIdentifier switch
        {
            "47" => TryCreateLs47(pinToNet),
            "74" => TryCreateHc74(device, pinToNet),
            "138" => TryCreateHc138(device, pinToNet),
            "139" => TryCreateHc139(device, pinToNet),
            "151" => TryCreateHc151(device, pinToNet),
            "153" => TryCreateHc153(device, pinToNet),
            "154" => TryCreateHc154(device, pinToNet),
            "157" => TryCreateHc157(device, pinToNet),
            "257" => TryCreateHc257(device, pinToNet),
            "161" => TryCreateHc161(device, pinToNet),
            "163" => TryCreateHc163(device, pinToNet),
            "173" => TryCreateHc173(device, pinToNet),
            "181" => TryCreateHc181(device, pinToNet),
            "182" => TryCreateHc182(device, pinToNet),
            "191" => TryCreateHc191(device, pinToNet),
            "194" => TryCreateHc194(device, pinToNet),
            "244" => TryCreateHc244(device, pinToNet),
            "245" => TryCreateHc245(device, pinToNet),
            "273" => TryCreateHc273(device, pinToNet),
            "283" => TryCreateHc283(device, pinToNet),
            "299" => TryCreateHc299(device, pinToNet),
            "374" => TryCreateHc374(device, pinToNet),
            "377" => TryCreateHc377(device, pinToNet),
            "541" => TryCreateHc541(device, pinToNet),
            "573" => TryCreateHc573(device, pinToNet),
            "574" => TryCreateHc574(device, pinToNet),
            "670" => TryCreateHc670(device, pinToNet),
            "688" => TryCreateHc688(device, pinToNet),
            "DS1813" => TryCreateDs1813(pinToNet),
            "GAL16V8" or "GAL20V8" or "GAL22V10" => TryCreateGal(device, pinToNet),
            "7seg-ca" => TryCreateSevenSegCa(pinToNet),
            _ => null
        };
    }

    private IChip? TryCreateHc194(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 1 /CLR, 2 DSR, 3..6 D0..D3,
        // 7 DSL, 9 S0, 10 S1, 11 CLK. The four Q outputs (15,14,13,12 =
        // Q0..Q3) are OPTIONAL -- an open output drives nothing and must
        // never block instantiation. TTL011 still flags genuinely unwired
        // INPUTS at design time.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc194(
            clrN: Get(1), dsr: Get(2),
            d0: Get(3), d1: Get(4), d2: Get(5), d3: Get(6),
            dsl: Get(7), s0: Get(9), s1: Get(10), clkN: Get(11),
            q0: Opt(15, "q0-nc"), q1: Opt(14, "q1-nc"),
            q2: Opt(13, "q2-nc"), q3: Opt(12, "q3-nc"),
            label: "194", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc299(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the pure inputs: 1 S0, 2 /OE1, 3 /OE2, 9 /CLR,
        // 11 DSR, 12 CP, 18 DSL, 19 S1. The eight I/O pins are bus pins
        // (outputs in hold/shift, inputs in load) and the Q0/Q7 serial
        // taps are outputs -- ALL optional via Opt() stand-ins, since an
        // open output must never block instantiation and an unwired I/O
        // bit simply loads 0 and drives nothing. TTL011 still flags
        // genuinely unwired control INPUTS at design time.
        int[] needed = { 1, 2, 3, 9, 11, 12, 18, 19 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc299(
            s0: Get(1), oe1N: Get(2), oe2N: Get(3), clrN: Get(9),
            dsr: Get(11), clkN: Get(12), dsl: Get(18), s1: Get(19),
            io0: Opt(7, "io0-nc"), io1: Opt(13, "io1-nc"),
            io2: Opt(6, "io2-nc"), io3: Opt(14, "io3-nc"),
            io4: Opt(5, "io4-nc"), io5: Opt(15, "io5-nc"),
            io6: Opt(4, "io6-nc"), io7: Opt(16, "io7-nc"),
            q0Tap: Opt(8, "q0tap-nc"), q7Tap: Opt(17, "q7tap-nc"),
            label: "299", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc574(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // The '574 IS the '374 behaviourally -- octal rising-edge D
        // register, async /OE tri-state, register clocking underneath a
        // disabled bus -- with a flow-through pinout instead of the
        // interleave: /OE=1, D0..D7 = pins 2..9 straight down the left,
        // CLK=11, and each Q directly OPPOSITE its D: Q(k) = pin 21-k's
        // partner, so Q0=19 down to Q7=12 (verified against TI SCLS148
        // and SG Micro; the part definition's Q labels were bit-reversed
        // until the 2026-07 fix -- the '374-anchored transcription trap).
        // One Hc374 core instance with remapped pins; the label keeps
        // logs honest. Same required-pin policy as the '374: a floating
        // /OE or CLK on a register is a real fault, everything else Opt().
        if (!pinToNet.TryGetValue(1, out Net? oeN) || oeN is null) return null;
        if (!pinToNet.TryGetValue(11, out Net? clkN) || clkN is null) return null;

        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);

        return new Hc374(
            oeN: oeN, clkN: clkN,
            d0: Opt(2, "d0-nc"), d1: Opt(3, "d1-nc"),
            d2: Opt(4, "d2-nc"), d3: Opt(5, "d3-nc"),
            d4: Opt(6, "d4-nc"), d5: Opt(7, "d5-nc"),
            d6: Opt(8, "d6-nc"), d7: Opt(9, "d7-nc"),
            q0: Opt(19, "q0-nc"), q1: Opt(18, "q1-nc"),
            q2: Opt(17, "q2-nc"), q3: Opt(16, "q3-nc"),
            q4: Opt(15, "q4-nc"), q5: Opt(14, "q5-nc"),
            q6: Opt(13, "q6-nc"), q7: Opt(12, "q7-nc"),
            label: "574", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc573(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // The '574's transparent sibling: same flow-through frame (D0..D7
        // = pins 2..9, each Q directly opposite so Q0=19 down to Q7=12)
        // with LE on pin 11 instead of a clock. /OE and LE are required --
        // a floating latch enable is a real fault (an open latch tracks
        // whatever noise reaches D); everything else Opt(), per the
        // '374/'574 policy.
        if (!pinToNet.TryGetValue(1, out Net? oeN) || oeN is null) return null;
        if (!pinToNet.TryGetValue(11, out Net? le) || le is null) return null;

        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);

        return new Hc573(
            oeN: oeN, le: le,
            d0: Opt(2, "d0-nc"), d1: Opt(3, "d1-nc"),
            d2: Opt(4, "d2-nc"), d3: Opt(5, "d3-nc"),
            d4: Opt(6, "d4-nc"), d5: Opt(7, "d5-nc"),
            d6: Opt(8, "d6-nc"), d7: Opt(9, "d7-nc"),
            q0: Opt(19, "q0-nc"), q1: Opt(18, "q1-nc"),
            q2: Opt(17, "q2-nc"), q3: Opt(16, "q3-nc"),
            q4: Opt(15, "q4-nc"), q5: Opt(14, "q5-nc"),
            q6: Opt(13, "q6-nc"), q7: Opt(12, "q7-nc"),
            label: "573", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private static IChip? TryCreateDs1813(IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Pin 1 = /RST (open-drain output + pushbutton sense). Pins 2 VCC and
        // 3 GND are power pins consumed by the build pipeline, not wired through
        // the model. A DS1813 with /RST unconnected has nothing to do.
        if (!pinToNet.TryGetValue(1, out Net? rst) || rst is null) return null;
        return new Ds1813(rst);
    }

    private IChip? TryCreateHc191(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required: 1 D1, 2 Q1, 3 Q0, 4 /CTEN, 5 D/U, 6 Q2, 7 Q3,
        // 9 D3, 10 D2, 11 /LD, 14 CLK, 15 D0. The cascade outputs
        // MAX/MIN (pin 12) and /RCO (pin 13) are OPTIONAL -- a lone
        // counter (e.g. the Mini Blinky RSP) leaves them open.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net maxMinNet = pinToNet.TryGetValue(12, out Net? mm) && mm is not null
            ? mm : new Net(-1, "maxmin-unconnected");
        Net rcoNet = pinToNet.TryGetValue(13, out Net? rco) && rco is not null
            ? rco : new Net(-1, "rco-unconnected");

        return new Hc191(
            d0: Get(15), d1: Get(1), d2: Get(10), d3: Get(9),
            ctenN: Get(4), du: Get(5), ldN: Get(11), clkN: Get(14),
            q0: Get(3), q1: Get(2), q2: Get(6), q3: Get(7),
            maxMinN: maxMinNet, rcoN: rcoNet,
            label: "191", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }


    // Replaces the existing TryCreateHc181 in TTLSim.Chips/ChipFactory.cs.
    private static IChip? TryCreateHc181(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 1,2 B0/A0, 3..6 S3..S0, 7 Cn,
        // 8 M, 18..23 B3/A3/B2/A2/B1/A1. The eight outputs -- F0..F3
        // (9,10,11,13), A=B (14), /G (15), /P (16) and Cn+4 (17) -- are
        // OPTIONAL: an open output drives nothing and must never block
        // instantiation (cf. the '138/'154 decoders, the '161/'163 counters
        // and the '283 carry-out). A '181 used as a plain adder slice
        // routinely leaves A=B and the /P //G lookahead pair open, and the
        // top slice of a chain leaves Cn+4 open. TTL011 still flags genuinely
        // unwired INPUTS at design time. Pin 12 GND and pin 24 VCC are
        // consumed by the build pipeline, not wired through the chip model.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 8, 18, 19, 20, 21, 22, 23 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc181(
            b0: Get(1), a0: Get(2),
            s3: Get(3), s2: Get(4), s1: Get(5), s0: Get(6),
            cn: Get(7), m: Get(8),
            f0: Opt(9, "f0-nc"), f1: Opt(10, "f1-nc"),
            f2: Opt(11, "f2-nc"), f3: Opt(13, "f3-nc"),
            aeqb: Opt(14, "aeqb-nc"),
            y: Opt(15, "y-nc"), x: Opt(16, "x-nc"),
            cnP4: Opt(17, "cnp4-nc"),
            b3: Get(18), a3: Get(19), b2: Get(20), a2: Get(21),
            b1: Get(22), a1: Get(23),
            delayPs: TtlTiming.ResolvePs(device));
    }

    private static IChip? TryCreateHc182(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required: the nine inputs -- /G1(1) /P1(2) /G0(3) /P0(4) /G3(5)
        // /P3(6) Cn(13) /G2(14) /P2(15). Unused /P and /G inputs must be
        // tied HIGH (not asserted) in the schematic, as on the real part.
        // All five outputs are individually OPTIONAL: a two-slice cascade
        // uses only Cn+x, and the group /G and /P outputs only feed a
        // further lookahead level (cf. the '191 cascade pins).
        int[] needed = { 1, 2, 3, 4, 5, 6, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Optional(int pin, string name) =>
            pinToNet.TryGetValue(pin, out Net? net) && net is not null
                ? net : new Net(-1, name);

        return new Hc182(
            g1: Get(1), p1: Get(2), g0: Get(3), p0: Get(4),
            g3: Get(5), p3: Get(6),
            pGrp: Optional(7, "pgrp-unconnected"),
            cnZ: Optional(9, "cnz-unconnected"),
            gGrp: Optional(10, "ggrp-unconnected"),
            cnY: Optional(11, "cny-unconnected"),
            cnX: Optional(12, "cnx-unconnected"),
            cn: Get(13), g2: Get(14), p2: Get(15),
            delayPs: TtlTiming.ResolvePs(device));
    }

    private static IChip? TryCreateHc244(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        foreach (int p in OctalBufferPins)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        // Bank 1: /1OE(1) gates 1A1..1A4 (2,4,6,8) → 1Y1..1Y4 (18,16,14,12).
        // Bank 2: /2OE(19) gates 2A1..2A4 (11,13,15,17) → 2Y1..2Y4 (9,7,5,3).
        return new Hc244(
            oe1N: Get(1),
            a1: Get(2), a2: Get(4), a3: Get(6), a4: Get(8),
            y1: Get(18), y2: Get(16), y3: Get(14), y4: Get(12),
            oe2N: Get(19),
            a5: Get(11), a6: Get(13), a7: Get(15), a8: Get(17),
            y5: Get(9), y6: Get(7), y7: Get(5), y8: Get(3),
            delayPs: TtlTiming.ResolvePs(device));
    }

    private static IChip? TryCreateHc245(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        foreach (int p in OctalBufferPins)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        // DIR(1), /OE(19). A1..A8 = pins 2..9; B1..B8 = pins 18..11.
        return new Hc245(
            dir: Get(1),
            a1: Get(2), a2: Get(3), a3: Get(4), a4: Get(5),
            a5: Get(6), a6: Get(7), a7: Get(8), a8: Get(9),
            b1: Get(18), b2: Get(17), b3: Get(16), b4: Get(15),
            b5: Get(14), b6: Get(13), b7: Get(12), b8: Get(11),
            oeN: Get(19),
            delayPs: TtlTiming.ResolvePs(device));
    }

    private static IChip? TryCreateHc541(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        foreach (int p in OctalBufferPins)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        // /OE1(1) and /OE2(19) ANDed. A1..A8 = pins 2..9; Y1..Y8 = pins 18..11.
        return new Hc541(
            oe1N: Get(1),
            a1: Get(2), a2: Get(3), a3: Get(4), a4: Get(5),
            a5: Get(6), a6: Get(7), a7: Get(8), a8: Get(9),
            y1: Get(18), y2: Get(17), y3: Get(16), y4: Get(15),
            y5: Get(14), y6: Get(13), y7: Get(12), y8: Get(11),
            oe2N: Get(19),
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IEnumerable<IChip> CreateHc393Halves(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        long delayPs = TtlTiming.ResolvePs(device);

        IChip? a = TryHc393Half(pinToNet, $"{device.Designator}A", delayPs,
            clk: 1, clr: 2, q0: 3, q1: 4, q2: 5, q3: 6);
        if (a is not null) yield return a;

        IChip? b = TryHc393Half(pinToNet, $"{device.Designator}B", delayPs,
            clk: 13, clr: 12, q0: 11, q1: 10, q2: 9, q3: 8);
        if (b is not null) yield return b;
    }

    private IChip? TryHc393Half(IReadOnlyDictionary<int, Net> pinToNet, string label, long delayPs,
        int clk, int clr, int q0, int q1, int q2, int q3)
    {
        if (!pinToNet.TryGetValue(clk, out Net? clkN) || clkN is null) return null;
        if (!pinToNet.TryGetValue(clr, out Net? clrN) || clrN is null) return null;
        if (!pinToNet.TryGetValue(q0, out Net? q0N) || q0N is null) return null;
        if (!pinToNet.TryGetValue(q1, out Net? q1N) || q1N is null) return null;
        if (!pinToNet.TryGetValue(q2, out Net? q2N) || q2N is null) return null;
        if (!pinToNet.TryGetValue(q3, out Net? q3N) || q3N is null) return null;
        return new Hc393Half(clkN, clrN, q0N, q1N, q2N, q3N, label, logger, delayPs);
    }

    private IEnumerable<IChip> CreateHc390Halves(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        long delayPs = TtlTiming.ResolvePs(device);

        // Half 1: CKA=1, MR=2, QA=3, CKB=4, QB=5, QC=6, QD=7.
        IChip? a = TryHc390Half(pinToNet, $"{device.Designator}A", delayPs,
            cka: 1, ckb: 4, mr: 2, qa: 3, qb: 5, qc: 6, qd: 7);
        if (a is not null) yield return a;

        // Half 2: CKA=15, MR=14, QA=13, CKB=12, QB=11, QC=10, QD=9.
        IChip? b = TryHc390Half(pinToNet, $"{device.Designator}B", delayPs,
            cka: 15, ckb: 12, mr: 14, qa: 13, qb: 11, qc: 10, qd: 9);
        if (b is not null) yield return b;
    }

    private IChip? TryHc390Half(IReadOnlyDictionary<int, Net> pinToNet, string label, long delayPs,
        int cka, int ckb, int mr, int qa, int qb, int qc, int qd)
    {
        if (!pinToNet.TryGetValue(cka, out Net? ckaN) || ckaN is null) return null;
        if (!pinToNet.TryGetValue(ckb, out Net? ckbN) || ckbN is null) return null;
        if (!pinToNet.TryGetValue(mr, out Net? mrN) || mrN is null) return null;
        if (!pinToNet.TryGetValue(qa, out Net? qaN) || qaN is null) return null;
        if (!pinToNet.TryGetValue(qb, out Net? qbN) || qbN is null) return null;
        if (!pinToNet.TryGetValue(qc, out Net? qcN) || qcN is null) return null;
        if (!pinToNet.TryGetValue(qd, out Net? qdN) || qdN is null) return null;
        return new Hc390Half(ckaN, ckbN, mrN, qaN, qbN, qcN, qdN, label, logger, delayPs);
    }


    private IChip? TryCreateHc74(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Inputs (D, CLK, and the async /PRE, /CLR of BOTH halves) must be
        // wired -- a floating input is a real fault. Outputs Q//Q (pins 5,6,
        // 8,9) are OPTIONAL: an unused flip-flop half leaves them open, and an
        // open output must never block instantiation (cf. the '283 carry-out).
        int[] needed = { 1, 2, 3, 4, 10, 11, 12, 13 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);

        return new Hc74(
            aClrN: Get(1), aD: Get(2), aClk: Get(3), aPreN: Get(4),
            aQ: Opt(5, "aq-nc"), aQn: Opt(6, "aqn-nc"),
            bQn: Opt(8, "bqn-nc"), bQ: Opt(9, "bq-nc"),
            bPreN: Get(10), bClk: Get(11), bD: Get(12), bClrN: Get(13),
            label: "74", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc139(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 1 /AE, 2/3 AA0/AA1, 15 /BE,
        // 14/13 BA0/BA1. The eight /Y outputs are OPTIONAL -- an address
        // decoder routinely leaves unused selects open (a 2-bank register
        // file uses /Y0//Y1 and leaves /Y2//Y3 for the 4-bank build), and an
        // open output must never block instantiation -- the '138/'154
        // decoder precedent, previously missed on this chip. TTL011 still
        // flags genuinely unwired INPUTS at design time.
        int[] needed = { 1, 2, 3, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc139(
            aeN: Get(1), aa0: Get(2), aa1: Get(3),
            ay0N: Opt(4, "ay0-nc"), ay1N: Opt(5, "ay1-nc"),
            ay2N: Opt(6, "ay2-nc"), ay3N: Opt(7, "ay3-nc"),
            by3N: Opt(9, "by3-nc"), by2N: Opt(10, "by2-nc"),
            by1N: Opt(11, "by1-nc"), by0N: Opt(12, "by0-nc"),
            ba1: Get(13), ba0: Get(14), beN: Get(15),
            label: "139", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc138(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 1..3 A0..A2 and 4..6 the three
        // enables (/E1, /E2, E3). The eight /Y outputs are OPTIONAL -- an
        // address decoder routinely leaves unused selects open, and an open
        // output must never block instantiation (cf. the '161 Q outputs and
        // the '283 carry-out). TTL011 still flags genuinely unwired INPUTS
        // at design time.
        int[] needed = { 1, 2, 3, 4, 5, 6 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc138(
            a0: Get(1), a1: Get(2), a2: Get(3),
            e1N: Get(4), e2N: Get(5), e3: Get(6),
            y7N: Opt(7, "y7-nc"), y6N: Opt(9, "y6-nc"),
            y5N: Opt(10, "y5-nc"), y4N: Opt(11, "y4-nc"),
            y3N: Opt(12, "y3-nc"), y2N: Opt(13, "y2-nc"),
            y1N: Opt(14, "y1-nc"), y0N: Opt(15, "y0-nc"),
            label: "138", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc154(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 18/19 the two enables and
        // 20..23 A3..A0. The sixteen /Y outputs are OPTIONAL -- same
        // decoder precedent as the '138.
        int[] needed = { 18, 19, 20, 21, 22, 23 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc154(
            y0N: Opt(1, "y0-nc"), y1N: Opt(2, "y1-nc"),
            y2N: Opt(3, "y2-nc"), y3N: Opt(4, "y3-nc"),
            y4N: Opt(5, "y4-nc"), y5N: Opt(6, "y5-nc"),
            y6N: Opt(7, "y6-nc"), y7N: Opt(8, "y7-nc"),
            y8N: Opt(9, "y8-nc"), y9N: Opt(10, "y9-nc"),
            y10N: Opt(11, "y10-nc"), y11N: Opt(13, "y11-nc"),
            y12N: Opt(14, "y12-nc"), y13N: Opt(15, "y13-nc"),
            y14N: Opt(16, "y14-nc"), y15N: Opt(17, "y15-nc"),
            e0N: Get(18), e1N: Get(19),
            a3: Get(20), a2: Get(21), a1: Get(22), a0: Get(23),
            label: "154", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc161(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Same pinout as the '163. Required pins are the INPUTS only:
        // 1 /CLR, 2 CLK, 3..6 D0..D3, 7 CEP, 9 /LD, 10 CET. The four Q
        // outputs (14,13,12,11 = Q0..Q3) and RCO (pin 15) are OPTIONAL --
        // an open output drives nothing and must never block instantiation
        // (cf. the '74/'273 outputs and the '283 carry-out). A counter that
        // uses only its low bits leaves the upper Q's open. TTL011 still
        // flags genuinely unwired INPUTS at design time.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc161(
            clrN: Get(1), clkN: Get(2),
            d0: Get(3), d1: Get(4), d2: Get(5), d3: Get(6),
            cepN: Get(7), ldN: Get(9), cetN: Get(10),
            q0: Opt(14, "q0-nc"), q1: Opt(13, "q1-nc"),
            q2: Opt(12, "q2-nc"), q3: Opt(11, "q3-nc"),
            rcoN: Opt(15, "rco-nc"),
            label: "161", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc163(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 1 /CLR, 2 CLK, 3..6 D0..D3,
        // 7 CEP, 9 /LD, 10 CET. The four Q outputs (14,13,12,11 = Q0..Q3)
        // and RCO (pin 15) are OPTIONAL -- an open output drives nothing and
        // must never block instantiation (cf. the '74/'273 outputs and the
        // '283 carry-out). A single counter that uses only its low bits
        // leaves the upper Q's open. TTL011 still flags genuinely unwired
        // INPUTS at design time.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc163(
            clrN: Get(1), clkN: Get(2),
            d0: Get(3), d1: Get(4), d2: Get(5), d3: Get(6),
            cepN: Get(7), ldN: Get(9), cetN: Get(10),
            q0: Opt(14, "q0-nc"), q1: Opt(13, "q1-nc"),
            q2: Opt(12, "q2-nc"), q3: Opt(11, "q3-nc"),
            rcoN: Opt(15, "rco-nc"),
            label: "163", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc173(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins: 1 M, 2 N, 3..6 Q0..Q3, 7 CLK, 9 G1, 10 G2,
        // 11..14 D3..D0 (datasheet has D inputs going high-to-low pin number),
        // 15 CLR. All 14 active pins must be present; the chip has no
        // optional cascade outputs.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        // Hc173 constructor wants D0..D3 in that order; map from physical
        // pins: D0 is pin 14, D1 is pin 13, D2 is pin 12, D3 is pin 11.
        return new Hc173(
            m: Get(1), n: Get(2),
            q0: Get(3), q1: Get(4), q2: Get(5), q3: Get(6),
            clkN: Get(7),
            g1N: Get(9), g2N: Get(10),
            d0: Get(14), d1: Get(13), d2: Get(12), d3: Get(11),
            clr: Get(15),
            label: "173", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc273(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // /CLR (pin 1) and CLK (pin 11) are required -- a floating clock or
        // reset on a register is a real fault. The eight D inputs and eight
        // Q outputs are OPTIONAL: a partially-used '273 (e.g. four of eight
        // flip-flops, as in the Mini Blinky synchronizer) leaves the spare
        // pins open. An unconnected D gets a local stand-in net that reads
        // as Low; an unconnected Q gets a stand-in that drives nothing.
        // Floating-input diagnostics (TTL011) still flag genuinely unwired
        // pins at design time.
        if (!pinToNet.TryGetValue(1, out Net? clrN) || clrN is null) return null;
        if (!pinToNet.TryGetValue(11, out Net? clkN) || clkN is null) return null;

        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);

        return new Hc273(
            clrN: clrN, clkN: clkN,
            d0: Opt(3, "d0-nc"), d1: Opt(4, "d1-nc"),
            d2: Opt(7, "d2-nc"), d3: Opt(8, "d3-nc"),
            d4: Opt(13, "d4-nc"), d5: Opt(14, "d5-nc"),
            d6: Opt(17, "d6-nc"), d7: Opt(18, "d7-nc"),
            q0: Opt(2, "q0-nc"), q1: Opt(5, "q1-nc"),
            q2: Opt(6, "q2-nc"), q3: Opt(9, "q3-nc"),
            q4: Opt(12, "q4-nc"), q5: Opt(15, "q5-nc"),
            q6: Opt(16, "q6-nc"), q7: Opt(19, "q7-nc"),
            label: "273", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc374(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Same policy as the '273: /OE (pin 1) and CLK (pin 11) are required
        // -- a floating clock or output enable on a bus register is a real
        // fault. The eight D inputs and eight Q outputs are OPTIONAL: a
        // partially-used '374 leaves the spare pins open. An unconnected D
        // gets a local stand-in net that reads as Low; an unconnected Q gets
        // a stand-in that drives nothing. Floating-input diagnostics (TTL011)
        // still flag genuinely unwired pins at design time.
        if (!pinToNet.TryGetValue(1, out Net? oeN) || oeN is null) return null;
        if (!pinToNet.TryGetValue(11, out Net? clkN) || clkN is null) return null;

        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);

        return new Hc374(
            oeN: oeN, clkN: clkN,
            d0: Opt(3, "d0-nc"), d1: Opt(4, "d1-nc"),
            d2: Opt(7, "d2-nc"), d3: Opt(8, "d3-nc"),
            d4: Opt(13, "d4-nc"), d5: Opt(14, "d5-nc"),
            d6: Opt(17, "d6-nc"), d7: Opt(18, "d7-nc"),
            q0: Opt(2, "q0-nc"), q1: Opt(5, "q1-nc"),
            q2: Opt(6, "q2-nc"), q3: Opt(9, "q3-nc"),
            q4: Opt(12, "q4-nc"), q5: Opt(15, "q5-nc"),
            q6: Opt(16, "q6-nc"), q7: Opt(19, "q7-nc"),
            label: "374", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc377(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Same policy as the '273/'374: /EN (pin 1) and CLK (pin 11) are
        // required -- a floating clock or enable on a register is a real
        // fault. The eight D inputs and eight Q outputs are OPTIONAL: a
        // partially-used '377 leaves the spare pins open. An unconnected D
        // gets a local stand-in net that reads as Low; an unconnected Q gets
        // a stand-in that drives nothing. Floating-input diagnostics (TTL011)
        // still flag genuinely unwired pins at design time.
        if (!pinToNet.TryGetValue(1, out Net? enN) || enN is null) return null;
        if (!pinToNet.TryGetValue(11, out Net? clkN) || clkN is null) return null;

        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);

        return new Hc377(
            enN: enN, clkN: clkN,
            d0: Opt(3, "d0-nc"), d1: Opt(4, "d1-nc"),
            d2: Opt(7, "d2-nc"), d3: Opt(8, "d3-nc"),
            d4: Opt(13, "d4-nc"), d5: Opt(14, "d5-nc"),
            d6: Opt(17, "d6-nc"), d7: Opt(18, "d7-nc"),
            q0: Opt(2, "q0-nc"), q1: Opt(5, "q1-nc"),
            q2: Opt(6, "q2-nc"), q3: Opt(9, "q3-nc"),
            q4: Opt(12, "q4-nc"), q5: Opt(15, "q5-nc"),
            q6: Opt(16, "q6-nc"), q7: Opt(19, "q7-nc"),
            label: "377", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc670(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 15/1/2/3 D1..D4, 14/13 WA/WB,
        // 12 /GW, 5/4 RA/RB, 11 /GR. The four Q outputs (10, 9, 7, 6 =
        // Q1..Q4) are OPTIONAL -- an open output drives nothing and must
        // never block instantiation (the '181 lesson, applied from the
        // start). TTL011 still flags genuinely unwired INPUTS at design
        // time.
        int[] needed = { 1, 2, 3, 4, 5, 11, 12, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc670(
            d1: Get(15), d2: Get(1), d3: Get(2), d4: Get(3),
            wa: Get(14), wb: Get(13), gwN: Get(12),
            ra: Get(5), rb: Get(4), grN: Get(11),
            q1: Opt(10, "q1-nc"), q2: Opt(9, "q2-nc"),
            q3: Opt(7, "q3-nc"), q4: Opt(6, "q4-nc"),
            label: "670", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc283(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // C4 (pin 9) is the carry-out cascade pin and is OPTIONAL -- the
        // top adder of a chain leaves it open. Treat it like RCO on the
        // '161: stand in a throwaway net when unconnected. Pin 16 VCC and
        // pin 8 GND are excluded (power isn't wired through the chip model).
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net c4Net = pinToNet.TryGetValue(9, out Net? c4) && c4 is not null
            ? c4
            : new Net(-1, "c4-unconnected");  // local stand-in, drives nothing

        return new Hc283(
            a1: Get(5), a2: Get(3), a3: Get(14), a4: Get(12),
            b1: Get(6), b2: Get(2), b3: Get(15), b4: Get(11),
            c0: Get(7),
            s1: Get(4), s2: Get(1), s3: Get(13), s4: Get(10),
            c4: c4Net,
            label: "283", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc688(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // /G (pin 1) and the /P=Q output (pin 19) are required -- the chip
        // is inert without them. The sixteen P/Q data pins are OPTIONAL: an
        // unconnected pin gets a local stand-in net that reads as Low, so a
        // narrower-than-8-bit comparison (e.g. a 4-bit PC against four DIP
        // switches with the upper bit pairs left open) sees 0 == 0 on the
        // unused bits and still matches -- same precedent as the parallel
        // memories. Floating-input diagnostics (TTL011) still flag genuinely
        // unwired pins at design time.
        if (!pinToNet.TryGetValue(1, out Net? gN) || gN is null) return null;
        if (!pinToNet.TryGetValue(19, out Net? outN) || outN is null) return null;

        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);

        return new Hc688(
            gN: gN,
            p0: Opt(2, "p0-nc"), p1: Opt(4, "p1-nc"),
            p2: Opt(6, "p2-nc"), p3: Opt(8, "p3-nc"),
            p4: Opt(12, "p4-nc"), p5: Opt(14, "p5-nc"),
            p6: Opt(16, "p6-nc"), p7: Opt(18, "p7-nc"),
            q0: Opt(3, "q0-nc"), q1: Opt(5, "q1-nc"),
            q2: Opt(7, "q2-nc"), q3: Opt(9, "q3-nc"),
            q4: Opt(11, "q4-nc"), q5: Opt(13, "q5-nc"),
            q6: Opt(15, "q6-nc"), q7: Opt(17, "q7-nc"),
            pEqQN: outN,
            label: "688", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc151(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // The inputs are required: all eight data inputs (I0..I3 pins 4,3,2,1;
        // I4..I7 pins 15,14,13,12), the three selects (S2 pin 9, S1 pin 10,
        // S0 pin 11) and the enable (/E pin 7). Pin 16 VCC and pin 8 GND are
        // excluded -- power isn't wired through the chip model.
        //
        // The two outputs Y (pin 5) and /Y (pin 6) are OPTIONAL: nearly every
        // real use wires only one of the pair (the Mini Blinky TOS source mux
        // uses Y and leaves /Y open). An open output drives nothing and must
        // never block instantiation, so an unconnected output gets a local
        // stand-in net (cf. the '153 Y outputs and the '161/'163 Qs).
        // Floating-INPUT diagnostics (TTL011) still flag genuinely unwired
        // inputs at design time.
        int[] needed = { 1, 2, 3, 4, 7, 9, 10, 11, 12, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc151(
            i3: Get(1), i2: Get(2), i1: Get(3), i0: Get(4),
            y: Opt(5, "y-nc"), yN: Opt(6, "yn-nc"),
            eN: Get(7),
            s2: Get(9), s1: Get(10), s0: Get(11),
            i7: Get(12), i6: Get(13), i5: Get(14), i4: Get(15),
            label: "151", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc153(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // The inputs are required: both enables (/1E pin 1, /2E pin 15), both
        // shared selects (S1 pin 2, S0 pin 14) and all eight data inputs
        // (1I0..1I3 pins 6,5,4,3; 2I0..2I3 pins 10,11,12,13). Pin 16 VCC and
        // pin 8 GND are excluded -- power isn't wired through the chip model.
        //
        // The two outputs 1Y (pin 7) and 2Y (pin 9) are OPTIONAL: a half-used
        // '153, or one whose Y feeds nothing yet, leaves them open. An open
        // output drives nothing and must never block instantiation, so an
        // unconnected Y gets a local stand-in net (cf. the '161/'163 Q outputs
        // and the '283 carry-out). Floating-INPUT diagnostics (TTL011) still
        // flag genuinely unwired inputs at design time.
        int[] needed = { 1, 2, 3, 4, 5, 6, 10, 11, 12, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc153(
            e1N: Get(1), s1: Get(2),
            i1_3: Get(3), i1_2: Get(4), i1_1: Get(5), i1_0: Get(6),
            y1: Opt(7, "1y-nc"),
            y2: Opt(9, "2y-nc"),
            i2_0: Get(10), i2_1: Get(11), i2_2: Get(12), i2_3: Get(13),
            s0: Get(14), e2N: Get(15),
            label: "153", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }



    /// <summary>
    /// The authoritative list of part identifiers this factory can model.
    /// Must stay in sync with CreateForUnits / CreateForUnit. Visual-only
    /// passives (LED, capacitor, crystal) are included even though they
    /// produce no IChip -- they have no electrical effect on the simulator
    /// but they are not "unsupported," so they must not raise TTL020.
    /// </summary>
    public bool IsSimulated(BuildDevice device) => device.PartIdentifier switch
    {
        // Box-chip ICs (per-unit dispatch in CreateForUnit).
        "47" or "74" or "138" or "139" or "151" or "153" or "154" or "157" or "161" or "163" or "173" or "181" or "182" or "191" or "194"
            or "244" or "245" or "257" or "273" or "283" or "299" or "374" or "377" or "541" or "573" or "574" or "670" or "688" or "7seg-ca"
            => true,
        // Reset supervisor (per-unit dispatch in CreateForUnit).
        "DS1813"
            => true,
        // Timer ICs: 555 single core, 556 dual core. Digital Schmitt/astable
        // stand-in, special-cased at the top of CreateForUnits (CreateTimerCores).
        "NE555" or "NE556"
            => true,
        // GAL/PLD: fuse-map evaluation (all modes); fuse map in device.Program.
        "GAL16V8" or "GAL20V8" or "GAL22V10"
            => true,
        // Parallel memory family (28C-series EEPROM + pin-compatible SRAM),
        // special-cased in CreateForUnits because contents are per-device.
        "28C256" or "28C128" or "28C64" or "28C16" or "62256"
            or "CY7C199" or "6116" or "2114" or "6264" or "W24512"
            => true,
        // Gate ICs -- all single-part boxes, special-cased at the top of
        // CreateForUnits (CreateGateChip). Plus the dual counters and the
        // '125/'126 tri-state buffer sections, which ride the same
        // one-core-per-section dispatch.
        "00" or "02" or "04" or "08" or "10" or "14" or "20" or "30"
            or "32" or "86" or "125" or "126" or "390" or "393"
            => true,
        // Electrically modelled passives.
        "resistor" or "resnet-sip9" or "button" or "button-4" or "switch" or "spdt-switch" or "jumper-2pin" or "jumper-3pin" or "diode"
            => true,
        // Visual-only parts: no electrical model, but not "unsupported".
        "led" or "capacitor" or "polarized-capacitor" or "crystal"
            => true,
        "hdr-out-2" or "hdr-out-3" or "hdr-out-4" or "hdr-out-6" or "hdr-out-8"
            => true,
        // Anything else -- decoders, '193, etc. -- is on the catalogue but
        // does not have a sim model yet.
        _ => false
    };

    private IChip? TryCreateMemory(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Per-part pinout + behaviour. Address pins listed LSB-first; data pins
        // I/O0..I/O7. The 28-pin family (28C256/128/64 EEPROM and pin-compatible
        // 62256 SRAM) shares one layout differing only in address-line count;
        // the 24-pin 28C16 has its own.
        // The default access time (speed grade) is NOT set per-case here -- it
        // comes from PartDelayDefaults after the switch, the single table the
        // property grid also reads, so the simulator and the grid cannot drift.
        int[] addr, data; int ce, oe, we, cs2 = -1; bool writable;
        switch (device.PartIdentifier)
        {
            case "28C256":
            case "62256":
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2, 26, 1 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27;
                writable = device.PartIdentifier == "62256";
                break;
            case "CY7C199":
                // 28-pin 32K x 8 SRAM, identical JEDEC pinout to the 62256;
                // the -15 grade is a 15 ns part (see PartDelayDefaults). Always writable.
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2, 26, 1 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27; writable = true;
                break;
            case "6264":
                // 28-pin 8K x 8 SRAM (also IDT7164 / HM6264). Same A0..A12 and
                // data map as the 28C64, but with a second, active-HIGH chip
                // enable CS2 on pin 26 (where the 62256 carries A13). /CS1 on 20,
                // /OE 22, /WE 27. Selected only when /CS1 LOW and CS2 HIGH.
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2 }; // A0..A12
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27; cs2 = 26; writable = true;
                break;
            case "W24512":
                // 32-pin 64K x 8 SRAM (also IS61C512 / UM61512). 16 address lines;
                // active-LOW /CS1 on 22 and active-HIGH CS2 on 30; /OE 24, /WE 29.
                // Selected only when /CS1 LOW and CS2 HIGH.
                addr = new[] { 12, 11, 10, 9, 8, 7, 6, 5, 27, 26, 23, 25, 4, 28, 3, 31 }; // A0..A15
                data = new[] { 13, 14, 15, 17, 18, 19, 20, 21 };
                ce = 22; oe = 24; we = 29; cs2 = 30; writable = true;
                break;
            case "28C128":
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2, 26 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27; writable = false;
                break;
            case "28C64":
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27; writable = false;
                break;
            case "28C16":
                addr = new[] { 8, 7, 6, 5, 4, 3, 2, 1, 23, 22, 19 };
                data = new[] { 9, 10, 11, 13, 14, 15, 16, 17 };
                ce = 18; oe = 20; we = 21; writable = false;
                break;
            case "6116":
                // 24-pin 2K x 8 SRAM, same JEDEC pinout as the 28C16; the
                // 6116P-70 is the 70 ns grade.
                addr = new[] { 8, 7, 6, 5, 4, 3, 2, 1, 23, 22, 19 };
                data = new[] { 9, 10, 11, 13, 14, 15, 16, 17 };
                ce = 18; oe = 20; we = 21; writable = true;
                break;
            case "2114":
                // 18-pin 1K x 4 SRAM. Nibble-wide, and it has NO output-enable
                // pin (oe = -1): outputs drive whenever /CS is LOW and /WE HIGH.
                addr = new[] { 5, 6, 7, 4, 3, 2, 1, 17, 16, 15 }; // A0..A9
                data = new[] { 14, 13, 12, 11 };                  // I/O1..I/O4
                ce = 8; oe = -1; we = 10; writable = true;
                break;
            default:
                return null;
        }

        // Resolve the access time (picoseconds). An explicit per-part
        // "Propagation Delay (ns)" from the property grid wins; otherwise use the
        // part's default speed grade from PartDelayDefaults -- the same table the
        // property grid displays, so the simulator and the grid never disagree.
        long defaultNs = PartDelayDefaults.DefaultDelayNs(device.PartIdentifier) ?? 250;
        long access = (device.PropagationDelayNs is int ns && ns > 0 ? ns : defaultNs) * 1000L;

        // All signal pins are optional: an unconnected pin gets a local stand-in
        // net (reads as 0, never drives) so partial wiring -- e.g. only the low
        // address lines in a small test -- can't make the chip fail to
        // instantiate (cf. the '283 carry-out). Floating-input diagnostics still
        // flag genuinely unwired pins.
        int tag = 0;
        Net Opt(int pin) =>
            pinToNet.TryGetValue(pin, out Net? net) && net is not null
                ? net : new Net(-1, $"mem-nc-{tag++}");

        Net[] addrNets = Array.ConvertAll(addr, Opt);
        Net[] dataNets = Array.ConvertAll(data, Opt);
        Net ceNet = Opt(ce), weNet = Opt(we);
        Net? oeNet = oe >= 1 ? Opt(oe) : null;   // 2114 has no /OE pin
        Net? cs2Net = cs2 >= 1 ? Opt(cs2) : null; // active-HIGH CS2 (6264/7164, W24512A)

        // EEPROM parts take their program from the embedded Intel HEX; SRAM
        // powers up blank. A malformed image is logged and treated as blank
        // rather than failing the build.
        byte[]? contents = null;
        if (!writable && !string.IsNullOrWhiteSpace(device.Program))
        {
            try { contents = IntelHex.Parse(device.Program); }
            catch (FormatException ex)
            {
                if (logger is not null)
                    logger.LogWarning("{Ref}: bad Intel HEX program ({Msg}); treating as blank.",
                        device.Designator, ex.Message);
            }
        }

        bool hasOe = oe >= 1;
        bool hasCs2 = cs2 >= 1;
        int[] pins = new int[addr.Length + data.Length + (hasOe ? 3 : 2) + (hasCs2 ? 1 : 0)];
        Array.Copy(addr, 0, pins, 0, addr.Length);
        Array.Copy(data, 0, pins, addr.Length, data.Length);
        int p = addr.Length + data.Length;
        pins[p++] = ce;
        if (hasOe) pins[p++] = oe;
        pins[p++] = we;
        if (hasCs2) pins[p] = cs2;

        return new ParallelMemory(
            addrNets, dataNets, ceNet, oeNet, weNet,
            writable, contents, access, pins,
            label: device.PartIdentifier, logger: logger, cs2N: cs2Net);
    }

    /// <summary>
    /// Builds a GAL model from the fuse map carried in <c>device.Program</c>
    /// (JEDEC text, the same field the EEPROM uses for Intel HEX). The
    /// 16V8/20V8 build a <see cref="Gal"/>; the 22V10 builds its sibling
    /// <see cref="Gal22V10"/>. An empty or malformed map leaves the array
    /// blank and is logged, rather than failing the build. The length-clamped
    /// copy lets both QF5828 and QF5892 (UES-bearing) 22V10 images fit.
    /// </summary>
    private IChip? TryCreateGal(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        bool is22V10 = device.PartIdentifier == Gal22V10Device.PartNumber;
        GalDevice? gd = is22V10 ? null : GalDevice.ForPartNumber(device.PartIdentifier);
        if (!is22V10 && gd is null) return null;

        bool[] fuses = new bool[is22V10 ? Gal22V10Device.FuseCount : gd!.FuseCount];
        if (!string.IsNullOrWhiteSpace(device.Program))
        {
            try
            {
                bool[] parsed = JedecFuseMap.Parse(device.Program).Fuses;
                Array.Copy(parsed, fuses, Math.Min(parsed.Length, fuses.Length));
            }
            catch (FormatException ex)
            {
                logger?.LogWarning("{Ref}: invalid JEDEC fuse map ({Msg}); treating as blank.",
                    device.Designator, ex.Message);
            }
        }

        // Honour an explicit per-part "Propagation Delay (ns)" from the property
        // grid; otherwise use the part's nominal grade from PartDelayDefaults --
        // the one table the memory path and the property grid also read.
        // Nanoseconds -> picoseconds.
        long delayPs = device.PropagationDelayNs is int ns && ns > 0
            ? ns * 1000L
            : (PartDelayDefaults.DefaultDelayNs(device.PartIdentifier)
               ?? PartDelayDefaults.GalDefaultDelayNs) * 1000L;

        return is22V10
            ? new Gal22V10(fuses, pinToNet, delayPs)
            : new Gal(gd!, fuses, pinToNet, delayPs);
    }

    private static IChip? TryCreateHc157(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins are the INPUTS only: 1 S, 15 /E,
        // 2 1I0, 3 1I1, 5 2I0, 6 2I1, 11 3I0, 10 3I1, 14 4I0, 13 4I1.
        // The four Y outputs (4 1Y, 7 2Y, 9 3Y, 12 4Y) are OPTIONAL -- an
        // open output drives nothing and must never block instantiation
        // (the '181 lesson). A partially-used '157 ties off its spare
        // sections' inputs and leaves the Y open; requiring Y silently
        // dropped the whole part from the simulation (TTL021). TTL011
        // still flags genuinely unwired INPUTS at design time.
        // Pin 8 GND and pin 16 VCC are consumed by the build pipeline and
        // aren't wired through the chip model.
        int[] needed = { 1, 15, 2, 3, 5, 6, 10, 11, 13, 14 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc157(
            s: Get(1), enN: Get(15),
            i1_0: Get(2), i1_1: Get(3), y1: Opt(4, "y1-nc"),
            i2_0: Get(5), i2_1: Get(6), y2: Opt(7, "y2-nc"),
            i3_0: Get(11), i3_1: Get(10), y3: Opt(9, "y3-nc"),
            i4_0: Get(14), i4_1: Get(13), y4: Opt(12, "y4-nc"),
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc257(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Same pinout as the '157 except pin 15 is /OE (tri-state) not /E.
        // Required pins are the INPUTS only: 1 S, 15 /OE, 2 1I0, 3 1I1,
        // 5 2I0, 6 2I1, 11 3I0, 10 3I1, 14 4I0, 13 4I1. The four Y outputs
        // (4 1Y, 7 2Y, 9 3Y, 12 4Y) are OPTIONAL, exactly as on the '157.
        // Pin 8 GND and pin 16 VCC are consumed by the build pipeline.
        int[] needed = { 1, 15, 2, 3, 5, 6, 10, 11, 13, 14 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net Opt(int pin, string tag) =>
            pinToNet.TryGetValue(pin, out Net? x) && x is not null
                ? x : new Net(-1, tag);   // local stand-in, drives nothing

        return new Hc257(
            s: Get(1), oeN: Get(15),
            i1_0: Get(2), i1_1: Get(3), y1: Opt(4, "y1-nc"),
            i2_0: Get(5), i2_1: Get(6), y2: Opt(7, "y2-nc"),
            i3_0: Get(11), i3_1: Get(10), y3: Opt(9, "y3-nc"),
            i4_0: Get(14), i4_1: Get(13), y4: Opt(12, "y4-nc"),
            delayPs: TtlTiming.ResolvePs(device));
    }





    // The three octal bus buffers share a 20-pin layout: signal pins
    // 1..9 and 11..19, with 10/GND and 20/VCC excluded. None has an
    // optional cascade pin, so all 18 signal pins are required.
    private static readonly int[] OctalBufferPins =
        { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 19 };





    private static bool IsMemoryPart(string id) =>
        id is "28C256" or "28C128" or "28C64" or "28C16" or "62256"
           or "CY7C199" or "6116" or "2114" or "6264" or "W24512";






    private static IChip? TryCreateLs47(IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins: 1,2,6,7 (BCD A,B,C,D); 3,5,4 (LT,RBI,BI); 13,12,11,10,9,15,14 (segs a..g).
        int[] needed = { 7, 1, 2, 6, 3, 5, 4, 13, 12, 11, 10, 9, 15, 14 };
        Net[] nets = new Net[needed.Length];
        for (int i = 0; i < needed.Length; i++)
        {
            if (!pinToNet.TryGetValue(needed[i], out Net? n) || n is null) return null;
            nets[i] = n;
        }
        return new Ls47(
            a: nets[0], b: nets[1], c: nets[2], d: nets[3],
            lt: nets[4], rbi: nets[5], bi: nets[6],
            segA: nets[7], segB: nets[8], segC: nets[9], segD: nets[10],
            segE: nets[11], segF: nets[12], segG: nets[13]);
    }

    private static IChip? TryCreateSevenSegCa(IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required: pins 1..7 (segments a..g) and pin 9 (common).
        // Optional: pin 8 (dp). When unwired, dp will resolve to Unknown and stay dark.
        int[] required = { 1, 2, 3, 4, 5, 6, 7, 9 };
        foreach (int p in required)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net dpNet = pinToNet.TryGetValue(8, out Net? d) && d is not null
            ? d
            : new Net(-1, "dp-unconnected");  // local stand-in, stays Unknown forever

        return new SevenSegCa(
            a: Get(1), b: Get(2), c: Get(3), d: Get(4),
            e: Get(5), f: Get(6), g: Get(7),
            dp: dpNet, common: Get(9));
    }
}