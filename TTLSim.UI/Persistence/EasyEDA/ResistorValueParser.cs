using System;
using System.Globalization;

namespace TTLSim.UI.Persistence.EasyEDA;

/// <summary>
/// Normalises a user-typed resistor value string into a canonical lookup key
/// that matches the manufacturer part suffix style: "1R", "1R5", "100R",
/// "1K", "1K8", "10K", "1M", "1M5", "10M".
///
/// All of these forms produce the same canonical output:
///   "100"     -> "100R"   (bare integer = ohms)
///   "100R"    -> "100R"
///   "220Ω"    -> "220R"   (omega character stripped)
///   "2k2"     -> "2K2"    (case-insensitive on unit letter)
///   "2K2"     -> "2K2"
///   "2.2k"    -> "2K2"    (decimal-point form -> engineering form)
///   "1.5K"    -> "1K5"
///   "1M5"     -> "1M5"
///   "4.7M"    -> "4M7"
///
/// <see cref="FormatForDisplay(string)"/> turns the same input into display
/// text with the SI omega symbol: "100Ω", "2.2kΩ", "1.5MΩ", "4.7MΩ".
///
/// Unrecognised input throws FormatException; the catalogue translates that
/// into a NotImplementedException with a clear message naming the offending
/// value.
/// </summary>
public static class ResistorValueParser
{
    public static string Normalise(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("Value is empty.");

        // Strip whitespace and the omega suffix; uppercase the unit letters
        // so we can pattern-match without case sensitivity.
        string s = raw.Trim();
        if (s.EndsWith("\u03a9"))                 // capital omega
            s = s[..^1].TrimEnd();
        if (s.EndsWith("\u03c9"))                 // lowercase omega (rare)
            s = s[..^1].TrimEnd();

        // Normalise unit letter to uppercase. We accept r/R, k/K, m/M.
        // Build a working copy in uppercase but keep the same length so
        // indexes line up.
        string up = s.ToUpperInvariant();

        // Case 1: bare number (no unit letter at all).
        if (TryParseBareNumber(up, out double ohms))
            return ToEngineering(ohms);

        // Case 2: number followed by R/K/M as a suffix (no decimal in the prefix).
        //   "100R", "1K", "1M5", "2K2", "10M"
        // The unit letter can be EITHER at the end OR embedded as a decimal
        // separator ("1K8" means 1.8k, "2M2" means 2.2M).
        // Detect both:
        char[] unitChars = ['R', 'K', 'M'];
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
                $"Value '{raw}' has no unit (R/K/M) and isn't a bare integer.");

        char unit = up[unitIdx];
        string prefix = up[..unitIdx];
        string suffix = up[(unitIdx + 1)..];

        // Two acceptable layouts:
        //   "<digits>[.<digits>]<unit>"      -- decimal-point form (e.g. "2.2K", "1.5K", "100R")
        //   "<digits><unit><digits>"          -- engineering form (e.g. "2K2", "1K5")
        double mantissa;
        if (suffix.Length == 0)
        {
            // Decimal-point or whole-number form.
            if (!double.TryParse(prefix, NumberStyles.Float, CultureInfo.InvariantCulture, out mantissa))
                throw new FormatException(
                    $"Value '{raw}' has unparseable mantissa '{prefix}'.");
        }
        else
        {
            // Engineering form: <prefix><unit><suffix> means <prefix>.<suffix> * unit.
            // Both prefix and suffix must be digit-only.
            if (!AllDigits(prefix) || !AllDigits(suffix))
                throw new FormatException(
                    $"Value '{raw}' has malformed engineering form. " +
                    "Use forms like '2K2' or '2.2K', not mixed.");

            string combined = $"{prefix}.{suffix}";
            if (!double.TryParse(combined, NumberStyles.Float, CultureInfo.InvariantCulture, out mantissa))
                throw new FormatException(
                    $"Value '{raw}' failed to parse as engineering form.");
        }

