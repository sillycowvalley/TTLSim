using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BlinkyM.Assembler;

/// <summary>
/// Blinky-M assembler.
///
/// Source (.asm) -> Intel HEX (.hex) + listing (.lst).
/// The .hex is byte-compatible with TTLSim's IntelHex.Parse: 16-byte type-00
/// data records, two's-complement checksum, type-01 EOF. Records carry real
/// 16-bit addresses; non-contiguous sections (multiple ORGs) start new records.
///
/// Instruction encoding per Blinky_M_Microcoded_CPU.md Appendix A:
/// byte-stream ISA, opcode byte = uROM row, 0/1/2 operand bytes.
/// 16-bit operands are little-endian. Reset entry is 0xE000 (the default ORG).
///
/// Dual-form I/O: "IN" / "OUT" with no operand assemble the stack forms
/// (0x37 / 0x38); with an operand they assemble the immediate-port forms
/// (0x35 / 0x36). STORE (a16) and STORE! (stack) are distinct mnemonics.
/// </summary>
internal static class Program
{
    private const int MemSize = 0x10000;     // 64K address space
    private const int ResetEntry = 0xE000;   // PC after reset; default ORG

    // ====================================================================
    // ENCODING TABLES (Blinky_M_Microcoded_CPU.md, Appendix A — ratified)
    //
    // Opcode = uROM row. High nibble = family, low nibble = member.
    // ALU members [3:2] are the shifter fill select and member bit 0 the
    // shift direction; branch members encode flag/polarity in IR[2:0].
    // Those placements are load-bearing wires — do not "tidy" these values.
    // ====================================================================

    // One-byte instructions: opcode only.
    private static readonly Dictionary<string, byte> NoOperandOps =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Control (0x0_)
        { "NOP", 0x00 }, { "RET", 0x01 }, { "BRK", 0x02 }, { "RTI", 0x03 },
        { "CLI", 0x04 }, { "SEI", 0x05 }, { "HALT", 0xFF },

        // ALU (0x1_)
        { "ADD", 0x10 }, { "ADC", 0x11 }, { "SUB", 0x12 }, { "XOR", 0x13 },
        { "NOT", 0x14 }, { "TST", 0x15 }, { "SHL", 0x16 }, { "SHR", 0x17 },
        { "AND", 0x18 }, { "CMP", 0x19 }, { "ASR", 0x1B }, { "OR",  0x1C },
        { "ROL", 0x1E }, { "ROR", 0x1F },

        // Stack (0x2_)
        { "DUP", 0x20 }, { "OVER", 0x21 }, { "TUCK", 0x22 }, { "SWAP", 0x23 },
        { "DROP", 0x24 }, { "NIP", 0x25 }, { "ROT", 0x26 },
        { ">R", 0x28 }, { "R>", 0x29 }, { "R@", 0x2A },

