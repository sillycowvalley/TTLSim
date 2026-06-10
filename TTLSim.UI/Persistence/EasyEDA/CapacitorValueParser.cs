using System;
using System.Globalization;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// Parses and formats user-typed capacitor values. Sibling of
/// <see cref="ResistorValueParser"/> with the same patterns adapted for
/// capacitance.
///
/// Accepted input forms (case-insensitive on unit letters, omega/µ
/// pass-through tolerated):
///   "100"     -> 100 pF      (bare integer = picofarads by default --
///                              see note below)
///   "100p"    -> 100 pF
///   "100pF"   -> 100 pF
///   "10n"     -> 10 nF
///   "10nF"    -> 10 nF
///   "1u"      -> 1 µF
///   "1uF"     -> 1 µF
///   "1µF"     -> 1 µF        (µ accepted in addition to u/U)
///   "4u7"     -> 4.7 µF      (engineering form -- unit letter as decimal pt)
///   "4.7u"    -> 4.7 µF
///   "1m"      -> 1 mF        (rare, but valid)
///
/// Bare-integer default is PICOFARADS, not farads. That's the EE convention
/// on schematics: "100" next to a cap means 100pF (a typical ceramic value),
/// never 100F (a supercapacitor). This differs from the resistor parser,
/// which treats bare integers as ohms -- but ohms in the 1..1000 range are
/// commonplace and farads in the same range are not.
///
/// <see cref="FormatForDisplay(string)"/> turns the same input into display
/// text with SI symbols: "100pF", "10nF", "1µF", "4.7µF".
///
/// Unrecognised input throws FormatException; the catalogue translates that
/// into a NotImplementedException naming the offending value.
/// </summary>
public static class CapacitorValueParser
{
    // Canonical internal representation: picofarads (long). Picofarads
    // give us integer arithmetic down to 1pF and headroom up to ~9 mF
    // before overflowing long, which covers every electrolytic value
    // anyone would put on a digital board.

    /// <summary>
    /// Parses a user-typed capacitor value into picofarads. Throws
    /// FormatException for unparseable input.
    /// </summary>
    public static long ParseToPicofarads(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("Value is empty.");

        // Strip whitespace and the F/f suffix if present (it's the unit
        // symbol "farad" -- carries no scale information, the prefix
        // letter before it does that). Also normalise µ to u.
        string s = raw.Trim();
        // Replace µ (U+00B5 micro sign and U+03BC greek small letter mu)
        // with the ASCII 'u' so the rest of the parser can stay single-char.
        s = s.Replace('\u00b5', 'u').Replace('\u03bc', 'u');

        // Strip a trailing F/f if present.
        if (s.EndsWith("F", StringComparison.OrdinalIgnoreCase))
            s = s[..^1].TrimEnd();

        string up = s.ToUpperInvariant();

        // Case 1: bare number (no unit letter at all) -> picofarads.
        if (TryParseBareNumber(up, out double picoFarads))
        {
            if (picoFarads <= 0)
                throw new FormatException($"Capacitance must be positive, got {picoFarads}.");
            return (long)Math.Round(picoFarads);
        }

        // Case 2: number with a scale-prefix letter (P/N/U/M).
        char[] unitChars = ['P', 'N', 'U', 'M'];
        int unitIdx = -1;
        for (int i = 0; i < up.Length; i++)
        {
            if (Array.IndexOf(unitChars, up[i]) >= 0)
            {
                unitIdx = i;
                break;
            }
        }
        if (unitIdx < 0)
            throw new FormatException(
                $"Value '{raw}' has no scale prefix (p/n/u/m) and isn't a bare integer.");

        char unit = up[unitIdx];
        string prefix = up[..unitIdx];
        string suffix = up[(unitIdx + 1)..];

        double mantissa;
        if (suffix.Length == 0)
        {
            if (!double.TryParse(prefix, NumberStyles.Float, CultureInfo.InvariantCulture, out mantissa))
                throw new FormatException(
                    $"Value '{raw}' has unparseable mantissa '{prefix}'.");
        }
        else
        {
            // Engineering form: "4U7" means 4.7µ.
            if (!AllDigits(prefix) || !AllDigits(suffix))
                throw new FormatException(
                    $"Value '{raw}' has malformed engineering form. " +
                    "Use forms like '4u7' or '4.7u', not mixed.");

            string combined = $"{prefix}.{suffix}";
            if (!double.TryParse(combined, NumberStyles.Float, CultureInfo.InvariantCulture, out mantissa))
                throw new FormatException(
                    $"Value '{raw}' failed to parse as engineering form.");
        }

        if (mantissa <= 0)
            throw new FormatException($"Capacitance must be positive, got {mantissa}.");

        // Scale to picofarads.
        double picoFaradsScaled = unit switch
        {
            'P' => mantissa,                       // pF = pF
            'N' => mantissa * 1_000.0,             // nF = 1000pF
            'U' => mantissa * 1_000_000.0,         // µF = 1e6 pF
            'M' => mantissa * 1_000_000_000.0,     // mF = 1e9 pF
            _ => throw new FormatException($"Value '{raw}' has unknown scale '{unit}'.")
        };

        return (long)Math.Round(picoFaradsScaled);
    }

    /// <summary>
    /// Convert a user-typed capacitor value into display text using SI
    /// scale symbols: "100pF", "10nF", "2.2µF", "1µF". Throws FormatException
    /// for unparseable input (same conditions as <see cref="ParseToPicofarads"/>).
    ///
    /// Used by the Frankenstein capacitor catalogue entries to populate the
    /// per-instance Value ATTR override that EasyEDA renders on the schematic.
    /// </summary>
    public static string FormatForDisplay(string raw)
    {
        long pF = ParseToPicofarads(raw);

        // Pick the largest scale that yields a value >= 1 with no
        // remainder, or with at most one-decimal-place remainder.
        // Order: mF, µF, nF, pF (largest to smallest).
        // The decision boundary is whether the value divides cleanly
        // by the next scale up.

        if (pF >= 1_000_000_000L && pF % 1_000_000_000L == 0)
            return Format(pF / 1_000_000_000L, 0, "mF");
        if (pF >= 1_000_000L)
            return FormatScaled(pF, 1_000_000L, "\u00b5F");      // µF
        if (pF >= 1_000L)
            return FormatScaled(pF, 1_000L, "nF");
        return $"{pF}pF";
    }

    private static string FormatScaled(long pF, long scale, string unit)
    {
        long whole = pF / scale;
        long remainder = pF % scale;
        if (remainder == 0)
            return $"{whole}{unit}";

        // Render with up to two decimal places (cap values are typically
        // expressed with 1-2 sig figs in their fractional part). Trim
        // trailing zeroes.
        double real = pF / (double)scale;
        string s = real.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{s}{unit}";
    }

    private static string Format(long whole, int decimals, string unit)
    {
        return decimals == 0 ? $"{whole}{unit}" : $"{whole.ToString(CultureInfo.InvariantCulture)}{unit}";
    }

    private static bool TryParseBareNumber(string s, out double pF)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out pF);
    }

    private static bool AllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }
}