        double multiplier = unit switch
        {
            'R' => 1.0,
            'K' => 1_000.0,
            'M' => 1_000_000.0,
            _ => throw new FormatException($"Value '{raw}' has unknown unit '{unit}'.")
        };

        return ToEngineering(mantissa * multiplier);
    }

    /// <summary>
    /// Convert a user-typed resistor value into display text using the
    /// SI symbol with omega: "470Ω", "2.2kΩ", "1.5MΩ", "1MΩ". Throws
    /// FormatException for unparseable input (same conditions as
    /// <see cref="Normalise"/>).
    ///
    /// Used by the Frankenstein resistor catalogue entry to populate the
    /// per-instance Value ATTR override that EasyEDA renders on the
    /// schematic.
    /// </summary>
    public static string FormatForDisplay(string raw)
    {
        // Reuse the canonicaliser to handle parsing, then convert the
        // engineering-suffix form ("470R", "2K2", "1M5") to display form
        // ("470Ω", "2.2kΩ", "1.5MΩ"). Engineering form is unambiguous --
        // the unit letter sits where a decimal point would be -- so this
        // is a straightforward textual transform.
        string canonical = Normalise(raw);

        // Find the unit letter. It is always present in the canonical
        // output: R / K / M.
        int unitIdx = -1;
        char unit = '\0';
        for (int i = 0; i < canonical.Length; i++)
        {
            char c = canonical[i];
            if (c == 'R' || c == 'K' || c == 'M')
            {
                unitIdx = i;
                unit = c;
                break;
            }
        }
        if (unitIdx < 0)
            throw new FormatException(
                $"FormatForDisplay: canonical form '{canonical}' has no unit letter. " +
                "This is a Normalise() invariant violation; please report.");

        string whole = canonical[..unitIdx];
        string frac = canonical[(unitIdx + 1)..];

        // Compose mantissa as "<whole>" or "<whole>.<frac>".
        string mantissa = frac.Length == 0 ? whole : $"{whole}.{frac}";

        // Map unit letter -> display suffix.
        string suffix = unit switch
        {
            'R' => "\u03a9",            // Ω
            'K' => "k\u03a9",           // kΩ
            'M' => "M\u03a9",           // MΩ
            _ => throw new FormatException($"Unknown unit '{unit}'."),
        };

        return mantissa + suffix;
    }

    private static bool TryParseBareNumber(string s, out double ohms)
    {
        // Accept either an integer ("100") or decimal ("100.5") with no unit.
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out ohms);
    }

    private static bool AllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }

    /// <summary>
    /// Convert an ohm value to MPN-suffix engineering form: "100R", "1K",
    /// "1K8", "10K", "1M", "1M5". The unit letter sits where the decimal
    /// point would be in scientific notation.
    /// </summary>
    private static string ToEngineering(double ohms)
    {
        if (ohms <= 0)
            throw new FormatException($"Resistance must be positive, got {ohms}.");

        char unit;
        double scaled;
        if (ohms >= 1_000_000)
        {
            unit = 'M';
            scaled = ohms / 1_000_000.0;
        }
        else if (ohms >= 1_000)
        {
            unit = 'K';
            scaled = ohms / 1_000.0;
        }
        else
        {
            unit = 'R';
            scaled = ohms;
        }

        // Integer scale: "1M", "10K", "100R"
        if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9)
            return $"{(long)Math.Round(scaled)}{unit}";

        // Fractional scale: render with one decimal place, then move the
        // unit letter to where the decimal point was. "1.5" -> "1K5".
        string decimalForm = scaled.ToString("0.#", CultureInfo.InvariantCulture);
        int dot = decimalForm.IndexOf('.');
        if (dot < 0)
            return $"{decimalForm}{unit}";

        string whole = decimalForm[..dot];
        string frac = decimalForm[(dot + 1)..];
        return $"{whole}{unit}{frac}";
    }
}