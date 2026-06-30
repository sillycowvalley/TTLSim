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
            or "14" or "20" or "30" or "32" or "86")
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
                if (termA is not null && termB is not null)
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
        return new ButtonInput(p1, p2);
    }

    private static IChip? TryCreateDiode(IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Diode pins: 1 = anode, 2 = cathode (per DiodeUnit.BuildPins).
        pinToNet.TryGetValue(1, out Net? anode);
        pinToNet.TryGetValue(2, out Net? cathode);
        if (anode is null || cathode is null) return null;
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
            "153" => TryCreateHc153(device, pinToNet),
            "157" => TryCreateHc157(device, pinToNet),
            "257" => TryCreateHc257(device, pinToNet),
            "161" => TryCreateHc161(device, pinToNet),
            "163" => TryCreateHc163(device, pinToNet),
            "173" => TryCreateHc173(device, pinToNet),
            "181" => TryCreateHc181(device, pinToNet),
            "191" => TryCreateHc191(device, pinToNet),
            "244" => TryCreateHc244(device, pinToNet),
            "245" => TryCreateHc245(device, pinToNet),
            "273" => TryCreateHc273(device, pinToNet),
            "283" => TryCreateHc283(device, pinToNet),
            "541" => TryCreateHc541(device, pinToNet),
            "688" => TryCreateHc688(device, pinToNet),
            "DS1813" => TryCreateDs1813(pinToNet),
            "GAL16V8" or "GAL20V8" => TryCreateGal(device, pinToNet),
            "7seg-ca" => TryCreateSevenSegCa(pinToNet),
            _ => null
        };
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


    private static IChip? TryCreateHc181(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins from ChipPartDefinition.Ic74181 (pin 12 GND and pin
        // 24 VCC are excluded -- power pins aren't wired through the chip
        // model, they're consumed by the build pipeline). All 22 signal pins
        // must be present; nothing on the '181 is optional.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 8,
                         9, 10, 11, 13,
                         14, 15, 16, 17,
                         18, 19, 20, 21, 22, 23 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        return new Hc181(
            b0: Get(1), a0: Get(2),
            s3: Get(3), s2: Get(4), s1: Get(5), s0: Get(6),
            cn: Get(7), m: Get(8),
            f0: Get(9), f1: Get(10), f2: Get(11), f3: Get(13),
            aeqb: Get(14),
            y: Get(15), x: Get(16),
            cnP4: Get(17),
            b3: Get(18), a3: Get(19), b2: Get(20), a2: Get(21),
            b1: Get(22), a1: Get(23),
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

    private IChip? TryCreateHc161(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Same pinout as the '163. RCO (pin 15) is an optional cascade output.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 14, 13, 12, 11 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net rcoNet = pinToNet.TryGetValue(15, out Net? rco) && rco is not null
            ? rco
            : new Net(-1, "rco-unconnected");

        return new Hc161(
            clrN: Get(1), clkN: Get(2),
            d0: Get(3), d1: Get(4), d2: Get(5), d3: Get(6),
            cepN: Get(7), ldN: Get(9), cetN: Get(10),
            q0: Get(14), q1: Get(13), q2: Get(12), q3: Get(11),
            rcoN: rcoNet,
            label: "161", logger: logger,
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc163(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins: 1 /CLR, 2 CLK, 3..6 D0..D3, 7 CEP, 9 /LD, 10 CET,
        // 14,13,12,11 Q0..Q3. RCO (pin 15) is an optional cascade output --
        // a single counter leaves it unconnected.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 14, 13, 12, 11 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];
        Net rcoNet = pinToNet.TryGetValue(15, out Net? rco) && rco is not null
            ? rco
            : new Net(-1, "rco-unconnected");  // local stand-in, drives nothing

        return new Hc163(
            clrN: Get(1), clkN: Get(2),
            d0: Get(3), d1: Get(4), d2: Get(5), d3: Get(6),
            cepN: Get(7), ldN: Get(9), cetN: Get(10),
            q0: Get(14), q1: Get(13), q2: Get(12), q3: Get(11),
            rcoN: rcoNet,
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

    private IChip? TryCreateHc153(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins from ChipPartDefinition.Ic74153 (pin 16 VCC and pin 8
        // GND excluded). All 14 signal pins must be present.
        int[] needed = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        return new Hc153(
            e1N: Get(1), s1: Get(2),
            i1_3: Get(3), i1_2: Get(4), i1_1: Get(5), i1_0: Get(6), y1: Get(7),
            y2: Get(9), i2_0: Get(10), i2_1: Get(11), i2_2: Get(12), i2_3: Get(13),
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
        "47" or "74" or "153" or "157" or "161" or "163" or "173" or "181" or "191"
            or "244" or "245" or "257" or "273" or "283" or "541" or "688" or "7seg-ca"
            => true,
        // Reset supervisor (per-unit dispatch in CreateForUnit).
        "DS1813"
            => true,
        // Timer ICs: 555 single core, 556 dual core. Digital Schmitt/astable
        // stand-in, special-cased at the top of CreateForUnits (CreateTimerCores).
        "NE555" or "NE556"
            => true,
        // GAL/PLD: combinational fuse-map evaluation; fuse map in device.Program.
        "GAL16V8" or "GAL20V8"
            => true,
        // Parallel memory family (28C-series EEPROM + pin-compatible SRAM),
        // special-cased in CreateForUnits because contents are per-device.
        "28C256" or "28C128" or "28C64" or "28C16" or "62256"
            or "CY7C199" or "6116" or "2114"
            => true,
        // Gate ICs -- all single-part boxes, special-cased at the top of
        // CreateForUnits (CreateGateChip). Plus the dual counters.
        "00" or "02" or "04" or "08" or "10" or "14" or "20" or "30"
            or "32" or "86" or "390" or "393"
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
        int[] addr, data; int ce, oe, we; bool writable; long access;
        switch (device.PartIdentifier)
        {
            case "28C256":
            case "62256":
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2, 26, 1 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27;
                writable = device.PartIdentifier == "62256";
                access = writable ? 55_000 : 250_000;
                break;
            case "CY7C199":
                // 28-pin 32K x 8 SRAM, identical JEDEC pinout to the 62256;
                // the -15 grade is a 15 ns part. Always writable.
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2, 26, 1 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27; writable = true; access = 15_000;
                break;
            case "28C128":
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2, 26 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27; writable = false; access = 250_000;
                break;
            case "28C64":
                addr = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 25, 24, 21, 23, 2 };
                data = new[] { 11, 12, 13, 15, 16, 17, 18, 19 };
                ce = 20; oe = 22; we = 27; writable = false; access = 250_000;
                break;
            case "28C16":
                addr = new[] { 8, 7, 6, 5, 4, 3, 2, 1, 23, 22, 19 };
                data = new[] { 9, 10, 11, 13, 14, 15, 16, 17 };
                ce = 18; oe = 20; we = 21; writable = false; access = 250_000;
                break;
            case "6116":
                // 24-pin 2K x 8 SRAM, same JEDEC pinout as the 28C16; the
                // 6116P-70 is the 70 ns grade.
                addr = new[] { 8, 7, 6, 5, 4, 3, 2, 1, 23, 22, 19 };
                data = new[] { 9, 10, 11, 13, 14, 15, 16, 17 };
                ce = 18; oe = 20; we = 21; writable = true; access = 70_000;
                break;
            case "2114":
                // 18-pin 1K x 4 SRAM. Nibble-wide, and it has NO output-enable
                // pin (oe = -1): outputs drive whenever /CS is LOW and /WE HIGH.
                // 200 ns is a representative grade; it only sets the sim delay.
                addr = new[] { 5, 6, 7, 4, 3, 2, 1, 17, 16, 15 }; // A0..A9
                data = new[] { 14, 13, 12, 11 };                  // I/O1..I/O4
                ce = 8; oe = -1; we = 10; writable = true; access = 200_000;
                break;
            default:
                return null;
        }

        // Honour an explicit per-part "Propagation Delay (ns)" from the property
        // grid; fall back to the per-part default speed grade set above when it
        // is unset. Nanoseconds -> picoseconds (the engine's tick unit).
        if (device.PropagationDelayNs is int ns && ns > 0)
            access = ns * 1000L;

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
        int[] pins = new int[addr.Length + data.Length + (hasOe ? 3 : 2)];
        Array.Copy(addr, 0, pins, 0, addr.Length);
        Array.Copy(data, 0, pins, addr.Length, data.Length);
        int p = addr.Length + data.Length;
        pins[p++] = ce;
        if (hasOe) pins[p++] = oe;
        pins[p] = we;

        return new ParallelMemory(
            addrNets, dataNets, ceNet, oeNet, weNet,
            writable, contents, access, pins,
            label: device.PartIdentifier, logger: logger);
    }

    /// <summary>
    /// Builds a combinational GAL model from the fuse map carried in
    /// <c>device.Program</c> (JEDEC text, the same field the EEPROM uses for
    /// Intel HEX). An empty or malformed map leaves the array blank and is
    /// logged, rather than failing the build.
    /// </summary>
    private IChip? TryCreateGal(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        GalDevice? gd = GalDevice.ForPartNumber(device.PartIdentifier);
        if (gd is null) return null;

        bool[] fuses = new bool[gd.FuseCount];
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
        // grid; fall back to the model's nominal grade when it is unset.
        // Nanoseconds -> picoseconds.
        long delayPs = device.PropagationDelayNs is int ns && ns > 0
            ? ns * 1000L
            : Gal.PropagationDelayPs;

        return new Gal(gd, fuses, pinToNet, delayPs);
    }

    private static IChip? TryCreateHc157(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Required pins: 1 S, 15 /E,
        // 2 1I0, 3 1I1, 4 1Y,
        // 5 2I0, 6 2I1, 7 2Y,
        // 11 3I0, 10 3I1, 9 3Y,
        // 14 4I0, 13 4I1, 12 4Y.
        // Pin 8 GND and pin 16 VCC are consumed by the build pipeline and
        // aren't wired through the chip model.
        int[] needed = { 1, 15, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        return new Hc157(
            s: Get(1), enN: Get(15),
            i1_0: Get(2), i1_1: Get(3), y1: Get(4),
            i2_0: Get(5), i2_1: Get(6), y2: Get(7),
            i3_0: Get(11), i3_1: Get(10), y3: Get(9),
            i4_0: Get(14), i4_1: Get(13), y4: Get(12),
            delayPs: TtlTiming.ResolvePs(device));
    }

    private IChip? TryCreateHc257(BuildDevice device, IReadOnlyDictionary<int, Net> pinToNet)
    {
        // Same pinout as the '157 except pin 15 is /OE (tri-state) not /E.
        // 1 S, 15 /OE, 2 1I0, 3 1I1, 4 1Y, 5 2I0, 6 2I1, 7 2Y,
        // 11 3I0, 10 3I1, 9 3Y, 14 4I0, 13 4I1, 12 4Y.
        // Pin 8 GND and pin 16 VCC are consumed by the build pipeline.
        int[] needed = { 1, 15, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14 };
        foreach (int p in needed)
            if (!pinToNet.TryGetValue(p, out Net? n) || n is null) return null;

        Net Get(int pin) => pinToNet[pin];

        return new Hc257(
            s: Get(1), oeN: Get(15),
            i1_0: Get(2), i1_1: Get(3), y1: Get(4),
            i2_0: Get(5), i2_1: Get(6), y2: Get(7),
            i3_0: Get(11), i3_1: Get(10), y3: Get(9),
            i4_0: Get(14), i4_1: Get(13), y4: Get(12),
            delayPs: TtlTiming.ResolvePs(device));
    }





    // The three octal bus buffers share a 20-pin layout: signal pins
    // 1..9 and 11..19, with 10/GND and 20/VCC excluded. None has an
    // optional cascade pin, so all 18 signal pins are required.
    private static readonly int[] OctalBufferPins =
        { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 19 };





    private static bool IsMemoryPart(string id) =>
        id is "28C256" or "28C128" or "28C64" or "28C16" or "62256"
           or "CY7C199" or "6116" or "2114";






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