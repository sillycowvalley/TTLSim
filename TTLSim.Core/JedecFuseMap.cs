using System.Globalization;
using System.Text;

namespace TTLSim.Core;

/// <summary>
/// Parsed JEDEC fuse map (the compiled artifact a GAL/PLD assembler such as
/// WinCUPL emits, and what burns into the device). This is the GAL peer of
/// <see cref="IntelHex"/>: it turns the JEDEC text into a flat fuse array plus
/// the declared fuse/pin counts, and leaves interpretation of which fuse means
/// what to the device model (a JEDEC file is device-agnostic — the same format
/// describes a 16V8, a 22V10, etc.).
/// </summary>
public sealed record JedecData(int FuseCount, bool[] Fuses, int PinCount);

/// <summary>
/// Minimal JEDEC (JESD3-C) reader. Handles the fields a GAL fuse map uses:
///   QF  fuse count            QP  pin count
///   F   default fuse state    L   fuse data at an address
///   C   fuse checksum         G   security fuse (parsed, ignored)
/// Fields are '*'-terminated; an optional STX/ETX frame and the trailing
/// transmission checksum are tolerated. A present fuse checksum (C) is
/// verified; a mismatch throws, mirroring how IntelHex rejects bad records.
/// </summary>
public static class JedecFuseMap
{
    public static JedecData Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Empty JEDEC file.");

        // Strip the STX/ETX frame if present; everything we read lives between
        // the design header and the ETX. Work field-by-field on '*' splits.
        int stx = text.IndexOf('\x02');
        int etx = text.IndexOf('\x03');
        string body = stx >= 0 && etx > stx ? text.Substring(stx + 1, etx - stx - 1) : text;

        // Drop the design specification -- the free text between STX and the
        // first '*'. It is never a field, but tools differ in what they put
        // there (e.g. WinCUPL writes "CUPL(WM) ..."); without this, that header
        // would be dispatched on its first character and a leading 'C'/'F'/'Q'
        // would be misread as a field.
        int firstField = body.IndexOf('*');
        if (firstField >= 0) body = body.Substring(firstField);

        int fuseCount = -1;
        int pinCount = 0;
        bool defaultFuse = false;
        int? declaredChecksum = null;

        // First pass for QF (we must size the array before applying L records).
        foreach (string raw in body.Split('*'))
        {
            string f = raw.Trim();
            if (f.Length < 2) continue;
            if (f[0] == 'Q' && f[1] == 'F')
                fuseCount = ParseInt(f.AsSpan(2), "QF fuse count");
        }
        if (fuseCount <= 0)
            throw new FormatException("JEDEC file has no QF (fuse count) field.");

        bool[] fuses = new bool[fuseCount];

        foreach (string raw in body.Split('*'))
        {
            string f = raw.Trim();
            if (f.Length == 0) continue;

            switch (f[0])
            {
                case 'Q' when f.Length >= 2 && f[1] == 'P':
                    pinCount = ParseInt(f.AsSpan(2), "QP pin count");
                    break;

                case 'F':                               // default fuse state
                    defaultFuse = f.Length >= 2 && f[1] == '1';
                    for (int i = 0; i < fuseCount; i++) fuses[i] = defaultFuse;
                    break;

                case 'L':                               // fuse data at address
                    ApplyFuseRecord(f, fuses);
                    break;

                case 'C':                               // fuse checksum (hex)
                    declaredChecksum = (int)ParseHex(f.AsSpan(1), "C fuse checksum");
                    break;

                    // QF handled in the first pass; G (security), N (note), V, etc.
                    // are not needed for evaluation and are ignored.
            }
        }

        if (declaredChecksum is int want)
        {
            int got = FuseChecksum(fuses);
            if (got != want)
                throw new FormatException(
                    $"JEDEC fuse checksum mismatch: file says {want:X4}, computed {got:X4}.");
        }

        return new JedecData(fuseCount, fuses, pinCount);
    }

    // L<address> <bit string> — bits are '0'/'1', whitespace between groups is
    // ignored, applied starting at <address>.
    private static void ApplyFuseRecord(string field, bool[] fuses)
    {
        int i = 1;
        while (i < field.Length && char.IsDigit(field[i])) i++;
        int addr = ParseInt(field.AsSpan(1, i - 1), "L address");

        int pos = addr;
        for (; i < field.Length; i++)
        {
            char c = field[i];
            if (c == '0' || c == '1')
            {
                if (pos >= fuses.Length)
                    throw new FormatException(
                        $"JEDEC L record overruns the fuse array at fuse {pos}.");
                fuses[pos++] = c == '1';
            }
            else if (!char.IsWhiteSpace(c))
            {
                throw new FormatException($"Unexpected character '{c}' in JEDEC L record.");
            }
        }
    }

    // The JESD3 fuse checksum: 16-bit sum of the fuse array packed LSB-first
    // into bytes (fuse 0 = bit 0 of byte 0).
    public static int FuseChecksum(bool[] fuses)
    {
        int sum = 0;
        for (int b = 0; b < fuses.Length; b += 8)
        {
            int by = 0;
            for (int bit = 0; bit < 8 && b + bit < fuses.Length; bit++)
                if (fuses[b + bit]) by |= 1 << bit;
            sum += by;
        }
        return sum & 0xFFFF;
    }

    private static int ParseInt(ReadOnlySpan<char> s, string what)
    {
        if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"Bad {what} in JEDEC file: '{s}'.");
        return v;
    }

    private static long ParseHex(ReadOnlySpan<char> s, string what)
    {
        if (!long.TryParse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v))
            throw new FormatException($"Bad {what} in JEDEC file: '{s}'.");
        return v;
    }
}