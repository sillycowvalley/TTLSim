namespace TTLSim.Chips.Pld;

/// <summary>
/// Geometry of the GAL22V10 / ATF22V10 fuse map (the two are fuse-compatible;
/// one part covers both). A sibling of <see cref="GalDevice"/> rather than an
/// extension of it, because the 22V10 shares almost nothing with the
/// SYN/AC0 model: there are no global modes, no XOR/AC1 regions, input
/// routing is fixed, product-term counts vary per macrocell, and two global
/// product-term rows implement asynchronous reset and synchronous preset.
///
/// Values are the GALasm 22V10 device tables (the same PinToFuse/ToOLMC
/// source the <see cref="GalDevice"/> constants came from), proven
/// fuse-by-fuse against WinCUPL .jed references for a combinational and a
/// registered design, and validated on silicon (ATF22V10C via BlinkyJED).
///
/// Fuse-array model:
///   - 132 product-term rows x 44 columns; fuse (r,c) = array[r*44 + c].
///     Column pairs are (true = 2*line, complement = 2*line+1), the same
///     convention as the V8 parts; <see cref="LineToPin"/> gives the fixed
///     line -> pin routing (12 dedicated inputs + 10 macrocell feedbacks).
///   - Row 0 is the global AR (asynchronous reset) term and row 131 the
///     global SP (synchronous preset) term. Between them sit ten OLMC blocks
///     in pin order 23 down to 14, each [1 output-enable row + its logic
///     rows]; logic term counts are 8,10,12,14,16,16,14,12,10,8.
///   - Each OLMC is configured by its own S0/S1 fuse pair at
///     <see cref="SBitsAddr"/> + 2*olmc (olmc = 23 - pin):
///       S1 = 1 combinational, 0 registered;
///       S0 = 1 active-high,  0 active-low (an output-buffer polarity
///       select; the array always holds the positive-logic expression).
///   - Fuses 5828..5891 are the UES (electronic signature). It is not part
///     of the logic; the evaluator reads only 0..5827. JEDEC files come in
///     both QF5828 (GALasm) and QF5892 (WinCUPL, BlinkyJED) flavours -- the
///     import gate accepts both, and the factory's length-clamped copy makes
///     either fit this <see cref="FuseCount"/>.
///
/// Registered semantics (all datasheet, all livetested on the V8 pattern):
///   - Pin 1 is the common register clock AND array input line 0, so unlike
///     V8 mode 3 it may appear in equations. There is no global /OE pin;
///     pin 13 is a plain input. Every macrocell -- registered or
///     combinational -- is gated by its own OE row (all-blown = a product of
///     nothing = always enabled; all-intact can never be true, so an erased
///     cell naturally never drives).
///   - A registered cell's feedback taps the register's /Q, independent of
///     the OE state and of the S0 polarity buffer (which sits after the
///     tap). A combinational cell's feedback taps the PIN, so a tri-stated
///     cell reads whatever drives the net externally.
///   - Registers have a defined power-up reset (Q = 0): active-high
///     registered outputs power up LOW and active-low ones HIGH -- the
///     opposite convention to the V8, whose inverting pin buffer makes every
///     registered output power up high.
/// </summary>
public static class Gal22V10Device
{
    public const string PartNumber = "GAL22V10";

    /// <summary>The logic-bearing fuse span the evaluator reads (array + S
    /// bits). Files declaring QF5892 additionally carry the UES, which the
    /// factory's clamped copy drops.</summary>
    public const int FuseCount = 5828;

    public const int Rows = 132;
    public const int Cols = 44;
    public const int ArRow = 0;      // global asynchronous-reset product term
    public const int SpRow = 131;    // global synchronous-preset product term

    public const int OlmcCount = 10;
    public const int ClockPin = 1;

    /// <summary>Macrocell output pins in OLMC-index order (index = 23 - pin),
    /// matching the S-bit pair order and the block tables below.</summary>
    public static readonly int[] OlmcOutputPins = { 23, 22, 21, 20, 19, 18, 17, 16, 15, 14 };

    /// <summary>First row of each OLMC block -- the OE row; logic rows follow.</summary>
    public static readonly int[] BlockStartRow = { 1, 10, 21, 34, 49, 66, 83, 98, 111, 122 };

    /// <summary>Logic terms per OLMC (excluding the OE row).</summary>
    public static readonly int[] BlockTermCount = { 8, 10, 12, 14, 16, 16, 14, 12, 10, 8 };

    /// <summary>S0/S1 pairs: S0 at SBitsAddr + 2*olmc, S1 at +1.</summary>
    public const int SBitsAddr = 5808;

    /// <summary>Fixed line -> pin routing (line i owns columns 2*i and
    /// 2*i+1). Dedicated inputs and macrocell feedbacks interleave; there are
    /// no mode variants on this family.</summary>
    public static readonly int[] LineToPin =
        { 1, 23, 2, 22, 3, 21, 4, 20, 5, 19, 6, 18, 7, 17, 8, 16, 9, 15, 10, 14, 11, 13 };

    public static int TrueColumn(int line) => 2 * line;
    public static int ComplementColumn(int line) => 2 * line + 1;

    public static int S0Fuse(int olmc) => SBitsAddr + 2 * olmc;
    public static int S1Fuse(int olmc) => SBitsAddr + 2 * olmc + 1;

    /// <summary>First fuse of an OLMC's block (its OE row).</summary>
    public static int BlockFirstFuse(int olmc) => BlockStartRow[olmc] * Cols;

    /// <summary>One past the last fuse of an OLMC's block (OE + logic rows).</summary>
    public static int BlockEndFuse(int olmc) =>
        (BlockStartRow[olmc] + 1 + BlockTermCount[olmc]) * Cols;
}