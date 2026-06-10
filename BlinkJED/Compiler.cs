using System;
using System.Collections.Generic;

namespace BlinkyJed;

/// <summary>
/// Stage 3: map parsed equations onto a device fuse map, for the
/// COMBINATIONAL (GAL "mode 1") case of the 16V8 / 20V8. Mirrors GALasm:
///
///   - Logic starts all-1; a used product-term row keeps its 1s except the
///     connected input columns, which are cleared to 0. Every other row stays 0
///     (the writer omits all-zero rows).
///   - output pin -> OLMC index (pin - first OLMC pin); product term t -> row
///     ToOlmc[olmc] + t; literal -> column PinToFuse[pin-1], true line at col,
///     complement at col+1.
///   - SYN=1, AC0=0 (mode 1); PT all 1; AC1 0; XOR[7-olmc]=1 per active-high out.
///
/// Not handled yet (rejected with a clear error): registered/tristate outputs
/// (mode 3 / mode 2), and inputs/feedback on the mode-2-forcing pins
/// (16V8: 15,16 ; 20V8: 18,19). Equation suffixes (.D/.R/.OE) are already
/// rejected by the parser.
/// </summary>
internal static class Compiler
{
    // Row offset per OLMC index: OLMC 0 -> row 56 ... OLMC 7 -> row 0.
    private static readonly int[] ToOlmc = { 56, 48, 40, 32, 24, 16, 8, 0 };

    // PinToFuse, MODE 1 (simple). Index = pin-1; -1 = no column for that pin.
    private static readonly int[] PinToFuse16Mode1 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, -1, 30, 26, 22, 18, -1, -1, 14, 10, 6, -1 };
    private static readonly int[] PinToFuse20Mode1 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, -1, 38, 34, 30, 26, 22, -1, -1, 18, 14, 10, 6, -1 };

    public static FuseMap Compile(PldDocument doc, TargetDevice device, List<string> errors)
    {
        var map = new FuseMap(device);   // Logic/Xor/Sig/Ac1/Pt all 0, Syn/Ac0 0

        bool is16 = device is Gal16V8;
        int firstOlmcPin = is16 ? 12 : 15;
        int lastOlmcPin = is16 ? 19 : 22;
        int[] pinToFuse = is16 ? PinToFuse16Mode1 : PinToFuse20Mode1;
        int specialA = is16 ? 15 : 18;   // mode-2-forcing input pins
        int specialB = is16 ? 16 : 19;
        int cols = device.Columns;

        // name -> pin number
        var pinOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PinDeclaration p in doc.Pins)
        {
            if (!pinOf.TryAdd(p.Name, p.Number))
                errors.Add($"Pin name '{p.Name}' is declared more than once.");
        }

        // Mode-1 architecture word.
        map.Syn = 1;
        map.Ac0 = 0;
        for (int i = 0; i < map.Pt.Length; i++) map.Pt[i] = 1;   // PT all 1

        var usedOlmc = new HashSet<int>();

        foreach (Equation eq in doc.Equations)
        {
            if (eq.Registered)
            {
                errors.Add($"Output '{eq.Target}': registered outputs (mode 3) not supported yet.");
                continue;
            }
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

            if (eq.Terms.Count > 8)
            {
                errors.Add($"Output '{eq.Target}': {eq.Terms.Count} product terms exceed the 8 available per output.");
                continue;
            }

            int startRow = ToOlmc[olmc];
            for (int t = 0; t < eq.Terms.Count; t++)
            {
                int baseIdx = (startRow + t) * cols;
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
                    if (litPin == specialA || litPin == specialB)
                    {
                        errors.Add($"Output '{eq.Target}': input on pin {litPin} would force mode 2 (not supported yet).");
                        continue;
                    }

                    int col = pinToFuse[litPin - 1];
                    if (col < 0)
                    {
                        errors.Add($"Output '{eq.Target}': pin {litPin} ('{sig}') cannot be an input here.");
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

        // Spare OLMC pins -> inputs (AC1 = 1), matching WinCUPL; used outputs stay 0.
        for (int olmc = 0; olmc < TargetDevice.Ac1Size; olmc++)
            if (!usedOlmc.Contains(olmc))
                map.Ac1[TargetDevice.Ac1Size - 1 - olmc] = 1;

        // User signature: first 8 chars of PartNo, MSB-first, into the Sig region
        // (matches WinCUPL). A shorter PartNo leaves the remaining bytes 0.
        int sigChars = Math.Min(8, doc.PartNo.Length);
        for (int k = 0; k < sigChars; k++)
        {
            int ch = doc.PartNo[k] & 0xFF;
            for (int b = 0; b < 8; b++)
                map.Sig[k * 8 + b] = (byte)((ch >> (7 - b)) & 1);
        }

        return map;
    }
}