        // Memory / I-O stack forms (0x3_)
        { "FETCH", 0x33 }, { "STORE!", 0x34 },
        // IN / OUT stack forms are selected by operand absence (see Encode).
    };

    private const byte OpInStack = 0x37;
    private const byte OpOutStack = 0x38;
    private const byte OpInPort = 0x35;
    private const byte OpOutPort = 0x36;

    // Two-byte instructions: opcode + one operand byte.
    private static readonly Dictionary<string, byte> ByteOperandOps =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "PUSH", 0x30 },      // ii  immediate
        { "ENTER", 0x40 },     // kk  unsigned frame size
        { "LOCAL@", 0x41 },    // oo  signed offset from BP (wraps in page 0)
        { "LOCAL!", 0x42 },    // oo  signed offset from BP (wraps in page 0)
    };

    // Three-byte instructions: opcode + a16 little-endian.
    private static readonly Dictionary<string, byte> WordOperandOps =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "LOAD", 0x31 }, { "STORE", 0x32 },
        { "JUMP", 0x50 }, { "CALL", 0x51 },
        { "BEQ", 0x5A }, { "BNE", 0x5B },
        { "BCS", 0x5C }, { "BCC", 0x5D },
        { "BMI", 0x5E }, { "BPL", 0x5F },
    };

    // ====================================================================
    // ENTRY POINT
    // ====================================================================

    private static int Main(string[] args)
    {
        string? input = null;
        string? output = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (IsHelp(a)) { PrintUsage(); return 0; }
            if (a == "-o")
            {
                if (i + 1 >= args.Length) { Console.Error.WriteLine("-o needs a file name."); return 1; }
                output = args[++i];
            }
            else if (input == null) input = a;
            else { Console.Error.WriteLine($"Unexpected argument '{a}'."); return 1; }
        }

        if (input == null) { PrintUsage(); return 1; }
        if (!File.Exists(input)) { Console.Error.WriteLine($"File not found: {input}"); return 1; }

        output ??= Path.ChangeExtension(input, ".hex");
        string listing = Path.ChangeExtension(output, ".lst");

        var errors = new List<string>();
        List<SourceLine> lines = Parse(File.ReadAllLines(input), errors);
        Dictionary<string, int> symbols = ResolveSymbols(lines, errors);
        List<EmittedChunk> image = Encode(lines, symbols, errors);

        if (errors.Count > 0)
        {
            foreach (string e in errors) Console.Error.WriteLine(e);
            Console.Error.WriteLine($"{errors.Count} error(s); nothing written.");
            return 1;
        }

        File.WriteAllText(output, ToIntelHex(image));
        File.WriteAllText(listing, ToListing(image, symbols));
        int byteCount = image.Sum(c => c.Bytes.Length);
        Console.WriteLine($"{byteCount} byte(s) -> {output}");
        Console.WriteLine($"listing   -> {listing}");
        return 0;
    }

    // ====================================================================
    // DATA MODEL
    // ====================================================================

    private sealed class SourceLine
    {
        public int Number;
        public string? Label;        // "name:" before the mnemonic
        public string? EquName;      // "name EQU value" form
        public string? Mnemonic;     // instruction or directive
        public string? Operand;      // remainder of the line (raw, trimmed)
        public string Raw = "";      // original text for the listing
    }

    private sealed class EmittedChunk
    {
        public int Address;
        public byte[] Bytes = Array.Empty<byte>();
        public string Text = "";     // source text for the listing
        public string Comment = "";  // resolved-label annotation
    }

    // ====================================================================
    // PARSE — split each line into label / mnemonic / operand
    // ====================================================================

    private static List<SourceLine> Parse(string[] raw, List<string> errors)
    {
        var result = new List<SourceLine>();

        for (int i = 0; i < raw.Length; i++)
        {
            var line = new SourceLine { Number = i + 1, Raw = raw[i] };
            string text = StripComment(raw[i]).Trim();

            // name EQU value  (no colon on the name)
            string[] equParts = text.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (equParts.Length == 3 &&
                string.Equals(equParts[1], "EQU", StringComparison.OrdinalIgnoreCase) &&
                IsIdentifier(equParts[0]))
            {
                line.EquName = equParts[0];
                line.Operand = equParts[2].Trim();
                result.Add(line);
                continue;
            }

            // label:
            int colon = text.IndexOf(':');
            if (colon > 0 && IsIdentifier(text.Substring(0, colon)))
            {
                line.Label = text.Substring(0, colon);
                text = text.Substring(colon + 1).Trim();
            }

            if (text.Length > 0)
            {
                string[] parts = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                line.Mnemonic = parts[0];
                if (parts.Length == 2) line.Operand = parts[1].Trim();
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>Strip "; comment", respecting double-quoted strings (DB "a;b").</summary>
    private static string StripComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (line[i] == ';' && !inString) return line.Substring(0, i);
        }
        return line;
    }

    private static bool IsIdentifier(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return false;
        char first = s[0];
        if (!(char.IsLetter(first) || first == '.')) return false;
        foreach (char c in s)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) return false;
        return true;
    }

    // ====================================================================
    // PASS 1 — assign addresses, collect labels and EQUs
    // ====================================================================

    private static Dictionary<string, int> ResolveSymbols(
        List<SourceLine> lines, List<string> errors)
    {
        var symbols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int address = ResetEntry;

        foreach (SourceLine line in lines)
        {
            if (line.EquName != null)
            {
                // EQUs are evaluated in order; they may reference earlier EQUs
                // (but not labels, whose values pass 1 is still discovering).
                int value = EvalNumber(line.EquName, line.Operand ?? "", symbols,
                                       line.Number, errors, labelsAllowed: false);
                Define(symbols, line.EquName, value, line.Number, errors);
                continue;
            }

            if (line.Label != null)
                Define(symbols, line.Label, address, line.Number, errors);

            if (line.Mnemonic == null) continue;
            string m = line.Mnemonic;

            if (string.Equals(m, "ORG", StringComparison.OrdinalIgnoreCase))
            {
                address = EvalNumber("ORG", line.Operand ?? "", symbols,
                                     line.Number, errors, labelsAllowed: false);
                continue;
            }

            address += SizeOf(line, errors);
            if (address > MemSize)
            {
                errors.Add($"Line {line.Number}: address passed 0xFFFF.");
                address = MemSize;
            }
        }

        return symbols;
    }

    private static void Define(Dictionary<string, int> symbols, string name,
        int value, int lineNumber, List<string> errors)
    {
        if (symbols.ContainsKey(name))
            errors.Add($"Line {lineNumber}: duplicate symbol '{name}'.");
        else
            symbols[name] = value;
    }

    /// <summary>Instruction/directive size in bytes. Sizes never depend on
    /// operand values, so pass 1 needs no label knowledge.</summary>
    private static int SizeOf(SourceLine line, List<string> errors)
    {
        string m = line.Mnemonic!;

        if (string.Equals(m, "DB", StringComparison.OrdinalIgnoreCase))
            return SplitOperands(line.Operand)
                .Sum(t => t.Length >= 2 && t[0] == '"' ? t.Length - 2 : 1);

        if (string.Equals(m, "DW", StringComparison.OrdinalIgnoreCase))
            return SplitOperands(line.Operand).Count * 2;

        // IN / OUT: form (and size) selected by operand presence.
        if (string.Equals(m, "IN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m, "OUT", StringComparison.OrdinalIgnoreCase))
            return line.Operand == null ? 1 : 2;

        if (NoOperandOps.ContainsKey(m)) return 1;
        if (ByteOperandOps.ContainsKey(m)) return 2;
        if (WordOperandOps.ContainsKey(m)) return 3;

        errors.Add($"Line {line.Number}: unknown mnemonic '{m}'.");
        return 0;
    }

    // ====================================================================
    // PASS 2 — encode
    // ====================================================================

    private static List<EmittedChunk> Encode(
        List<SourceLine> lines, Dictionary<string, int> symbols, List<string> errors)
    {
        var image = new List<EmittedChunk>();
        var occupied = new Dictionary<int, int>();   // address -> line number
        int address = ResetEntry;

        foreach (SourceLine line in lines)
        {
            if (line.EquName != null || line.Mnemonic == null) continue;
            string m = line.Mnemonic;

            if (string.Equals(m, "ORG", StringComparison.OrdinalIgnoreCase))
            {
                address = EvalNumber("ORG", line.Operand ?? "", symbols,
                                     line.Number, errors, labelsAllowed: false) & 0xFFFF;
                continue;
            }

            byte[] bytes;
            string comment = "";

            if (string.Equals(m, "DB", StringComparison.OrdinalIgnoreCase))
            {
                var data = new List<byte>();
                foreach (string t in SplitOperands(line.Operand))
                {
                    if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
                        data.AddRange(Encoding.ASCII.GetBytes(t.Substring(1, t.Length - 2)));
                    else
                        data.Add((byte)(EvalByte(m, t, symbols, line.Number, errors) & 0xFF));
                }
                if (data.Count == 0)
                    errors.Add($"Line {line.Number}: DB requires at least one value.");
                bytes = data.ToArray();
            }
            else if (string.Equals(m, "DW", StringComparison.OrdinalIgnoreCase))
            {
                var data = new List<byte>();
                foreach (string t in SplitOperands(line.Operand))
                {
                    int w = EvalWord(m, t, symbols, line.Number, errors);
                    data.Add((byte)(w & 0xFF));
                    data.Add((byte)((w >> 8) & 0xFF));
                }
                if (data.Count == 0)
                    errors.Add($"Line {line.Number}: DW requires at least one value.");
                bytes = data.ToArray();
            }
            else if (string.Equals(m, "IN", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(m, "OUT", StringComparison.OrdinalIgnoreCase))
            {
                bool isIn = string.Equals(m, "IN", StringComparison.OrdinalIgnoreCase);
                if (line.Operand == null)
                {
                    // Stack form: port from TOS.
                    bytes = new[] { isIn ? OpInStack : OpOutStack };
                }
                else
                {
                    int port = EvalByte(m, line.Operand, symbols, line.Number, errors);
                    bytes = new[] { isIn ? OpInPort : OpOutPort, (byte)(port & 0xFF) };
                }
            }
            else if (NoOperandOps.TryGetValue(m, out byte op1))
            {
                if (line.Operand != null)
                    errors.Add($"Line {line.Number}: '{m}' takes no operand.");
                bytes = new[] { op1 };
            }
            else if (ByteOperandOps.TryGetValue(m, out byte op2))
            {
                int v = RequireOperand(line, errors, out string? tok)
                    ? EvalByte(m, tok!, symbols, line.Number, errors) : 0;
                bytes = new[] { op2, (byte)(v & 0xFF) };
            }
            else if (WordOperandOps.TryGetValue(m, out byte op3))
            {
                int v = 0;
                if (RequireOperand(line, errors, out string? tok))
                {
                    v = EvalWord(m, tok!, symbols, line.Number, errors);
                    string bare = tok!.StartsWith("#") ? tok.Substring(1) : tok;
                    if (symbols.ContainsKey(bare)) comment = $"{bare} = {v:X4}";
                }
                bytes = new[] { op3, (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) };
            }
            else
            {
                // Unknown mnemonic — already reported in pass 1.
                continue;
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                int a = address + i;
                if (a >= MemSize) break;   // range error already reported in pass 1
                if (occupied.TryGetValue(a, out int prior))
                    errors.Add($"Line {line.Number}: overwrites 0x{a:X4} " +
                               $"(already emitted by line {prior}).");
                else
                    occupied[a] = line.Number;
            }

            image.Add(new EmittedChunk
            {
                Address = address,
                Bytes = bytes,
                Text = line.Operand == null ? m : $"{m} {line.Operand}",
                Comment = comment,
            });
            address = Math.Min(address + bytes.Length, MemSize);
        }

        return image;
    }

    private static bool RequireOperand(SourceLine line, List<string> errors, out string? token)
    {
        token = line.Operand;
        if (token != null) return true;
        errors.Add($"Line {line.Number}: '{line.Mnemonic}' requires an operand.");
        return false;
    }

    // ====================================================================
    // OPERAND EVALUATION
    // ====================================================================

    /// <summary>Split a DB/DW operand list on commas, respecting "strings".</summary>
    private static List<string> SplitOperands(string? operand)
    {
        var result = new List<string>();
        if (operand == null) return result;

        var current = new StringBuilder();
        bool inString = false;
        foreach (char c in operand)
        {
            if (c == '"') inString = !inString;
            if (c == ',' && !inString)
            {
                if (current.ToString().Trim().Length > 0) result.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.ToString().Trim().Length > 0) result.Add(current.ToString().Trim());
        return result;
    }

    /// <summary>Byte operand: -128..255, wrapped to 8 bits (LOCAL offsets are
    /// signed and page-0 arithmetic wraps, so both views are legal).</summary>
    private static int EvalByte(string context, string token,
        Dictionary<string, int> symbols, int lineNumber, List<string> errors)
    {
        int v = EvalNumber(context, token, symbols, lineNumber, errors, labelsAllowed: true);
        if (v < -128 || v > 255)
            errors.Add($"Line {lineNumber}: '{token}' = {v} is out of byte range (-128..255).");
        return v & 0xFF;
    }

    /// <summary>Word operand: 0..0xFFFF (addresses, DW values).</summary>
    private static int EvalWord(string context, string token,
        Dictionary<string, int> symbols, int lineNumber, List<string> errors)
    {
        int v = EvalNumber(context, token, symbols, lineNumber, errors, labelsAllowed: true);
        if (v < 0 || v > 0xFFFF)
            errors.Add($"Line {lineNumber}: '{token}' = {v} is out of range (0..0xFFFF).");
        return v & 0xFFFF;
    }

    private static int EvalNumber(string context, string token,
        Dictionary<string, int> symbols, int lineNumber, List<string> errors,
        bool labelsAllowed)
    {
        string t = token.Trim();
        if (t.StartsWith("#")) t = t.Substring(1);

        if (t.Length == 0)
        {
            errors.Add($"Line {lineNumber}: '{context}' has an empty operand.");
            return 0;
        }

        // 'c' character literal
        if (t.Length == 3 && t[0] == '\'' && t[2] == '\'')
            return t[1];

        if (symbols.TryGetValue(t, out int symbolValue))
        {
            if (!labelsAllowed)
            {
                // ORG / EQU operands are evaluated during pass 1, when label
                // addresses are still being assigned; only EQUs already defined
                // above this line are in the table, so a hit here is safe.
            }
            return symbolValue;
        }

        try
        {
            bool negative = t.StartsWith("-");
            string u = negative ? t.Substring(1) : t;
            int value;

            if (u.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToInt32(u.Substring(2), 16);
            else if (u.StartsWith("$"))
                value = Convert.ToInt32(u.Substring(1), 16);
            else if (u.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToInt32(u.Substring(2), 2);
            else if (u.StartsWith("%"))
                value = Convert.ToInt32(u.Substring(1), 2);
            else
                value = int.Parse(u, CultureInfo.InvariantCulture);

            return negative ? -value : value;
        }
        catch
        {
            errors.Add(labelsAllowed
                ? $"Line {lineNumber}: cannot parse operand '{token}' (unknown symbol or number)."
                : $"Line {lineNumber}: '{context}' operand '{token}' must be a number " +
                  "or an EQU defined above this line.");
            return 0;
        }
    }

    // ====================================================================
    // OUTPUT — Intel HEX and listing
    // ====================================================================

    private static string ToIntelHex(List<EmittedChunk> image)
    {
        // Flatten to a sorted sparse byte map, then emit 16-byte records,
        // breaking on address discontinuities (ORG gaps).
        var map = new SortedDictionary<int, byte>();
        foreach (EmittedChunk chunk in image)
            for (int i = 0; i < chunk.Bytes.Length; i++)
                map[chunk.Address + i] = chunk.Bytes[i];

        var sb = new StringBuilder();
        var record = new List<byte>();
        int recordStart = 0;
        int expected = -1;

        void Flush()
        {
            if (record.Count == 0) return;
            int sum = record.Count + ((recordStart >> 8) & 0xFF) + (recordStart & 0xFF);
            sb.Append($":{record.Count:X2}{recordStart:X4}00");
            foreach (byte b in record) { sb.Append($"{b:X2}"); sum += b; }
            sb.Append($"{(256 - (sum & 0xFF)) & 0xFF:X2}\n");
            record.Clear();
        }

        foreach (KeyValuePair<int, byte> pair in map)
        {
            if (pair.Key != expected || record.Count == 16)
            {
                Flush();
                recordStart = pair.Key;
            }
            record.Add(pair.Value);
            expected = pair.Key + 1;
        }
        Flush();

        sb.Append(":00000001FF\n");
        return sb.ToString();
    }

    private static string ToListing(List<EmittedChunk> image, Dictionary<string, int> symbols)
    {
        var sb = new StringBuilder();
        sb.AppendLine("addr  bytes     source");
        sb.AppendLine("----  --------  ------");

        foreach (EmittedChunk chunk in image)
        {
            // Long DB/DW payloads wrap at 3 bytes per listing row.
            for (int i = 0; i < chunk.Bytes.Length; i += 3)
            {
                string hex = string.Join(" ",
                    chunk.Bytes.Skip(i).Take(3).Select(b => b.ToString("X2")));
                string source = i == 0
                    ? chunk.Text + (chunk.Comment.Length > 0 ? $"  ; {chunk.Comment}" : "")
                    : "";
                sb.AppendLine($"{chunk.Address + i:X4}  {hex,-8}  {source}".TrimEnd());
            }
        }

        var labels = symbols.OrderBy(p => p.Value).ToList();
        if (labels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("symbols");
            sb.AppendLine("-------");
            foreach (KeyValuePair<string, int> pair in labels)
                sb.AppendLine($"{pair.Value:X4}  {pair.Key}");
        }

        return sb.ToString();
    }

    // ====================================================================
    // USAGE
    // ====================================================================

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "/?" or "-?";

    private static void PrintUsage()
    {
        Console.WriteLine(
@"Blinky-M assembler

Usage:
  bmasm <input.asm> [-o <output.hex>]

Output:
  <input>.hex   Intel HEX (real 16-bit addresses; loadable into TTLSim)
  <input>.lst   address/code listing + symbol table

Syntax:
  ; comment to end of line
  label:            a name for the next byte's address
  NAME EQU value    define a constant (numbers or earlier EQUs)
  ORG addr          set the assembly address (default 0xE000 = reset entry)
  DB v, ""text"", ...  emit bytes / ASCII
  DW v, label, ...    emit 16-bit words, little-endian
  MNEM [operand]    operand is a number, 'c', label, or EQU; # prefix optional

Numbers: 12 (decimal), 0x0C / $0C (hex), 0b1100 / %1100 (binary), 'A' (char).
Byte operands accept -128..255; addresses 0..0xFFFF, little-endian.

Instructions (Blinky-M, Appendix A):
  Control: NOP RET BRK RTI CLI SEI HALT
  ALU:     ADD ADC SUB AND OR XOR NOT TST CMP SHL SHR ASR ROL ROR
  Stack:   DUP OVER TUCK SWAP DROP NIP ROT >R R> R@
  Mem/IO:  PUSH #ii  LOAD aaaa  STORE aaaa  FETCH  STORE!
           IN #pp / IN (port from TOS)   OUT #pp / OUT (port from TOS)
  Frame:   ENTER kk  LOCAL@ oo  LOCAL! oo
  Flow:    JUMP aaaa  CALL aaaa  BEQ BNE BCS BCC BMI BPL aaaa");
    }
}