using System.Text;

namespace BlinkyMGen;

public static class IntelHex
{
    /// <summary>Standard Intel HEX (I8HEX), 16 data bytes per record, EOF record last.</summary>
    public static string Emit(byte[] image)
    {
        var sb = new StringBuilder(image.Length * 3);
        for (int addr = 0; addr < image.Length; addr += 16)
        {
            int count = Math.Min(16, image.Length - addr);
            int sum = count + ((addr >> 8) & 0xFF) + (addr & 0xFF); // record type 00
            sb.Append(':').Append(count.ToString("X2"))
              .Append(addr.ToString("X4")).Append("00");
            for (int i = 0; i < count; i++)
            {
                byte b = image[addr + i];
                sum += b;
                sb.Append(b.ToString("X2"));
            }
            sb.Append(((byte)(-sum)).ToString("X2")).Append("\r\n");
        }
        sb.Append(":00000001FF\r\n");
        return sb.ToString();
    }
}
