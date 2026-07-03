using System;
using System.Collections.Generic;
using System.Globalization;

namespace TTLSim.Chips.Pld;

/// <summary>
/// Reads the free-text design-specification header of a JEDEC fuse map --
/// the region between STX and the first '*' field -- for the metadata
/// BlinkyJED writes there:
///
///   Name:           GAL1_ALU
///   Pins:           1:WRPH 2:OP0 3:OP1 ... 13:!C_WE
///
/// Standard JEDEC has no pin-name or design-name field, so this block is
/// BlinkyJED's own convention (see Chip_Labels.md). Each "Pins:" line is
/// independently parseable (wrapped lines repeat the label); tokens are
/// "number:name" with CUPL's leading '!' for active-low, converted here to
/// TTLSim's leading-'/' convention. Files without the block (WinCUPL output,
/// older BlinkyJED output) simply return null and callers fall back to the
/// fuse-derived role labels or the static definition names.
/// </summary>
public static class GalJedecHeader
{
    /// <summary>
    /// Pin-number -&gt; signal-name map from the header's "Pins:" lines, with
    /// active-low '!' converted to a leading '/'. Null when the text has no
    /// parseable "Pins:" line.
    /// </summary>
    public static Dictionary<int, string>? TryParsePinNames(string? jedecText)
    {
        string? header = HeaderRegion(jedecText);
        if (header is null) return null;

        Dictionary<int, string>? names = null;
        foreach (string rawLine in header.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("Pins:", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (string token in line.Substring(5)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.IndexOf(':');
                if (colon <= 0 || colon == token.Length - 1) continue;
                if (!int.TryParse(token.Substring(0, colon), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int number) || number < 1)
                    continue;

                string name = token.Substring(colon + 1);
                if (name[0] == '!') name = "/" + name.Substring(1);
                if (name.Length == 0 || name == "/") continue;

                names ??= new Dictionary<int, string>();
                names[number] = name;
            }
        }
        return names;
    }

    /// <summary>
    /// The design name from the header's "Name:" line (e.g. "GAL1_ALU"), or
    /// null when absent. This is the .pld Name property BlinkyJED writes, so
    /// a programmed GAL can be identified by its design rather than its
    /// generic part number.
    /// </summary>
    public static string? TryParseDesignName(string? jedecText)
    {
        string? header = HeaderRegion(jedecText);
        if (header is null) return null;

        foreach (string rawLine in header.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase)) continue;
            string value = line.Substring(5).Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }

    /// <summary>
    /// The design-specification region: from just after STX (or the start of
    /// the text when unframed) to the first '*' field. Null for empty input
    /// or when no header text precedes the fields.
    /// </summary>
    private static string? HeaderRegion(string? jedecText)
    {
        if (string.IsNullOrWhiteSpace(jedecText)) return null;

        int start = jedecText.IndexOf('\x02');
        start = start >= 0 ? start + 1 : 0;
        int end = jedecText.IndexOf('*', start);
        if (end < 0) end = jedecText.Length;
        return end > start ? jedecText.Substring(start, end - start) : null;
    }
}