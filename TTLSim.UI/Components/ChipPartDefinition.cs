using System;

namespace TTLSim.UI.Components;

/// <summary>
/// A box-shaped IC drawn as a rectangle with named pins. Used for parts
/// where the gate-style symbols don't apply: memory chips, microcontrollers,
/// peripherals, and so on. Pins on left and right at positions driven by
/// pin number (DIP-style mirror layout).
/// </summary>
public sealed record ChipPartDefinition(
    string PartNumber,
    int PinCount,
    int PowerPin,
    int GroundPin,
    int BodyWidth,                // body width in grid cells (typical 8 for DIP chips)
    ChipPin[] Pins,
    bool IsSeries74 = false,      // true if PartNumber is a bare 74-series id (gets "74" + family prefix at render time)
    TtlFamily DefaultFamily = TtlFamily.HC,
    // Optional cosmetic decorator: draws extra detail (e.g. miniature gate
    // glyphs) inside the box AFTER the body and pin stubs are drawn, in the
    // same rotated coordinate space. Null leaves the plain box unchanged.
    Action<ChipDecoration>? Decorate = null,
    // Optional per-pin name-visibility filter. Returns true to draw the pin's
    // name inside the body, false to suppress it (the stub + dot still draw,
    // so the pin remains wireable). Null = draw every pin name (default,
    // unchanged behaviour for all existing chips). Gate boxes set this to
    // show only VCC/GND when a Decorate helper makes the I/O pins self-evident.
    Func<ChipPin, bool>? ShowPinName = null)
    : PartDefinition(PartNumber, "U")
{
    // ------------------------------------------------------------------ catalogue

    /// <summary>
    /// Build the pin map for a standard 28-pin DIP parallel memory
    /// (28C-series EEPROM or 62-series SRAM). The pinout is identical
    /// across the family except for the two high-order address pins,
    /// which appear on pin 1 (A14) and pin 26 (A13) when present.
    ///
    /// Address-line count drives the variant:
    ///   15 -> 28C256 / 62256: pin 1 = A14, pin 26 = A13
    ///   14 -> 28C128:         pin 1 = NC,  pin 26 = A13
    ///   13 -> 28C64:          pin 1 = NC,  pin 26 = NC
    ///
    /// The 24-pin 28C16 predates this family and has a completely different
    /// pin layout; it doesn't ride this helper -- see Ic28C16 below.
    ///
    /// I/O pins are bidirectional (output on read, input on write) and are
    /// declared as outputs: a pin that can drive must never be tied to a rail,
    /// so the floating-INPUT diagnostic (which advises exactly that) doesn't
    /// apply. An unused data line is a harmless open output, not a fault.
    /// </summary>
    private static ChipPin[] Build28PinMemoryPinout(int addressLines)
    {
        if (addressLines is < 13 or > 15)
            throw new ArgumentOutOfRangeException(nameof(addressLines),
                "Standard 28-pin memory parts have 13, 14, or 15 address lines.");

        string pin1 = addressLines >= 15 ? "A14" : "NC";
        string pin26 = addressLines >= 14 ? "A13" : "NC";

        return new[]
        {
            new ChipPin(pin1,    1),  new ChipPin("VCC",  28),
            new ChipPin("A12",   2),  new ChipPin("/WE",  27),
            new ChipPin("A7",    3),  new ChipPin(pin26,  26),
            new ChipPin("A6",    4),  new ChipPin("A8",   25),
            new ChipPin("A5",    5),  new ChipPin("A9",   24),
            new ChipPin("A4",    6),  new ChipPin("A11",  23),
            new ChipPin("A3",    7),  new ChipPin("/OE",  22),
            new ChipPin("A2",    8),  new ChipPin("A10",  21),
            new ChipPin("A1",    9),  new ChipPin("/CE",  20),
            new ChipPin("A0",   10),  new ChipPin("I/O7", 19, Out),
            new ChipPin("I/O0", 11, Out),  new ChipPin("I/O6", 18, Out),
            new ChipPin("I/O1", 12, Out),  new ChipPin("I/O5", 17, Out),
            new ChipPin("I/O2", 13, Out),  new ChipPin("I/O4", 16, Out),
            new ChipPin("GND",  14),  new ChipPin("I/O3", 15, Out),
        };
    }

    /// <summary>32K x 8 Parallel EEPROM, 28-pin DIP.</summary>
    public static readonly ChipPartDefinition Ic28C256 = new(
        PartNumber: "28C256", PinCount: 28, PowerPin: 28, GroundPin: 14,
        BodyWidth: 12, Pins: Build28PinMemoryPinout(addressLines: 15));

    /// <summary>16K x 8 Parallel EEPROM, 28-pin DIP. Pin-compatible with the
    /// 28C256 except pin 1 is NC (no A14).</summary>
    public static readonly ChipPartDefinition Ic28C128 = new(
        PartNumber: "28C128", PinCount: 28, PowerPin: 28, GroundPin: 14,
        BodyWidth: 12, Pins: Build28PinMemoryPinout(addressLines: 14));

    /// <summary>8K x 8 Parallel EEPROM, 28-pin DIP. Pin-compatible with the
    /// 28C256 except pins 1 and 26 are NC (no A14, no A13).</summary>
    public static readonly ChipPartDefinition Ic28C64 = new(
        PartNumber: "28C64", PinCount: 28, PowerPin: 28, GroundPin: 14,
        BodyWidth: 12, Pins: Build28PinMemoryPinout(addressLines: 13));

    /// <summary>2K x 8 Parallel EEPROM, 24-pin DIP. This is the chip used in
    /// Ben Eater's 8-bit breadboard CPU output module and control logic.
    /// Pin layout is unrelated to the 28-pin 28C-family (the '16 predates
    /// the pin-compatible 28C64/128/256 standard), so the pinout is
    /// hand-rolled rather than going through Build28PinMemoryPinout.</summary>
    public static readonly ChipPartDefinition Ic28C16 = new(
        PartNumber: "28C16", PinCount: 24, PowerPin: 24, GroundPin: 12,
        BodyWidth: 12,
        Pins: new ChipPin[]
        {
            new("A7",    1),              new("VCC",  24),
            new("A6",    2),              new("A8",   23),
            new("A5",    3),              new("A9",   22),
            new("A4",    4),              new("/WE",  21),
            new("A3",    5),              new("/OE",  20),
            new("A2",    6),              new("A10",  19),
            new("A1",    7),              new("/CE",  18),
            new("A0",    8),              new("I/O7", 17, Out),
            new("I/O0",  9, Out),         new("I/O6", 16, Out),
            new("I/O1", 10, Out),         new("I/O5", 15, Out),
            new("I/O2", 11, Out),         new("I/O4", 14, Out),
            new("GND",  12),              new("I/O3", 13, Out),
        });

    /// <summary>32K x 8 Static RAM, 28-pin DIP. Pin-compatible with the 28C256.</summary>
    public static readonly ChipPartDefinition Ic62256 = new(
        PartNumber: "62256", PinCount: 28, PowerPin: 28, GroundPin: 14,
        BodyWidth: 12, Pins: Build28PinMemoryPinout(addressLines: 15));

    /// <summary>2114 1K x 4 Static RAM, 18-pin DIP. Classic late-1970s NMOS SRAM
    /// (Intel, AMD, Mostek, NEC, etc.); pair two for a byte-wide 1Kx8. Single
    /// +5V supply with the unusual VCC-on-18 / GND-on-9 placement (not the
    /// corners). 10 address lines, 4 bidirectional data lines, async; one
    /// active-low chip select and one active-low write enable (no separate
    /// /OE -- outputs drive whenever /CS is LOW and /WE is HIGH). Bidirectional
    /// I/O pins are declared as outputs (they drive on read), matching the
    /// 28C / 62256 family: a driving pin must never be tied to a rail, so the
    /// floating-input diagnostic does not apply to them.</summary>
    public static readonly ChipPartDefinition Ic2114 = new(
        PartNumber: "2114", PinCount: 18, PowerPin: 18, GroundPin: 9,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
        new("A6",   1),              new("VCC",  18),
        new("A5",   2),              new("A7",   17),
        new("A4",   3),              new("A8",   16),
        new("A3",   4),              new("A9",   15),
        new("A0",   5),              new("I/O1", 14, Out),
        new("A1",   6),              new("I/O2", 13, Out),
        new("A2",   7),              new("I/O3", 12, Out),
        new("/CS",  8),              new("I/O4", 11, Out),
        new("GND",  9),              new("/WE",  10),
        });

    /// <summary>6116 2K x 8 Static RAM, 24-pin DIP (e.g. HM6116, the 6116P-70 is
    /// the 70 ns plastic-DIP grade). Single +5 V supply, async, 11 address lines,
    /// 8 bidirectional data lines, with separate active-low /CS, /OE and /WE. The
    /// pinout is the standard JEDEC 24-pin 2K x 8 layout -- identical to the
    /// 28C16 already in this catalogue -- so it rides the same ParallelMemory
    /// engine as the byte-wide EEPROM/SRAM family, just writable with a 70 ns
    /// access time. Bidirectional I/O pins are declared as outputs (they drive on
    /// read), so the floating-input diagnostic does not apply to them.</summary>
    public static readonly ChipPartDefinition Ic6116 = new(
        PartNumber: "6116", PinCount: 24, PowerPin: 24, GroundPin: 12,
        BodyWidth: 12,
        Pins: new ChipPin[]
        {
            new("A7",    1),              new("VCC",  24),
            new("A6",    2),              new("A8",   23),
            new("A5",    3),              new("A9",   22),
            new("A4",    4),              new("/WE",  21),
            new("A3",    5),              new("/OE",  20),
            new("A2",    6),              new("A10",  19),
            new("A1",    7),              new("/CS",  18),
            new("A0",    8),              new("I/O7", 17, Out),
            new("I/O0",  9, Out),         new("I/O6", 16, Out),
            new("I/O1", 10, Out),         new("I/O5", 15, Out),
            new("I/O2", 11, Out),         new("I/O4", 14, Out),
            new("GND",  12),              new("I/O3", 13, Out),
        });

    /// <summary>NE555 single precision timer, 8-pin DIP. Active-low pins
    /// (RESET, TRIG) use a leading '/' which the renderer converts to a
    /// bar over the leading letters.</summary>
    public static readonly ChipPartDefinition IcNe555 = new(
        PartNumber: "NE555", PinCount: 8, PowerPin: 8, GroundPin: 1,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("GND",   1),              new("VCC",   8),
            new("/TRIG", 2),              new("DISCH", 7),
            new("OUT",   3, Out),         new("THRES", 6),
            new("/RESET",4),              new("CTRL",  5),
        });

    /// <summary>NE556 dual precision timer, 14-pin DIP. Two independent 555
    /// timers sharing VCC and GND. Pin pairs: 1/13 DISCH, 2/12 THRES,
    /// 3/11 CTRL, 4/10 RESET, 5/9 OUT, 6/8 TRIG. Pin 7 GND, pin 14 VCC.</summary>
    public static readonly ChipPartDefinition IcNe556 = new(
        PartNumber: "NE556", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("DISCH1",  1),              new("VCC",     14),
            new("THRES1",  2),              new("DISCH2",  13),
            new("CTRL1",   3),              new("THRES2",  12),
            new("/RESET1", 4),              new("CTRL2",   11),
            new("OUT1",    5, Out),         new("/RESET2", 10),
            new("/TRIG1",  6),              new("OUT2",     9, Out),
            new("GND",     7),              new("/TRIG2",   8),
        });

    /// <summary>74HC74 dual positive-edge-triggered D flip-flop with
    /// asynchronous preset and clear, 14-pin DIP. PRE and CLR are
    /// active-low. The two halves are labelled A and B; once a dedicated
    /// flip-flop unit class lands, each half becomes its own Unit and the
    /// prefix is replaced by the standard UnitLetter mechanism.</summary>
    public static readonly ChipPartDefinition Ic7474 = new(
        PartNumber: "74", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/ACLR", 1),              new("VCC",   14),
            new("AD",    2),              new("/BCLR", 13),
            new("ACLK",  3),              new("BD",    12),
            new("/APRE", 4),              new("BCLK",  11),
            new("AQ",    5, Out),         new("/BPRE", 10),
            new("/AQ",   6, Out),         new("BQ",     9, Out),
            new("GND",   7),              new("/BQ",    8, Out),
        },
        IsSeries74: true
        );

    /// <summary>74LS107 dual JK flip-flop with asynchronous clear, 14-pin DIP.
    /// Negative-edge clock; /CLR (active low) overrides the clock to force
    /// Q LOW. The two halves are labelled 1 and 2 in datasheet convention;
    /// like the 7474, this part stays as a named-pin box until a dedicated
    /// JK flip-flop unit kind and class land. The 74107 is the standard
    /// substitute for the 74LS76 used in Ben Eater's clock module videos --
    /// functionally equivalent but the pinout differs.</summary>
    public static readonly ChipPartDefinition Ic74107 = new(
        PartNumber: "107", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1J",    1),              new("VCC",   14),
            new("/1Q",   2, Out),         new("/1CLR", 13),
            new("1Q",    3, Out),         new("1CLK",  12),
            new("1K",    4),              new("2K",    11),
            new("2Q",    5, Out),         new("/2CLR", 10),
            new("/2Q",   6, Out),         new("2CLK",   9),
            new("GND",   7),              new("2J",     8),
        },
        IsSeries74: true
        );

    /// <summary>74HC393 dual 4-bit binary ripple counter, 14-pin DIP.
    /// Each half has its own clock (active-low edge) and async clear
    /// (active high -- unusual for the family). The two halves are
    /// labelled A and B; once a dedicated counter unit class lands, each
    /// half becomes its own Unit and the prefix is replaced by the
    /// standard UnitLetter mechanism.</summary>
    public static readonly ChipPartDefinition Ic74393 = new(
        PartNumber: "393", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("ACLK", 1),              new("VCC",  14),
            new("ACLR", 2),              new("BCLK", 13),
            new("AQ0",  3, Out),         new("BCLR", 12),
            new("AQ1",  4, Out),         new("BQ0",  11, Out),
            new("AQ2",  5, Out),         new("BQ1",  10, Out),
            new("AQ3",  6, Out),         new("BQ2",   9, Out),
            new("GND",  7),              new("BQ3",   8, Out),
        },
        IsSeries74: true
        );

    /// <summary>74xx00 quad 2-input NAND, 14-pin DIP. Single-part box
    /// (gate n: inputs nA/nB, output nY). Simulated as four independent
    /// HC00 NAND gates by ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7400 = new(
        PartNumber: "00", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1B",  2),               new("4B",  13),
            new("1Y",  3, Out),          new("4A",  12),
            new("2A",  4),               new("4Y",  11, Out),
            new("2B",  5),               new("3B",  10),
            new("2Y",  6, Out),          new("3A",   9),
            new("GND", 7),               new("3Y",   8, Out),
        },
        IsSeries74: true,
        // Show only the supply pins by name; the four NAND glyphs make the
        // signal pins self-evident.
        ShowPinName: p => p.Name is "VCC" or "GND",
        // Draw four miniature 2-input NAND glyphs, one per gate, wired to the
        // pin groups the simulator uses (see ChipFactory.CreateGateChip "00"):
        //   gate a: 1,2 -> 3     gate b: 4,5 -> 6
        //   gate c: 9,10 -> 8    gate d: 12,13 -> 11
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.NandGate,
            (new[] { 1, 2 }, 3), (new[] { 4, 5 }, 6),
            (new[] { 9, 10 }, 8), (new[] { 12, 13 }, 11)));

    /// <summary>74xx02 quad 2-input NOR, 14-pin DIP. Single-part box
    /// (gate n: inputs nA/nB, output nY). Note the output-first pinout:
    /// pin 1 is gate-a's output. Simulated as four HC02 NOR gates by
    /// ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7402 = new(
        PartNumber: "02", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1Y",  1, Out),          new("VCC", 14),
            new("1A",  2),               new("4Y",  13, Out),
            new("1B",  3),               new("4B",  12),
            new("2Y",  4, Out),          new("4A",  11),
            new("2A",  5),               new("3Y",  10, Out),
            new("2B",  6),               new("3B",   9),
            new("GND", 7),               new("3A",   8),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // ChipFactory.CreateGateChip "02":
        //   gate a: 2,3 -> 1     gate b: 5,6 -> 4
        //   gate c: 8,9 -> 10    gate d: 11,12 -> 13
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.NorGate,
            (new[] { 2, 3 }, 1), (new[] { 5, 6 }, 4),
            (new[] { 8, 9 }, 10), (new[] { 11, 12 }, 13)));

    /// <summary>74xx08 quad 2-input AND, 14-pin DIP. Single-part box
    /// (gate n: inputs nA/nB, output nY). Simulated as four HC08 AND gates
    /// by ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7408 = new(
        PartNumber: "08", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1B",  2),               new("4B",  13),
            new("1Y",  3, Out),          new("4A",  12),
            new("2A",  4),               new("4Y",  11, Out),
            new("2B",  5),               new("3B",  10),
            new("2Y",  6, Out),          new("3A",   9),
            new("GND", 7),               new("3Y",   8, Out),
        },
        IsSeries74: true,
        // Show only the supply pins by name; the four AND glyphs make the
        // signal pins self-evident.
        ShowPinName: p => p.Name is "VCC" or "GND",
        // Same gate grouping as the 7400 (see ChipFactory.CreateGateChip "08"):
        //   gate a: 1,2 -> 3     gate b: 4,5 -> 6
        //   gate c: 9,10 -> 8    gate d: 12,13 -> 11
        // The only difference from the 7400 is AndGate vs NandGate -- no
        // bubble. Body geometry and lead routing are shared.
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.AndGate,
            (new[] { 1, 2 }, 3), (new[] { 4, 5 }, 6),
            (new[] { 9, 10 }, 8), (new[] { 12, 13 }, 11)));

    /// <summary>74xx04 hex inverter, 14-pin DIP. Single-part box (inverter
    /// n: input nA, output nY). Simulated as six HC04 inverters by
    /// ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7404 = new(
        PartNumber: "04", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1Y",  2, Out),          new("6A",  13),
            new("2A",  3),               new("6Y",  12, Out),
            new("2Y",  4, Out),          new("5A",  11),
            new("3A",  5),               new("5Y",  10, Out),
            new("3Y",  6, Out),          new("4A",   9),
            new("GND", 7),               new("4Y",   8, Out),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // ChipFactory.CreateGateChip "04":
        //   1->2, 3->4, 5->6 (left)   9->8, 11->10, 13->12 (right)
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.NotGate,
            (new[] { 1 }, 2), (new[] { 3 }, 4), (new[] { 5 }, 6),
            (new[] { 9 }, 8), (new[] { 11 }, 10), (new[] { 13 }, 12)));

    /// <summary>74xx14 hex Schmitt-trigger inverter, 14-pin DIP. Same pinout
    /// as '04. Single-part box; simulated as six HC14 inverters by
    /// ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7414 = new(
        PartNumber: "14", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1Y",  2, Out),          new("6A",  13),
            new("2A",  3),               new("6Y",  12, Out),
            new("2Y",  4, Out),          new("5A",  11),
            new("3A",  5),               new("5Y",  10, Out),
            new("3Y",  6, Out),          new("4A",   9),
            new("GND", 7),               new("4Y",   8, Out),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // ChipFactory.CreateGateChip "14": same grouping as '04.
        // Uses the SchmittInverterGate stub helper -- renders as a plain
        // inverter until the hysteresis glyph is added.
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.SchmittInverterGate,
            (new[] { 1 }, 2), (new[] { 3 }, 4), (new[] { 5 }, 6),
            (new[] { 9 }, 8), (new[] { 11 }, 10), (new[] { 13 }, 12)));

    /// <summary>74xx10 triple 3-input NAND, 14-pin DIP. Single-part box
    /// (gate n: inputs nA/nB/nC, output nY). Note gate 1's third input is
    /// on pin 13 and its output on pin 12. Simulated as three HC10 gates by
    /// ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7410 = new(
        PartNumber: "10", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1B",  2),               new("1C",  13),
            new("2A",  3),               new("1Y",  12, Out),
            new("2B",  4),               new("3C",  11),
            new("2C",  5),               new("3B",  10),
            new("2Y",  6, Out),          new("3A",   9),
            new("GND", 7),               new("3Y",   8, Out),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // ChipFactory.CreateGateChip "10":
        //   gate a: 1,2,13 -> 12     gate b: 3,4,5 -> 6     gate c: 9,10,11 -> 8
        // Gate a has a CROSS-SIDE input (pin 13 on the right while the cluster
        // is on the left). The current DrawGateInternal routes leads to the
        // cluster's stub edge only, so pin 13's lead will be visually wrong
        // (truncated to the left stub edge) until cross-side lead routing is
        // added. Other two gates are clean same-side groups.
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.NandGate,
            (new[] { 1, 2, 13 }, 12),
            (new[] { 3, 4, 5 }, 6),
            (new[] { 9, 10, 11 }, 8)));

    /// <summary>74xx20 dual 4-input NAND, 14-pin DIP. Pins 3 and 11 are NC.
    /// Single-part box (gate n: inputs nA/nB/nC/nD, output nY). Simulated as
    /// two HC20 gates by ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7420 = new(
        PartNumber: "20", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1B",  2),               new("2D",  13),
            new("NC",  3),               new("2C",  12),
            new("1C",  4),               new("NC",  11),
            new("1D",  5),               new("2B",  10),
            new("1Y",  6, Out),          new("2A",   9),
            new("GND", 7),               new("2Y",   8, Out),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // Two 4-input NAND gates; the unused pin 3 / pin 11 get no lead.
        //   gate a: 1,2,4,5 -> 6     gate b: 9,10,12,13 -> 8
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.NandGate,
            (new[] { 1, 2, 4, 5 }, 6), (new[] { 9, 10, 12, 13 }, 8)));

    /// <summary>74xx30 single 8-input NAND, 14-pin DIP. Pins 9, 10, 13 are
    /// NC. Single-part box (inputs 1A..1H, output 1Y). Simulated as one HC30
    /// gate by ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7430 = new(
        PartNumber: "30", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1B",  2),               new("NC",  13),
            new("1C",  3),               new("1H",  12),
            new("1D",  4),               new("1G",  11),
            new("1E",  5),               new("NC",  10),
            new("1F",  6),               new("NC",   9),
            new("GND", 7),               new("1Y",   8, Out),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // ChipFactory.CreateGateChip "30":
        //   single 8-input NAND: 1,2,3,4,5,6,11,12 -> 8
        // Six inputs are on the left, two (11,12) on the right -- CROSS-SIDE.
        // The two right-side leads will render truncated until cross-side
        // routing is added.
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.NandGate,
            (new[] { 1, 2, 3, 4, 5, 6, 11, 12 }, 8)));

    /// <summary>74xx32 quad 2-input OR, 14-pin DIP. Single-part box (gate n:
    /// inputs nA/nB, output nY). Simulated as four HC32 OR gates by
    /// ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7432 = new(
        PartNumber: "32", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1B",  2),               new("4B",  13),
            new("1Y",  3, Out),          new("4A",  12),
            new("2A",  4),               new("4Y",  11, Out),
            new("2B",  5),               new("3B",  10),
            new("2Y",  6, Out),          new("3A",   9),
            new("GND", 7),               new("3Y",   8, Out),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // Same grouping as the 7400; only the glyph differs (OR vs NAND).
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.OrGate,
            (new[] { 1, 2 }, 3), (new[] { 4, 5 }, 6),
            (new[] { 9, 10 }, 8), (new[] { 12, 13 }, 11)));

    /// <summary>74xx86 quad 2-input XOR, 14-pin DIP. Single-part box (gate n:
    /// inputs nA/nB, output nY). Simulated as four HC86 XOR gates by
    /// ChipFactory; exports via the standard DIP-14 path.</summary>
    public static readonly ChipPartDefinition Ic7486 = new(
        PartNumber: "86", PinCount: 14, PowerPin: 14, GroundPin: 7,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1A",  1),               new("VCC", 14),
            new("1B",  2),               new("4B",  13),
            new("1Y",  3, Out),          new("4A",  12),
            new("2A",  4),               new("4Y",  11, Out),
            new("2B",  5),               new("3B",  10),
            new("2Y",  6, Out),          new("3A",   9),
            new("GND", 7),               new("3Y",   8, Out),
        },
        IsSeries74: true,
        ShowPinName: p => p.Name is "VCC" or "GND",
        // Same grouping as the 7400; only the glyph differs (XOR vs NAND).
        Decorate: d => ChipDecoration.Decor.Array(d, ChipDecoration.Decor.XorGate,
            (new[] { 1, 2 }, 3), (new[] { 4, 5 }, 6),
            (new[] { 9, 10 }, 8), (new[] { 12, 13 }, 11)));

    /// <summary>74LS390 dual 4-bit decade counter, 16-pin DIP. Each half
    /// is partitioned into an independently-clocked ÷2 section (CKA → QA)
    /// and ÷5 section (CKB → QB,QC,QD), sharing one async master reset MR
    /// (active HIGH -- unusual for the family, like the '393). To get a
    /// BCD ÷10, wire QA to CKB externally; for a 50%-duty ÷10, wire the
    /// input to CKB, QD to CKA, and take QA as the output. Used in
    /// counter/timer chains where decimal scaling is wanted.</summary>
    public static readonly ChipPartDefinition Ic74390 = new(
        PartNumber: "390", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("1CKA", 1),              new("VCC",  16),
            new("1CLR", 2),              new("2CKA", 15),
            new("1QA",  3, Out),         new("2CLR", 14),
            new("1CKB", 4),              new("2QA",  13, Out),
            new("1QB",  5, Out),         new("2CKB", 12),
            new("1QC",  6, Out),         new("2QB",  11, Out),
            new("1QD",  7, Out),         new("2QC",  10, Out),
            new("GND",  8),              new("2QD",   9, Out),
        },
        IsSeries74: true,
        DefaultFamily: TtlFamily.LS
        );

    // ---- Registers -----------------------------------------------------

    /// <summary>74LS173 4-bit D-type register with 3-state outputs,
    /// 16-pin DIP. Edge-triggered load gated by data-enable inputs G1, G2
    /// (both must be LOW to load on the next clock edge -- otherwise the
    /// register holds). Output-control inputs M, N (both must be LOW for
    /// outputs to drive -- otherwise high-Z). CLR is async **active HIGH**
    /// on this part (unusual for the family, hence the bare name). The
    /// workhorse register in Ben Eater's 8-bit CPU -- used for A, B, IR,
    /// MAR, and the output register.</summary>
    public static readonly ChipPartDefinition Ic74173 = new(
        PartNumber: "173", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("M",   1),              new("VCC", 16),
            new("N",   2),              new("CLR", 15),
            new("1Q",  3, Out),         new("1D",  14),
            new("2Q",  4, Out),         new("2D",  13),
            new("3Q",  5, Out),         new("3D",  12),
            new("4Q",  6, Out),         new("4D",  11),
            new("CLK", 7),              new("G2",  10),
            new("GND", 8),              new("G1",   9),
        },
        IsSeries74: true
        );

    /// <summary>74LS175 quad D flip-flop with common clock and async clear,
    /// 16-pin DIP. Positive-edge clock; /MR (master reset) is async active-low,
    /// forces all Q LOW. Each flip-flop exposes both Q and complementary Q.
    /// Effectively a 4-bit register without 3-state outputs -- simpler than
    /// the '173 (no enables, always driving) and half the bits.</summary>
    public static readonly ChipPartDefinition Ic74175 = new(
        PartNumber: "175", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
        new("/MR", 1),              new("VCC", 16),
        new("Q0",  2, Out),         new("Q3",  15, Out),
        new("/Q0", 3, Out),         new("/Q3", 14, Out),
        new("D0",  4),              new("D3",  13),
        new("D1",  5),              new("D2",  12),
        new("/Q1", 6, Out),         new("/Q2", 11, Out),
        new("Q1",  7, Out),         new("Q2",  10, Out),
        new("GND", 8),              new("CP",   9),
        },
        IsSeries74: true
        );

    /// <summary>74HC574 octal D flip-flop with 3-state outputs, 20-pin DIP.
    /// Positive-edge clock; /OE high puts Q0..Q7 in high-Z. Common as a bus
    /// driver / output register in CPU designs.</summary>
    public static readonly ChipPartDefinition Ic74574 = new(
        PartNumber: "574", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/OE", 1),              new("VCC", 20),
            new("D0",  2),              new("Q7",  19, Out),
            new("D1",  3),              new("Q6",  18, Out),
            new("D2",  4),              new("Q5",  17, Out),
            new("D3",  5),              new("Q4",  16, Out),
            new("D4",  6),              new("Q3",  15, Out),
            new("D5",  7),              new("Q2",  14, Out),
            new("D6",  8),              new("Q1",  13, Out),
            new("D7",  9),              new("Q0",  12, Out),
            new("GND", 10),             new("CLK", 11),
        },
        IsSeries74: true
        );

    /// <summary>74HC273 octal D flip-flop with async clear, no tri-state,
    /// 20-pin DIP. Outputs always driven (no /OE); /CLR forces all Qs low.
    /// Common as a holding register where bus contention isn't a concern.</summary>
    public static readonly ChipPartDefinition Ic74273 = new(
        PartNumber: "273", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/CLR", 1),              new("VCC", 20),
            new("Q0",   2, Out),         new("Q7",  19, Out),
            new("D0",   3),              new("D7",  18),
            new("D1",   4),              new("D6",  17),
            new("Q1",   5, Out),         new("Q6",  16, Out),
            new("Q2",   6, Out),         new("Q5",  15, Out),
            new("D2",   7),              new("D5",  14),
            new("D3",   8),              new("D4",  13),
            new("Q3",   9, Out),         new("Q4",  12, Out),
            new("GND", 10),              new("CLK", 11),
        },
        IsSeries74: true
        );

    /// <summary>74HC377 octal D flip-flop with clock enable, no tri-state,
    /// 20-pin DIP. /EN low gates the clock so the register only updates on
    /// selected cycles. No async clear.</summary>
    public static readonly ChipPartDefinition Ic74377 = new(
        PartNumber: "377", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/EN", 1),              new("VCC", 20),
            new("Q0",  2, Out),         new("Q7",  19, Out),
            new("D0",  3),              new("D7",  18),
            new("D1",  4),              new("D6",  17),
            new("Q1",  5, Out),         new("Q6",  16, Out),
            new("Q2",  6, Out),         new("Q5",  15, Out),
            new("D2",  7),              new("D5",  14),
            new("D3",  8),              new("D4",  13),
            new("Q3",  9, Out),         new("Q4",  12, Out),
            new("GND", 10),             new("CLK", 11),
        },
        IsSeries74: true
        );

    /// <summary>74LS373 octal D-type transparent latch with 3-state outputs,
    /// 20-pin DIP. LE HIGH makes outputs follow D inputs transparently; LE
    /// LOW latches the current values. /OE HIGH puts Q0..Q7 in high-Z.
    /// Common as an address latch on multiplexed-bus microprocessors (e.g.
    /// 8051, 8085) -- LE driven by ALE captures the low byte of the address.
    /// Functionally similar to the '574 except level-triggered rather than
    /// edge-triggered, and pins interleave D/Q rather than grouping them.</summary>
    public static readonly ChipPartDefinition Ic74373 = new(
        PartNumber: "373", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
        new("/OE", 1),              new("VCC", 20),
        new("Q0",  2, Out),         new("Q7",  19, Out),
        new("D0",  3),              new("D7",  18),
        new("D1",  4),              new("D6",  17),
        new("Q1",  5, Out),         new("Q6",  16, Out),
        new("Q2",  6, Out),         new("Q5",  15, Out),
        new("D2",  7),              new("D5",  14),
        new("D3",  8),              new("D4",  13),
        new("Q3",  9, Out),         new("Q4",  12, Out),
        new("GND", 10),             new("LE",  11),
        },
        IsSeries74: true
        );

    // ---- Counters ------------------------------------------------------

    /// <summary>74HC161 presettable synchronous 4-bit binary counter with
    /// asynchronous clear, 16-pin DIP. CEP and CET both high to count; CET
    /// also gates RCO for cascading. /LD low loads D0..D3 on next clock
    /// edge. /CLR is asynchronous on the '161 (it overrides the clock).</summary>
    public static readonly ChipPartDefinition Ic74161 = new(
        PartNumber: "161", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/CLR", 1),              new("VCC", 16),
            new("CLK",  2),              new("RCO", 15, Out),
            new("D0",   3),              new("Q0",  14, Out),
            new("D1",   4),              new("Q1",  13, Out),
            new("D2",   5),              new("Q2",  12, Out),
            new("D3",   6),              new("Q3",  11, Out),
            new("CEP",  7),              new("CET", 10),
            new("GND",  8),              new("/LD",  9),
        },
        IsSeries74: true
        );

    /// <summary>74HC163 presettable synchronous 4-bit binary counter with
    /// synchronous clear, 16-pin DIP. Pin-compatible with the '161; the only
    /// difference is that /CLR is sampled on the clock edge rather than
    /// overriding it.</summary>
    public static readonly ChipPartDefinition Ic74163 = new(
        PartNumber: "163", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/CLR", 1),              new("VCC", 16),
            new("CLK",  2),              new("RCO", 15, Out),
            new("D0",   3),              new("Q0",  14, Out),
            new("D1",   4),              new("Q1",  13, Out),
            new("D2",   5),              new("Q2",  12, Out),
            new("D3",   6),              new("Q3",  11, Out),
            new("CEP",  7),              new("CET", 10),
            new("GND",  8),              new("/LD",  9),
        },
        IsSeries74: true
        );

    /// <summary>74HC193 presettable synchronous 4-bit binary up/down counter,
    /// 16-pin DIP. CLKU and CLKD are separate up- and down-clocks (the unused
    /// one must be held high). /CO and /BO are the cascading carry-out and
    /// borrow-out. /LD asynchronously loads D0..D3; CLR (active high, not
    /// low) asynchronously zeroes the count.</summary>
    public static readonly ChipPartDefinition Ic74193 = new(
        PartNumber: "193", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("D1",   1),              new("VCC", 16),
            new("Q1",   2, Out),         new("D0",  15),
            new("Q0",   3, Out),         new("CLR", 14),
            new("CLKD", 4),              new("/BO", 13, Out),
            new("CLKU", 5),              new("/CO", 12, Out),
            new("Q2",   6, Out),         new("/LD", 11),
            new("Q3",   7, Out),         new("D2",  10),
            new("GND",  8),              new("D3",   9),
        },
        IsSeries74: true
        );

    // ---- ALU / shift register / adders --------------------------------

    /// <summary>74HC181 4-bit arithmetic/logic unit, 24-pin DIP. The classic
    /// "ALU on a chip" used in countless minicomputers. S0..S3 select one
    /// of 16 functions; M chooses logic (M=H) vs arithmetic (M=L). Active-low
    /// data convention per the datasheet: /A and /B inputs, /F outputs.
    /// /Cn+4 is carry-out; X and Y are propagate / generate for cascading
    /// through a 74HC182. A=B is the open-collector zero-detect output.</summary>
    public static readonly ChipPartDefinition Ic74181 = new(
        PartNumber: "181", PinCount: 24, PowerPin: 24, GroundPin: 12,
        BodyWidth: 12,
        Pins: new ChipPin[]
        {
            new("/B0",   1),              new("VCC",   24),
            new("/A0",   2),              new("/A1",   23),
            new("S3",    3),              new("/B1",   22),
            new("S2",    4),              new("/A2",   21),
            new("S1",    5),              new("/B2",   20),
            new("S0",    6),              new("/A3",   19),
            new("Cn",    7),              new("/B3",   18),
            new("M",     8),              new("/Cn+4", 17, Out),
            new("/F0",   9, Out),         new("X",     16, Out),
            new("/F1",  10, Out),         new("Y",     15, Out),
            new("/F2",  11, Out),         new("A=B",   14, Out),
            new("GND",  12),              new("/F3",   13, Out),
        },
        IsSeries74: true
        );

    /// <summary>74HC182 carry lookahead generator, 16-pin DIP. Pairs with up
    /// to four '181s to produce full lookahead carry across 16 bits. Inputs
    /// are the propagate (/Pn) and generate (/Gn) outputs of each '181;
    /// outputs Cn+x, Cn+y, Cn+z feed each '181's Cn input. /G and /P
    /// outputs allow further levels of lookahead.</summary>
    public static readonly ChipPartDefinition Ic74182 = new(
        PartNumber: "182", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/G1", 1),              new("VCC",   16),
            new("/P1", 2),              new("/P2",   15),
            new("/G0", 3),              new("/G2",   14),
            new("/P0", 4),              new("Cn",    13),
            new("/G3", 5),              new("Cn+x",  12, Out),
            new("/P3", 6),              new("Cn+y",  11, Out),
            new("/P",  7, Out),         new("/G",    10, Out),
            new("GND", 8),              new("Cn+z",   9, Out),
        },
        IsSeries74: true
        );

    /// <summary>74LS283 4-bit binary full adder with internal fast (lookahead)
    /// carry, 16-pin DIP. Adds A1..A4 + B1..B4 + C0 to produce S1..S4 and
    /// C4. The core of Ben Eater's ALU -- two '283s give 8-bit addition;
    /// XOR gates on the B inputs plus C0=1 turn it into a subtractor (two's
    /// complement). Note: pin order is awkward (A and B inputs interleaved
    /// with sum outputs down the package) -- that's the datasheet layout,
    /// not a transcription error.</summary>
    public static readonly ChipPartDefinition Ic74283 = new(
        PartNumber: "283", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("S2", 1, Out),          new("VCC", 16),
            new("B2", 2),               new("B3",  15),
            new("A2", 3),               new("A3",  14),
            new("S1", 4, Out),          new("S3",  13, Out),
            new("A1", 5),               new("A4",  12),
            new("B1", 6),               new("B4",  11),
            new("C0", 7),               new("S4",  10, Out),
            new("GND",8),               new("C4",   9, Out),
        },
        IsSeries74: true
        );

    /// <summary>74HC299 8-bit universal shift register with 3-state I/O,
    /// 20-pin DIP. Four modes via S0/S1: hold, shift-right, shift-left,
    /// parallel load. DSR / DSL are serial data into the right and left
    /// ends; Q0 and Q7 are serial outputs for cascading. IO0..IO7 are
    /// bidirectional pins (parallel data in on load, tri-state outputs
    /// otherwise). /OE1 and /OE2 both low to drive the bus; /CLR is async.
    /// (Datasheet names IO pins "I/O0".."I/O7" but the slash is notation,
    /// not active-low, so we strip it.)</summary>
    public static readonly ChipPartDefinition Ic74299 = new(
        PartNumber: "299", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("S0",   1),              new("VCC",  20),
            new("/OE1", 2),              new("S1",   19),
            new("/OE2", 3),              new("DSL",  18),
            new("IO6",  4),              new("Q7",   17, Out),
            new("IO4",  5),              new("IO7",  16),
            new("IO2",  6),              new("IO5",  15),
            new("IO0",  7),              new("IO3",  14),
            new("Q0",   8, Out),         new("IO1",  13),
            new("/CLR", 9),              new("DSR",  12),
            new("GND", 10),              new("CLK",  11),
        },
        IsSeries74: true
        );

    /// <summary>74HC595 8-bit serial-in/parallel-out shift register feeding
    /// an 8-bit D-type storage register with 3-state outputs, 16-pin DIP.
    /// SER is the serial input, SRCLK clocks the shift register, RCLK
    /// transfers the shift register contents to the storage latch. /SRCLR
    /// async-clears the shift register; /OE puts QA..QH in high-Z. QH' is
    /// the serial output for cascading multiple '595s. Used in Ben Eater's
    /// EEPROM programmer (two '595s + an Arduino Nano) -- not the CPU
    /// proper but useful if the programmer is modelled too.</summary>
    public static readonly ChipPartDefinition Ic74595 = new(
        PartNumber: "595", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("QB",     1, Out),       new("VCC",    16),
            new("QC",     2, Out),       new("QA",     15, Out),
            new("QD",     3, Out),       new("SER",    14),
            new("QE",     4, Out),       new("/OE",    13),
            new("QF",     5, Out),       new("RCLK",   12),
            new("QG",     6, Out),       new("SRCLK",  11),
            new("QH",     7, Out),       new("/SRCLR", 10),
            new("GND",    8),            new("QH'",     9, Out),
        },
        IsSeries74: true
        );

    // ---- RAM ----------------------------------------------------------

    /// <summary>74189 64-bit RAM (16 words x 4 bits) with 3-state outputs,
    /// 16-pin DIP. /CS (active LOW) enables the chip; /WE LOW writes the
    /// data inputs to the addressed word, /WE HIGH reads (outputs driven).
    /// Outputs are the **complement** of stored data (hence the /O1../O4
    /// labels) -- Ben Eater's RAM module passes them through inverters
    /// before the bus. Pin-compatible with the 7489 (open-collector); the
    /// '189 is the tri-state variant. Two of these give 16 bytes of RAM
    /// in the Eater CPU.</summary>
    public static readonly ChipPartDefinition Ic74189 = new(
        PartNumber: "189", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("A0",  1),              new("VCC", 16),
            new("/CS", 2),              new("A1",  15),
            new("/WE", 3),              new("A2",  14),
            new("D1",  4),              new("A3",  13),
            new("/O1", 5, Out),         new("D4",  12),
            new("D2",  6),              new("/O4", 11, Out),
            new("/O2", 7, Out),         new("D3",  10),
            new("GND", 8),              new("/O3",  9, Out),
        },
        IsSeries74: true
        );

    // ---- Bus / buffers ------------------------------------------------

    /// <summary>74HC245 octal bidirectional bus transceiver, 20-pin DIP.
    /// DIR selects direction (HIGH = A drives B, LOW = B drives A); /OE
    /// disables both directions (high-Z). The classic data-bus buffer.
    /// All data pins are bidirectional — left as Input for diagnostics.</summary>
    public static readonly ChipPartDefinition Ic74245 = new(
        PartNumber: "245", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("DIR", 1),              new("VCC", 20),
            new("A0",  2),              new("/OE", 19),
            new("A1",  3),              new("B0",  18),
            new("A2",  4),              new("B1",  17),
            new("A3",  5),              new("B2",  16),
            new("A4",  6),              new("B3",  15),
            new("A5",  7),              new("B4",  14),
            new("A6",  8),              new("B5",  13),
            new("A7",  9),              new("B6",  12),
            new("GND",10),              new("B7",  11),
        },
        IsSeries74: true,
        // A0..A7 <-> B0..B7 bidirectional transceiver, drawn as back-to-back
        // diamonds centred on the chip axis. /OE (pin 19) enables the whole
        // device when LOW -> one inverting-input buffer feeding a centre bus
        // (behind the diamonds). DIR not depicted. Last channel mid-row = 18.
        Decorate: d =>
        {
            ChipDecoration.Decor.Oe245EnableBus(d, 0f, 18f);
            ChipDecoration.Decor.Array(d, ChipDecoration.Decor.HBidir,
                (new[] { 2 }, 18), (new[] { 3 }, 17), (new[] { 4 }, 16), (new[] { 5 }, 15),
                (new[] { 6 }, 14), (new[] { 7 }, 13), (new[] { 8 }, 12), (new[] { 9 }, 11));
            ChipDecoration.Decor.Oe245EnableGate(d, 19, 0f);
        }
        );

    /// <summary>74HC244 octal unidirectional buffer with two banks of four
    /// 3-state outputs, 20-pin DIP. Each bank has its own /OE (/AOE, /BOE).
    /// Pins interleave bank A and bank B down the package -- faithful to
    /// the datasheet but messy in layout; see 74HC541 for a cleaner
    /// alternative.</summary>
    public static readonly ChipPartDefinition Ic74244 = new(
        PartNumber: "244", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/AOE", 1),              new("VCC",  20),
            new("AA0",  2),              new("/BOE", 19),
            new("BY0",  3, Out),         new("AY0",  18, Out),
            new("AA1",  4),              new("BA0",  17),
            new("BY1",  5, Out),         new("AY1",  16, Out),
            new("AA2",  6),              new("BA1",  15),
            new("BY2",  7, Out),         new("AY2",  14, Out),
            new("AA3",  8),              new("BA2",  13),
            new("BY3",  9, Out),         new("AY3",  12, Out),
            new("GND", 10),              new("BA3",  11),
        },
        IsSeries74: true,
        // /AOE (pin 1) gates bank A; /BOE (pin 19) gates bank B. The enable
        // buses are drawn first (behind) so the buffer triangles paint over
        // them. Bank A (left in -> right out): AA0(2)->AY0(18) .. AA3(8)->AY3(12);
        // last bank-A mid-row = 16. Bank B (right in -> left out):
        // BA0(17)->BY0(3) .. BA3(11)->BY3(9); last bank-B mid-row = 18.
        Decorate: d =>
        {
            // Enable buses behind the buffers (they paint over the bus).
            ChipDecoration.Decor.OeEnableBus(d,
                -ChipDecoration.Decor.HBufferColumnCells, 2.0f, 16f);
            ChipDecoration.Decor.OeEnableBus(d,
                +ChipDecoration.Decor.HBufferColumnCells, 4.0f, 18f);
            ChipDecoration.Decor.Array(d, ChipDecoration.Decor.HBuffer,
                (new[] { 2 }, 18), (new[] { 4 }, 16), (new[] { 6 }, 14), (new[] { 8 }, 12),
                (new[] { 17 }, 3), (new[] { 15 }, 5), (new[] { 13 }, 7), (new[] { 11 }, 9));
            // Enable inverters in front of any buffer wire that crosses them.
            ChipDecoration.Decor.OeEnableGate(d, 1,
                -ChipDecoration.Decor.HBufferColumnCells, 2.0f);
            ChipDecoration.Decor.OeEnableGate(d, 19,
                +ChipDecoration.Decor.HBufferColumnCells, 4.0f);
        }
        );

    /// <summary>74HC541 octal unidirectional buffer with 3-state outputs,
    /// 20-pin DIP. Same function as the '244 but with all inputs on one
    /// side and all outputs on the other -- vastly cleaner for PCB layout.
    /// Both /OE1 and /OE2 must be LOW to drive the outputs.</summary>
    public static readonly ChipPartDefinition Ic74541 = new(
        PartNumber: "541", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/OE1", 1),              new("VCC",  20),
            new("A0",   2),              new("/OE2", 19),
            new("A1",   3),              new("Y0",   18, Out),
            new("A2",   4),              new("Y1",   17, Out),
            new("A3",   5),              new("Y2",   16, Out),
            new("A4",   6),              new("Y3",   15, Out),
            new("A5",   7),              new("Y4",   14, Out),
            new("A6",   8),              new("Y5",   13, Out),
            new("A7",   9),              new("Y6",   12, Out),
            new("GND", 10),              new("Y7",   11, Out),
        },
        IsSeries74: true,
        // A0..A7 -> Y0..Y7 octal buffers, centred on the chip axis. Enabled by
        // /OE1 (pin 1) AND /OE2 (pin 19) both LOW -> a 2-input AND with inverted
        // inputs, feeding a centre enable bus (behind the buffers). Last buffer
        // mid-row = 18.
        Decorate: d =>
        {
            ChipDecoration.Decor.Oe541EnableBus(d, 0f, 18f);
            ChipDecoration.Decor.Array(d, ChipDecoration.Decor.HBufferCentered,
                (new[] { 2 }, 18), (new[] { 3 }, 17), (new[] { 4 }, 16), (new[] { 5 }, 15),
                (new[] { 6 }, 14), (new[] { 7 }, 13), (new[] { 8 }, 12), (new[] { 9 }, 11));
            ChipDecoration.Decor.Oe541EnableGate(d, 1, 19, 0f);
        }
        );

    // ---- Multiplexers --------------------------------------------------

    /// <summary>74HC257 quad 2-to-1 multiplexer with 3-state outputs,
    /// 16-pin DIP. Common select S routes Ix0 (S LOW) or Ix1 (S HIGH) to
    /// each of the four Y outputs; /OE high tri-states all four outputs.</summary>
    public static readonly ChipPartDefinition Ic74257 = new(
        PartNumber: "257", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("S",   1),              new("VCC", 16),
            new("1I0", 2),              new("/OE", 15),
            new("1I1", 3),              new("4I0", 14),
            new("1Y",  4, Out),         new("4I1", 13),
            new("2I0", 5),              new("4Y",  12, Out),
            new("2I1", 6),              new("3I0", 11),
            new("2Y",  7, Out),         new("3I1", 10),
            new("GND", 8),              new("3Y",   9, Out),
        },
        IsSeries74: true
        );

    /// <summary>74HC157 quad 2-to-1 multiplexer, no tri-state, 16-pin DIP.
    /// Identical layout to the '257 except pin 15 is /E (enable) rather
    /// than /OE: /E HIGH forces all four Y outputs LOW (active drive,
    /// not high-Z).</summary>
    public static readonly ChipPartDefinition Ic74157 = new(
        PartNumber: "157", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("S",   1),              new("VCC", 16),
            new("1I0", 2),              new("/E",  15),
            new("1I1", 3),              new("4I0", 14),
            new("1Y",  4, Out),         new("4I1", 13),
            new("2I0", 5),              new("4Y",  12, Out),
            new("2I1", 6),              new("3I0", 11),
            new("2Y",  7, Out),         new("3I1", 10),
            new("GND", 8),              new("3Y",   9, Out),
        },
        IsSeries74: true
        );

    /// <summary>74LS151 8-to-1 multiplexer with complementary outputs,
    /// 16-pin DIP. S0..S2 select which of I0..I7 is routed to Y (true) and
    /// /Y (complement). /E HIGH disables the chip: Y forced LOW, /Y forced
    /// HIGH regardless of inputs. The classic "8 lines to one" data selector;
    /// also handy as a Boolean function generator (3-variable function in
    /// one chip via the select inputs).</summary>
    public static readonly ChipPartDefinition Ic74151 = new(
        PartNumber: "151", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
        new("I3",  1),              new("VCC", 16),
        new("I2",  2),              new("I4",  15),
        new("I1",  3),              new("I5",  14),
        new("I0",  4),              new("I6",  13),
        new("Y",   5, Out),         new("I7",  12),
        new("/Y",  6, Out),         new("S0",  11),
        new("/E",  7),              new("S1",  10),
        new("GND", 8),              new("S2",   9),
        },
        IsSeries74: true
        );

    /// <summary>74LS153 dual 4-to-1 multiplexer, 16-pin DIP. Shared select
    /// inputs S0, S1 drive both halves; each half has its own active-low
    /// enable (/1E, /2E) and its own output (1Y, 2Y). With /E HIGH the
    /// corresponding output is forced LOW. Non-inverting outputs (no
    /// complementary Y). Useful as a 2-bit selector across four sources.</summary>
    public static readonly ChipPartDefinition Ic74153 = new(
        PartNumber: "153", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
        new("/1E", 1),              new("VCC", 16),
        new("S1",  2),              new("/2E", 15),
        new("1I3", 3),              new("S0",  14),
        new("1I2", 4),              new("2I3", 13),
        new("1I1", 5),              new("2I2", 12),
        new("1I0", 6),              new("2I1", 11),
        new("1Y",  7, Out),         new("2I0", 10),
        new("GND", 8),              new("2Y",   9, Out),
        },
        IsSeries74: true
        );

    // ---- Decoders -----------------------------------------------------

    /// <summary>74HC138 3-to-8 line decoder/demultiplexer, 16-pin DIP.
    /// Address bits A0..A2 select which of /Y0../Y7 goes LOW; the rest
    /// stay HIGH. Enabled only when /E1 LOW, /E2 LOW, and E3 HIGH --
    /// the three enables are commonly used to combine with chip-select
    /// logic for memory decoding.</summary>
    public static readonly ChipPartDefinition Ic74138 = new(
        PartNumber: "138", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("A0",  1),              new("VCC", 16),
            new("A1",  2),              new("/Y0", 15, Out),
            new("A2",  3),              new("/Y1", 14, Out),
            new("/E1", 4),              new("/Y2", 13, Out),
            new("/E2", 5),              new("/Y3", 12, Out),
            new("E3",  6),              new("/Y4", 11, Out),
            new("/Y7", 7, Out),         new("/Y5", 10, Out),
            new("GND", 8),              new("/Y6",  9, Out),
        },
        IsSeries74: true
        );

    /// <summary>74HC139 dual 2-to-4 line decoder, 16-pin DIP. Two
    /// independent decoders sharing VCC/GND. Bank A: /AE enable, AA0/AA1
    /// inputs, /AY0../AY3 outputs (active LOW). Bank B: mirrored.</summary>
    public static readonly ChipPartDefinition Ic74139 = new(
        PartNumber: "139", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/AE",  1),              new("VCC", 16),
            new("AA0",  2),              new("/BE", 15),
            new("AA1",  3),              new("BA0", 14),
            new("/AY0", 4, Out),         new("BA1", 13),
            new("/AY1", 5, Out),         new("/BY0",12, Out),
            new("/AY2", 6, Out),         new("/BY1",11, Out),
            new("/AY3", 7, Out),         new("/BY2",10, Out),
            new("GND",  8),              new("/BY3", 9, Out),
        },
        IsSeries74: true
        );

    /// <summary>74HC154 4-to-16 line decoder/demultiplexer, 24-pin DIP.
    /// A0..A3 select which of /Y0../Y15 goes LOW. Both enables /E0 and
    /// /E1 must be LOW for any output to assert. Useful for full address
    /// decoding without cascading multiple '138s.</summary>
    public static readonly ChipPartDefinition Ic74154 = new(
        PartNumber: "154", PinCount: 24, PowerPin: 24, GroundPin: 12,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("/Y0",  1, Out),         new("VCC",  24),
            new("/Y1",  2, Out),         new("A0",   23),
            new("/Y2",  3, Out),         new("A1",   22),
            new("/Y3",  4, Out),         new("A2",   21),
            new("/Y4",  5, Out),         new("A3",   20),
            new("/Y5",  6, Out),         new("/E1",  19),
            new("/Y6",  7, Out),         new("/E0",  18),
            new("/Y7",  8, Out),         new("/Y15", 17, Out),
            new("/Y8",  9, Out),         new("/Y14", 16, Out),
            new("/Y9", 10, Out),         new("/Y13", 15, Out),
            new("/Y10",11, Out),         new("/Y12", 14, Out),
            new("GND", 12),              new("/Y11", 13, Out),
        },
        IsSeries74: true
        );

    // ---- Display decoders ---------------------------------------------

    /// <summary>7447 BCD-to-7-segment decoder/driver, 16-pin DIP. Open-collector
    /// active-LOW segment outputs (a..g) for driving common-anode displays
    /// through current-limiting resistors. /LT lights all segments for lamp
    /// test; /RBI / /BI cascade to blank leading zeros across multi-digit
    /// displays. Inputs A..D are BCD (A=LSB, D=MSB); inputs above 9 produce
    /// distinct non-digit glyphs rather than blanking.</summary>
    public static readonly ChipPartDefinition Ic7447 = new(
        PartNumber: "47", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("B",    1),              new("VCC", 16),
            new("C",    2),              new("f",   15, Out),
            new("/LT",  3),              new("g",   14, Out),
            new("/BI",  4),              new("a",   13, Out),
            new("/RBI", 5),              new("b",   12, Out),
            new("D",    6),              new("c",   11, Out),
            new("A",    7),              new("d",   10, Out),
            new("GND",  8),              new("e",    9, Out),
        },
        IsSeries74: true,
        DefaultFamily: TtlFamily.LS
        );

    /// <summary>7448 BCD-to-7-segment decoder/driver, 16-pin DIP. Active-HIGH
    /// segment outputs (a..g) with internal pull-ups for driving common-cathode
    /// displays through current-limiting resistors. Pin-identical to the 7447;
    /// the difference is output polarity. /LT, /BI/RBO, /RBI behave the same
    /// way as on the '47. The natural pick for a digital-style schematic where
    /// open-collector signalling isn't desired.</summary>
    public static readonly ChipPartDefinition Ic7448 = new(
        PartNumber: "48", PinCount: 16, PowerPin: 16, GroundPin: 8,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("B",    1),              new("VCC", 16),
            new("C",    2),              new("f",   15, Out),
            new("/LT",  3),              new("g",   14, Out),
            new("/BI",  4),              new("a",   13, Out),
            new("/RBI", 5),              new("b",   12, Out),
            new("D",    6),              new("c",   11, Out),
            new("A",    7),              new("d",   10, Out),
            new("GND",  8),              new("e",    9, Out),
        },
        IsSeries74: true,
        DefaultFamily: TtlFamily.LS
        );

    // ---- Programmable logic (GAL / PLD) -------------------------------

    /// <summary>
    /// Lattice GAL16V8 (and pin-compatible Atmel ATF16V8B), 20-pin DIP.
    /// Generic CMOS PLD: 8 output-logic macrocells (pins 12-19), eight
    /// dedicated inputs (pins 2-9), plus pin 1 (clock in registered mode,
    /// otherwise an input) and pin 11 (/OE in registered mode, otherwise an
    /// input). The macrocell pins are declared as outputs so they may drive a
    /// net (and so the floating-INPUT diagnostic, which advises tying to a
    /// rail, doesn't apply); when a fuse map configures one as a plain input
    /// it simply doesn't drive. Pin function is set by the JEDEC fuse map, not
    /// by this symbol -- the names here are the generic package roles.
    /// No simulation model yet; placeable and exportable as a board part.
    /// </summary>
    public static readonly ChipPartDefinition IcGal16V8 = new(
        PartNumber: "GAL16V8", PinCount: 20, PowerPin: 20, GroundPin: 10,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("CLK",  1),              new("VCC",  20),
            new("I2",   2),              new("IO19", 19, Out),
            new("I3",   3),              new("IO18", 18, Out),
            new("I4",   4),              new("IO17", 17, Out),
            new("I5",   5),              new("IO16", 16, Out),
            new("I6",   6),              new("IO15", 15, Out),
            new("I7",   7),              new("IO14", 14, Out),
            new("I8",   8),              new("IO13", 13, Out),
            new("I9",   9),              new("IO12", 12, Out),
            new("GND", 10),              new("/OE",  11),
        });

    /// <summary>
    /// Lattice GAL20V8 (and pin-compatible Atmel ATF20V8B), 24-pin DIP.
    /// Same architecture as the '16V8 with more input pins: 8 macrocells
    /// (pins 15-22), ten dedicated inputs (pins 2-11), two more inputs
    /// (pins 14, 23), pin 1 (clock / input) and pin 13 (/OE / input). Used
    /// where the decode needs more inputs than a '16V8 can take -- e.g. the
    /// flag-reading PCSEL device that must see N/Z/C alongside the opcode.
    /// Pin roles are set by the fuse map; names are the generic package roles.
    /// </summary>
    public static readonly ChipPartDefinition IcGal20V8 = new(
        PartNumber: "GAL20V8", PinCount: 24, PowerPin: 24, GroundPin: 12,
        BodyWidth: 8,
        Pins: new ChipPin[]
        {
            new("CLK",  1),              new("VCC",  24),
            new("I2",   2),              new("I23",  23),
            new("I3",   3),              new("IO22", 22, Out),
            new("I4",   4),              new("IO21", 21, Out),
            new("I5",   5),              new("IO20", 20, Out),
            new("I6",   6),              new("IO19", 19, Out),
            new("I7",   7),              new("IO18", 18, Out),
            new("I8",   8),              new("IO17", 17, Out),
            new("I9",   9),              new("IO16", 16, Out),
            new("I10", 10),              new("IO15", 15, Out),
            new("I11", 11),              new("I14",  14),
            new("GND", 12),              new("/OE",  13),
        });

    // ---- shorthand for readability ------------------------------------
    private const ChipPinRole Out = ChipPinRole.Output;


}