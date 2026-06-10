using System.Globalization;
using System.Text;

namespace TTLSim.Core;

/// <summary>
/// Minimal Intel HEX reader/writer for embedding EEPROM program images in a
/// schematic. Supports record type 00 (data) and 01 (EOF), plus 02/04 extended
/// address records so images are not silently misread; Blinky parts never reach
/// 64K, but honouring the base keeps the parser correct if they ever do.
///
/// Parse() returns a dense byte array spanning 0..highestAddress (gaps zero).
/// Write() emits 16-byte data records up to the last non-zero byte, then EOF.
/// </summary>
public static class IntelHex
{
    public static byte[] Parse(string text)
    {
        var bytes = new SortedDictionary<int, byte>();
        int baseAddr = 0;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] != ':')
                throw new FormatException($"Intel HEX line does not start with ':': {line}");

            byte[] rec = HexToBytes(line.AsSpan(1));
            if (rec.Length < 5)
                throw new FormatException($"Intel HEX record too short: {line}");

            int len = rec[0];
            int addr = (rec[1] << 8) | rec[2];
            int type = rec[3];
            if (rec.Length != len + 5)
                throw new FormatException($"Intel HEX length mismatch: {line}");

            byte sum = 0;
            foreach (byte b in rec) sum += b;
            if (sum != 0)
                throw new FormatException($"Intel HEX checksum error: {line}");

            switch (type)
            {
                case 0x00:                       // data
                    for (int i = 0; i < len; i++)
                        bytes[baseAddr + addr + i] = rec[4 + i];
                    break;
                case 0x01:                       // end of file
                    return Materialize(bytes);
                case 0x02:                       // extended segment address (<<4)
                    baseAddr = ((rec[4] << 8) | rec[5]) << 4;
                    break;
                case 0x04:                       // extended linear address (<<16)
                    baseAddr = ((rec[4] << 8) | rec[5]) << 16;
                    break;
                default:
                    break;                       // ignore unknown record types
            }
        }
        return Materialize(bytes);
    }

    public static string Write(byte[] data)
    {
        int end = data.Length;
        while (end > 0 && data[end - 1] == 0) end--;   // trim trailing zeros

        var sb = new StringBuilder();
        for (int offset = 0; offset < end; offset += 16)
        {
            int len = Math.Min(16, end - offset);
            var rec = new byte[len + 4];
            rec[0] = (byte)len;
            rec[1] = (byte)(offset >> 8);
            rec[2] = (byte)offset;
            rec[3] = 0x00;
            Array.Copy(data, offset, rec, 4, len);

            byte sum = 0;
            foreach (byte b in rec) sum += b;
            byte checksum = (byte)(-sum);

            sb.Append(':');
            foreach (byte b in rec) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append(checksum.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        sb.Append(":00000001FF\n");              // EOF record
        return sb.ToString();
    }

    private static byte[] Materialize(SortedDictionary<int, byte> bytes)
    {
        if (bytes.Count == 0) return Array.Empty<byte>();
        int max = 0;
        foreach (int a in bytes.Keys) max = a;     // sorted -> last is highest
        var result = new byte[max + 1];
        foreach (var kv in bytes) result[kv.Key] = kv.Value;
        return result;
    }

    private static byte[] HexToBytes(ReadOnlySpan<char> hex)
    {
        if ((hex.Length & 1) != 0)
            throw new FormatException("Intel HEX record has odd hex-digit count.");
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = byte.Parse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return result;
    }
}