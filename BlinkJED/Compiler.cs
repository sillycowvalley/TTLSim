using System;
using System.Collections.Generic;

namespace BlinkyJed;

/// <summary>
/// GAL OLMC configuration mode for the whole device. The 16V8/20V8 pick ONE
/// mode for all eight macrocells, encoded by the SYN and AC0 architecture bits:
///   Simple      SYN=1 AC0=0   combinational, 8 terms, no tristate control
///   Complex     SYN=1 AC0=1   combinational I/O with own-pin feedback + a
///                             tristate (OE) term; 7 sum terms per cell
///   Registered  SYN=0 AC0=1   registered outputs (D-FF, /Q feedback, common
///                             CLK on pin 1 and /OE on pin 11/13); each cell is
///                             individually registered (8 terms) or combinational
///                             (7 terms + OE), chosen by that cell's AC1 bit.
/// </summary>
internal enum GalMode { Simple, Complex, Registered }

/// <summary>
/// Stage 3: map parsed equations onto a device fuse map for the 16V8 / 20V8,
/// covering all three OLMC modes (simple / complex / registered). Mirrors GALasm.
///
/// Array building (shared by every mode):
///   - A used product-term row starts all-1; each connected input column is
///     cleared to 0 (true line at PinToFuse[pin-1], complement at col+1). Rows
///     with no term stay all-0 and the writer omits them.
///   - output pin -> OLMC index (pin - first OLMC pin); the eight rows of an
///     OLMC begin at ToOlmc[olmc].
///
/// Mode selection is decided from the equations, not the .pld device mnemonic
/// (BlinkyJED ignores the S/A/AS/MS suffix, exactly as TargetDevice.Resolve
/// notes): any registered output forces registered mode; otherwise an output fed
/// back into the array -- or an input on a pin only the array can reach in the
/// wider modes -- forces complex; otherwise simple.
///
/// Per-mode architecture:
///   - SYN/AC0 as above. PT (product-term disable) all 1 in every mode.
///   - XOR[7-olmc] = 1 for an active-high output (0 = active low), all modes.
///   - AC1[7-olmc]: simple -> 0 for outputs, 1 for spare/input cells;
///                  complex -> 1 for every cell; registered -> 0 for registered
///                  cells, 1 for combinational or spare cells.
///   - Complex, and combinational cells inside registered mode, reserve the
///     FIRST product term (row offset 0) as the tristate/OE control and use
///     offsets 1..7 for the sum (7 terms). With no explicit .OE the output is
///     always enabled, emitted as an all-1 (fully blown) OE row. Registered and
///     simple cells use all eight rows (offsets 0..7) for the sum.
///
/// Simple-mode output is byte-for-byte unchanged from the previous version.
///
/// VALIDATION NOTE: the simple-mode fuse map was validated by diffing against
/// WinCUPL. The complex/registered architecture-bit and OE-term encodings here
/// follow the published GALasm model but have NOT been diffed against a WinCUPL
/// .jed in this build -- do that (compile the same .pld with WinCUPL/GALasm and
/// compare) before programming silicon.
///
/// Still not emitted (rejected by the parser with a clear error): an explicit
/// .OE tristate expression. Complex-mode outputs default to always-enabled.
/// </summary>
internal static class Compiler
{
    // Row offset per OLMC index: OLMC 0 -> row 56 ... OLMC 7 -> row 0.
    private static readonly int[] ToOlmc = { 56, 48, 40, 32, 24, 16, 8, 0 };

