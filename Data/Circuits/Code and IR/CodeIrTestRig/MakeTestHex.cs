// ============================================================
//  MakeTestHex — emits U1_high.hex and U2_low.hex for the
//  Code & IR module test rig (one Intel HEX file per byte lane,
//  U1 = high bytes IR[15:8], U2 = low bytes IR[7:0]).
//
//  The pattern here MUST match ExpectedWord() in CodeIrTestRig.ino.
//
//  Build/run (any recent .NET SDK):
//      dotnet run
//  or compile MakeTestHex.cs in a plain console project.
// ============================================================

using System;
using System.IO;
using System.Text;

internal static class MakeTestHex
{
    private const int WordCount = 32768; // 28C256 = 32K x 8 per lane

    private static ushort ExpectedWord(int addr)
    {
        if (addr < 16)  return (ushort)(1 << addr);                 // walking one
        if (addr < 32)  return (ushort)~(1 << (addr - 16));         // walking zero
        if (addr == 32) return 0x0000;
        if (addr == 33) return 0xFFFF;
        if (addr == 34) return 0xAA55;
        if (addr == 35) return 0x55AA;
        return (ushort)((addr * 0x9E37) ^ 0x55AA);                  // address hash
    }

    private static void Main()
    {
        var high = new byte[WordCount];
        var low  = new byte[WordCount];

        for (int addr = 0; addr < WordCount; addr++)
        {
            ushort word = ExpectedWord(addr);
            high[addr] = (byte)(word >> 8);
            low[addr]  = (byte)(word & 0xFF);
        }

        File.WriteAllText("U1_high.hex", ToIntelHex(high));
        File.WriteAllText("U2_low.hex",  ToIntelHex(low));

        Console.WriteLine("Wrote U1_high.hex (IR[15:8]) and U2_low.hex (IR[7:0]).");
    }

    private static string ToIntelHex(byte[] data)
    {
        var sb = new StringBuilder();

        for (int baseAddr = 0; baseAddr < data.Length; baseAddr += 16)
        {
            int count = Math.Min(16, data.Length - baseAddr);
            int checksum = count + ((baseAddr >> 8) & 0xFF) + (baseAddr & 0xFF); // record type 00 adds nothing

            sb.Append(':');
            sb.Append(count.ToString("X2"));
            sb.Append(baseAddr.ToString("X4"));
            sb.Append("00");

            for (int i = 0; i < count; i++)
            {
                byte b = data[baseAddr + i];
                checksum += b;
                sb.Append(b.ToString("X2"));
            }

            sb.Append(((byte)(-checksum)).ToString("X2"));
            sb.Append('\n');
        }

        sb.Append(":00000001FF\n");
        return sb.ToString();
    }
}
