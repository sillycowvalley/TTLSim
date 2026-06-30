namespace TTLSim.Chips.Pld;

/// <summary>
/// Geometry of a GAL fuse map, sufficient for a COMBINATIONAL evaluator.
/// Values here are taken from the GALasm reference (PinToFuse*/ToOLMC tables,
/// SetAND, and the fuse-address #defines in galasm.h), so the column routing,
/// row->OLMC mapping, and fuse offsets match what a real assembler emits.
///
/// Fuse-array model (sum-of-products):
///   - The AND array is <see cref="Rows"/> product-term rows x <see cref="Cols"/>
///     columns. Fuse (r,c) = array[r*Cols + c]. SetAND writes
///     GALLogic[row*numofcol + column + negation], so column pairs are
///     (true = even base, complement = base+1) -- matching TrueColumn/
///     ComplementColumn below.
///   - <see cref="ColumnMapForMode"/> returns line->pin routing for the device
///     mode (selected by the SYN and AC0 fuses). Line i owns columns 2*i and
///     2*i+1; the pin feeding it depends on mode because the GAL re-routes
///     feedback differently in simple/complex/registered configurations.
///   - A fuse of 0 (intact) connects that column's literal into the product
///     term; 1 (blown) omits it. The term ANDs all connected literals; a
///     fully-blown row is unused and contributes 0 to the OLMC's OR.
///   - OLMC o (output on <see cref="OlmcOutputPins"/>[o], rows o*8..o*8+7) ORs
///     its terms then applies XOR polarity fuse <see cref="XorFuseBase"/>+o
///     (1 = active high, 0 = active low).
///
/// Modes (galasm MODE1/2/3), decoded from the SYN/AC0 fuses:
///   SYN=1 AC0=0 -> simple    (MODE1)
///   SYN=1 AC0=1 -> complex   (MODE2)
///   SYN=0 AC0=1 -> registered(MODE3)  (registered OLMCs evaluate combinationally
///                                       here; clocked behaviour is not modelled)
///
/// The pin-presentation metadata below (<see cref="Ac1FuseBase"/> and friends)
/// is consumed by <see cref="GalPinModel"/> to derive each pin's role and label
/// from the loaded fuses; the evaluator itself does not use it.
/// </summary>
public sealed record GalDevice(
    string PartNumber,
    int FuseCount,
    int Rows,
    int Cols,
    int OlmcCount,
    int[] OlmcOutputPins,
    int XorFuseBase,
    int SynFuse,
    int Ac0Fuse,
    int[] ColumnMapMode1,
    int[] ColumnMapMode2,
    int[] ColumnMapMode3)
{
    public int ProductTermsPerOlmc => Rows / OlmcCount;
    public int TrueColumn(int line) => 2 * line;
    public int ComplementColumn(int line) => 2 * line + 1;

    // ---- Pin-presentation metadata (used by GalPinModel, not the evaluator) ----

    /// <summary>JEDEC fuse number of the first AC1 bit (per-OLMC direction).
    /// AC1 for an OLMC on pin P is at Ac1FuseBase + (7 - (P - FirstOlmcPin)).</summary>
    public int Ac1FuseBase { get; init; }

    /// <summary>Lowest-numbered OLMC pin (16V8 = 12, 20V8 = 15). Used to map a
    /// pin number to its AC1 bit.</summary>
    public int FirstOlmcPin { get; init; }

    /// <summary>The pin that is CLK in registered mode, a plain input otherwise.</summary>
    public int ClockPin { get; init; }

    /// <summary>The pin that is /OE in registered mode, a plain input otherwise.</summary>
    public int OePin { get; init; }

    /// <summary>OLMC pins that force mode 2 and so can never be configured as
    /// inputs (16V8 = 15,16 ; 20V8 = 18,19).</summary>
    public int[] SpecialPins { get; init; } = System.Array.Empty<int>();

    /// <summary>Non-OLMC signal pins that are always array inputs (excludes
    /// power/ground and the clock/OE pins).</summary>
    public int[] DedicatedInputPins { get; init; } = System.Array.Empty<int>();

    /// <summary>Line-&gt;pin routing for the mode encoded by the SYN/AC0 fuses.
    /// A value of 0 marks a line not used as an array input in that mode.</summary>
    public int[] ColumnMapForMode(bool syn, bool ac0)
    {
        if (syn && !ac0) return ColumnMapMode1;   // simple
        if (syn && ac0) return ColumnMapMode2;    // complex
        if (!syn && ac0) return ColumnMapMode3;   // registered
        return ColumnMapMode1;                    // SYN=0 AC0=0 is invalid; fall back
    }

    // ---- GAL16V8: DIP-20, OLMCs on pins 19..12, 64x32 array, 2194 fuses ----
    // Column maps are the galasm PinToFuse16Mode* tables inverted to line->pin
    // (line i = base column 2*i). 0 = unused line in that mode.
    public static readonly GalDevice Gal16V8 = new(
        PartNumber: "GAL16V8",
        FuseCount: 2194,
        Rows: 64,
        Cols: 32,
        OlmcCount: 8,
        OlmcOutputPins: new[] { 19, 18, 17, 16, 15, 14, 13, 12 },
        XorFuseBase: 2048,
        SynFuse: 2192,
        Ac0Fuse: 2193,
        ColumnMapMode1: new[] { 2, 1, 3, 19, 4, 18, 5, 17, 6, 14, 7, 13, 8, 12, 9, 11 },
        ColumnMapMode2: new[] { 2, 1, 3, 18, 4, 17, 5, 16, 6, 15, 7, 14, 8, 13, 9, 11 },
        ColumnMapMode3: new[] { 2, 19, 3, 18, 4, 17, 5, 16, 6, 15, 7, 14, 8, 13, 9, 12 })
    {
        Ac1FuseBase = 2120,
        FirstOlmcPin = 12,
        ClockPin = 1,
        OePin = 11,
        SpecialPins = new[] { 15, 16 },
        DedicatedInputPins = new[] { 2, 3, 4, 5, 6, 7, 8, 9 },
    };

    // ---- GAL20V8: DIP-24, OLMCs on pins 22..15, 64x40 array, 2706 fuses ----
    public static readonly GalDevice Gal20V8 = new(
        PartNumber: "GAL20V8",
        FuseCount: 2706,
        Rows: 64,
        Cols: 40,
        OlmcCount: 8,
        OlmcOutputPins: new[] { 22, 21, 20, 19, 18, 17, 16, 15 },
        XorFuseBase: 2560,
        SynFuse: 2704,
        Ac0Fuse: 2705,
        ColumnMapMode1: new[] { 2, 1, 3, 23, 4, 22, 5, 21, 6, 20, 7, 17, 8, 16, 9, 15, 10, 14, 11, 13 },
        ColumnMapMode2: new[] { 2, 1, 3, 23, 4, 21, 5, 20, 6, 19, 7, 18, 8, 17, 9, 16, 10, 14, 11, 13 },
        ColumnMapMode3: new[] { 2, 23, 3, 22, 4, 21, 5, 20, 6, 19, 7, 18, 8, 17, 9, 16, 10, 15, 11, 14 })
    {
        Ac1FuseBase = 2632,
        FirstOlmcPin = 15,
        ClockPin = 1,
        OePin = 13,
        SpecialPins = new[] { 18, 19 },
        DedicatedInputPins = new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 14, 23 },
    };

    public static GalDevice? ForPartNumber(string partNumber) => partNumber switch
    {
        "GAL16V8" => Gal16V8,
        "GAL20V8" => Gal20V8,
        _ => null
    };
}