    // PinToFuse tables. Index = pin-1; value = base column (true literal), or -1
    // when that pin has no array-input column in this mode. Derived from the same
    // GALasm PinToFuse*/ColumnMap tables the simulator geometry uses.
    private static readonly int[] PinToFuse16Mode1 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, -1, 30, 26, 22, 18, -1, -1, 14, 10, 6, -1 };
    private static readonly int[] PinToFuse16Mode2 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, -1, 30, -1, 26, 22, 18, 14, 10, 6, -1, -1 };
    private static readonly int[] PinToFuse16Mode3 =
        { -1, 0, 4, 8, 12, 16, 20, 24, 28, -1, -1, 30, 26, 22, 18, 14, 10, 6, 2, -1 };

    private static readonly int[] PinToFuse20Mode1 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, -1, 38, 34, 30, 26, 22, -1, -1, 18, 14, 10, 6, -1 };
    private static readonly int[] PinToFuse20Mode2 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, -1, 38, 34, -1, 30, 26, 22, 18, 14, 10, -1, 6, -1 };
    private static readonly int[] PinToFuse20Mode3 =
        { -1, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, -1, -1, 38, 34, 30, 26, 22, 18, 14, 10, 6, 2, -1 };

    public static FuseMap Compile(PldDocument doc, TargetDevice device, List<string> errors)
    {
        var map = new FuseMap(device);   // Logic/Xor/Sig/Ac1/Pt all 0, Syn/Ac0 0

        bool is16 = device is Gal16V8;
        int firstOlmcPin = is16 ? 12 : 15;
        int lastOlmcPin = is16 ? 19 : 22;
        int specialA = is16 ? 15 : 18;   // pins the array reaches only in complex/registered
        int specialB = is16 ? 16 : 19;
        int cols = device.Columns;

        // name -> pin number
        var pinOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PinDeclaration p in doc.Pins)
        {
            if (!pinOf.TryAdd(p.Name, p.Number))
                errors.Add($"Pin name '{p.Name}' is declared more than once.");
        }

        // ---- choose the device mode from the equations ---------------------
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation e in doc.Equations) outputNames.Add(e.Target);

        bool anyRegistered = false;
        bool needsWiderInput = false;   // an output fed back, or an input on a special pin
        foreach (Equation e in doc.Equations)
        {
            if (e.Registered) anyRegistered = true;
            foreach (ProductTerm t in e.Terms)
                foreach (KeyValuePair<string, bool> lit in t.Literals)
                {
                    if (outputNames.Contains(lit.Key)) needsWiderInput = true;
                    if (pinOf.TryGetValue(lit.Key, out int lp) && (lp == specialA || lp == specialB))
                        needsWiderInput = true;
                }
        }

        GalMode mode = anyRegistered ? GalMode.Registered
                     : needsWiderInput ? GalMode.Complex
                     : GalMode.Simple;

        int[] pinToFuse = mode switch
        {
            GalMode.Simple => is16 ? PinToFuse16Mode1 : PinToFuse20Mode1,
            GalMode.Complex => is16 ? PinToFuse16Mode2 : PinToFuse20Mode2,
            _ => is16 ? PinToFuse16Mode3 : PinToFuse20Mode3,
        };

        // ---- architecture word ---------------------------------------------
        map.Syn = (byte)(mode == GalMode.Registered ? 0 : 1);
        map.Ac0 = (byte)(mode == GalMode.Simple ? 0 : 1);
        for (int i = 0; i < map.Pt.Length; i++) map.Pt[i] = 1;   // all product terms enabled

        var usedOlmc = new HashSet<int>();
        var registeredCell = new bool[TargetDevice.Ac1Size];

        foreach (Equation eq in doc.Equations)
        {
            if (!pinOf.TryGetValue(eq.Target, out int pin))
            {
                errors.Add($"Output '{eq.Target}' has no PIN declaration.");
                continue;
            }
            if (pin < firstOlmcPin || pin > lastOlmcPin)
            {
                errors.Add($"Output '{eq.Target}' (pin {pin}) is not an output-capable pin ({firstOlmcPin}-{lastOlmcPin}).");
                continue;
            }

            int olmc = pin - firstOlmcPin;
            if (!usedOlmc.Add(olmc))
            {
                errors.Add($"Output '{eq.Target}' (pin {pin}) is driven by more than one equation.");
                continue;
            }
            registeredCell[olmc] = eq.Registered;

            // Complex and registered-combinational cells give up the first product
            // term to the tristate/OE control; registered and simple cells keep all 8.
            bool hasOeRow = mode == GalMode.Complex
                         || (mode == GalMode.Registered && !eq.Registered);
            int sumStart = hasOeRow ? 1 : 0;
            int maxTerms = 8 - sumStart;

            if (eq.Terms.Count > maxTerms)
            {
                errors.Add($"Output '{eq.Target}': {eq.Terms.Count} product terms exceed the {maxTerms} available per output in {mode.ToString().ToLowerInvariant()} mode.");
                continue;
            }

            int startRow = ToOlmc[olmc];

            // Always-enabled tristate term: a fully blown (all-1) OE row.
            if (hasOeRow)
            {
                int oeBase = startRow * cols;
                for (int c = 0; c < cols; c++) map.Logic[oeBase + c] = 1;
            }

            for (int t = 0; t < eq.Terms.Count; t++)
            {
                int baseIdx = (startRow + sumStart + t) * cols;
                for (int c = 0; c < cols; c++) map.Logic[baseIdx + c] = 1;   // row starts all-1

                foreach (KeyValuePair<string, bool> lit in eq.Terms[t].Literals)
                {
                    string sig = lit.Key;
                    bool asserted = lit.Value;

                    if (!pinOf.TryGetValue(sig, out int litPin))
                    {
                        errors.Add($"Output '{eq.Target}': undeclared signal '{sig}'.");
                        continue;
                    }

                    int col = pinToFuse[litPin - 1];
                    if (col < 0)
                    {
                        errors.Add($"Output '{eq.Target}': pin {litPin} ('{sig}') cannot be an input in {mode.ToString().ToLowerInvariant()} mode.");
                        continue;
                    }

                    int negation = asserted ? 0 : 1;   // true line at col, complement at col+1
                    map.Logic[baseIdx + col + negation] = 0;   // clear -> connect this input
                }
            }

            // Output polarity: active-high output sets its XOR bit.
            if (!eq.ActiveLow)
                map.Xor[TargetDevice.XorSize - 1 - olmc] = 1;
        }

        // ---- AC1 per OLMC --------------------------------------------------
        for (int olmc = 0; olmc < TargetDevice.Ac1Size; olmc++)
        {
            byte ac1;
            if (mode == GalMode.Complex)
                ac1 = 1;                                       // every cell is tristate I/O
            else if (!usedOlmc.Contains(olmc))
                ac1 = 1;                                       // spare cell -> input
            else if (mode == GalMode.Registered && !registeredCell[olmc])
                ac1 = 1;                                       // combinational cell in registered mode
            else
                ac1 = 0;                                       // simple output, or registered cell

            map.Ac1[TargetDevice.Ac1Size - 1 - olmc] = ac1;
        }

        // User signature: emitted ERASED (all 1s), deliberately diverging from
        // WinCUPL, which writes the first 8 chars of PartNo here. Some device
        // programmers (observed: Xgecu T48, APP 13.16, GAL16V8D) do not program
        // the UES region from the fuse buffer but still verify it against the
        // buffer; any 0 bit in the file's UES then aborts the burn at fuse 2056
        // with "Verify Error". An all-1s UES matches the erased state, so it
        // verifies whether the programmer skips the region or programs it.
        // The UES is a purely cosmetic label; nothing functional reads it.
        for (int i = 0; i < map.Sig.Length; i++)
            map.Sig[i] = 1;

        return map;
    }
}