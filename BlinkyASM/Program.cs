using System.Globalization;
using System.Text;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BlinkyM.Assembler;

/// <summary>
/// Blinky-M assembler (rev 14).
///
/// Source (.asm) -> Intel HEX (.hex) + listing (.lst). The .hex is
/// byte-compatible with TTLSim's IntelHex.Parse: 16-byte type-00 data records,
/// two's-complement checksum, type-01 EOF. Records carry real 16-bit addresses;
/// non-contiguous sections (multiple ORGs) start new records.
///
/// The opcode map comes entirely from the generated OpcodeTable.cs, so the
/// assembler can never drift from the control store. The ALU quadrant is
/// reached by name (ADD, SUB, ...) or by the general form:
///     ALU #n            n = raw 6-bit '181 function (M CN S3 S2 S1 S0)
///     ALU m, cn, ssss   the same, as (M, CN, S3..S0) fields
///
/// Command line (input may be a file or a folder):
///     bmasm file.asm                 -> file.hex, file.lst beside it
///     bmasm file.asm -o out          -> out\file.hex, out\file.lst
///     bmasm file.asm -o out\name.hex -> that exact file (+ name.lst)
///     bmasm folder                   -> every *.asm assembled beside itself
///     bmasm folder   -o out          -> every *.asm assembled into out\
/// </summary>
internal static class Program
{
    private const int MemSize = 0x10000;      // 64K address space
    private const int ResetEntry = 0xE000;    // PC after reset; default ORG

    // ====================================================================
    // ENCODING TABLES — built from the generated OpcodeTable (rev 14).
    // ====================================================================

    private sealed record OpInfo(byte Opcode, OperandShape Shape, int Length);

    private static readonly Dictionary<string, OpInfo> Ops =
        BuildOpTable();

    private static Dictionary<string, OpInfo> BuildOpTable()
    {
        var map = new Dictionary<string, OpInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (OpcodeEntry e in OpcodeTable.Entries)
            map[e.Mnemonic] = new OpInfo(e.Opcode, e.Shape, e.Length);
        return map;
    }

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
            if (a is "-o" or "-d" or "--out")
            {
                if (i + 1 >= args.Length) { Console.Error.WriteLine($"{a} needs a path."); return 1; }
                output = args[++i];
            }
            else if (input == null) input = a;
            else { Console.Error.WriteLine($"Unexpected argument '{a}'."); return 1; }
        }

        if (input == null) { PrintUsage(); return 1; }

        // Resolve the work list: one file, or every *.asm in a folder.
        List<string> sources;
        bool folderInput = Directory.Exists(input);
        if (folderInput)
        {
            sources = Directory.EnumerateFiles(input, "*.asm", SearchOption.TopDirectoryOnly)
                               .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                               .ToList();
            if (sources.Count == 0)
            {
                Console.Error.WriteLine($"No .asm files in folder: {input}");
                return 1;
            }
        }
        else if (File.Exists(input))
        {
            sources = new List<string> { input };
        }
        else
        {
            Console.Error.WriteLine($"Not found: {input}");
            return 1;
        }

        // An -o that is a folder (or that must be one, because the input was a
        // folder) sends derived <base>.hex/.lst there; an -o that is a file path
        // is honoured only for a single-file input.
        bool outputIsFolder = folderInput
            || (output != null && (Directory.Exists(output) || LooksLikeFolder(output)));

        int failed = 0;
        foreach (string src in sources)
        {
            string hexPath, lstPath;
            if (output == null)
            {
                hexPath = Path.ChangeExtension(src, ".hex");
            }
            else if (outputIsFolder)
            {
                Directory.CreateDirectory(output);
                string baseName = Path.GetFileNameWithoutExtension(src);
                hexPath = Path.Combine(output, baseName + ".hex");
            }
            else
            {
                hexPath = output;   // explicit single-file target
            }
            lstPath = Path.ChangeExtension(hexPath, ".lst");

            if (!AssembleOne(src, hexPath, lstPath)) failed++;
        }

        if (sources.Count > 1)
            Console.WriteLine($"{sources.Count - failed}/{sources.Count} assembled" +
                              (failed > 0 ? $", {failed} failed" : ""));
        return failed > 0 ? 1 : 0;
    }

    private static bool AssembleOne(string src, string hexPath, string lstPath)
    {
        var errors = new List<string>();
        List<SourceLine> lines = Parse(File.ReadAllLines(src), errors);
        Dictionary<string, int> symbols = ResolveSymbols(lines, errors);
        List<EmittedChunk> image = Encode(lines, symbols, errors);

        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"--- {Path.GetFileName(src)} ---");
            foreach (string e in errors) Console.Error.WriteLine(e);
            Console.Error.WriteLine($"{errors.Count} error(s); nothing written.");
            return false;
        }

        File.WriteAllText(hexPath, ToIntelHex(image));
        File.WriteAllText(lstPath, ToListing(image, symbols));
        int byteCount = image.Sum(c => c.Bytes.Length);
        Console.WriteLine($"{Path.GetFileName(src),-20} {byteCount,5} byte(s) -> {hexPath}");
        return true;
    }

    private static bool LooksLikeFolder(string path)
    {
        char last = path.Length > 0 ? path[^1] : '\0';
        if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
            return true;
        // No extension and not obviously a .hex target -> treat as folder.
        return string.IsNullOrEmpty(Path.GetExtension(path));
    }

    // ====================================================================
    // DATA MODEL
    // ====================================================================

    private sealed class SourceLine
    {
        public int Number;
        public string? Label;
        public string? EquName;
        public string? Mnemonic;
        public string? Operand;
        public string Raw = "";
    }

    private sealed class EmittedChunk
    {
        public int Address;
        public byte[] Bytes = Array.Empty<byte>();
        public string Text = "";
        public string Comment = "";
    }

    // ====================================================================
    // PARSE
    // ====================================================================

    private static List<SourceLine> Parse(string[] raw, List<string> errors)
    {
        var result = new List<SourceLine>();

        for (int i = 0; i < raw.Length; i++)
        {
            var line = new SourceLine { Number = i + 1, Raw = raw[i] };
            string text = StripComment(raw[i]).Trim();

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
    // PASS 1 — addresses, labels, EQUs
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

    private static int SizeOf(SourceLine line, List<string> errors)
    {
        string m = line.Mnemonic!;

        if (string.Equals(m, "DB", StringComparison.OrdinalIgnoreCase))
            return SplitOperands(line.Operand)
                .Sum(t => t.Length >= 2 && t[0] == '"' ? t.Length - 2 : 1);

        if (string.Equals(m, "DW", StringComparison.OrdinalIgnoreCase))
            return SplitOperands(line.Operand).Count * 2;

        if (string.Equals(m, "ALU", StringComparison.OrdinalIgnoreCase))
            return 1;   // function rides the opcode; one byte

        if (Ops.TryGetValue(m, out OpInfo? info))
            return info.Length;

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
        var occupied = new Dictionary<int, int>();
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
            else if (string.Equals(m, "ALU", StringComparison.OrdinalIgnoreCase))
            {
                bytes = new[] { EncodeAlu(line, symbols, errors) };
            }
            else if (Ops.TryGetValue(m, out OpInfo? info))
            {
                bytes = EncodeOp(m, info, line, symbols, errors, ref comment);
            }
            else
            {
                continue;   // unknown; reported in pass 1
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                int a = address + i;
                if (a >= MemSize) break;
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

    private static byte[] EncodeOp(string m, OpInfo info, SourceLine line,
        Dictionary<string, int> symbols, List<string> errors, ref string comment)
    {
        switch (info.Shape)
        {
            case OperandShape.None:
                if (line.Operand != null)
                    errors.Add($"Line {line.Number}: '{m}' takes no operand.");
                return new[] { info.Opcode };

            case OperandShape.Addr16:
            {
                int v = 0;
                if (RequireOperand(line, errors, out string? tok))
                {
                    v = EvalWord(m, tok!, symbols, line.Number, errors);
                    string bare = tok!.StartsWith("#") ? tok.Substring(1) : tok;
                    if (symbols.ContainsKey(bare)) comment = $"{bare} = {v:X4}";
                }
                return new[] { info.Opcode, (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) };
            }

            default:   // Imm8, Port8, Count8, Offset8
            {
                int v = RequireOperand(line, errors, out string? tok)
                    ? EvalByte(m, tok!, symbols, line.Number, errors) : 0;
                return new[] { info.Opcode, (byte)(v & 0xFF) };
            }
        }
    }

    /// <summary>ALU #n (raw 6-bit function) or ALU m, cn, ssss (fields).</summary>
    private static byte EncodeAlu(SourceLine line,
        Dictionary<string, int> symbols, List<string> errors)
    {
        if (!RequireOperand(line, errors, out string? operand))
            return OpcodeTable.AluQuadrantBase;

        List<string> parts = SplitOperands(operand);
        if (parts.Count == 1)
        {
            int fn = EvalNumber("ALU", parts[0], symbols, line.Number, errors, labelsAllowed: true);
            if (fn < 0 || fn > 0x3F)
                errors.Add($"Line {line.Number}: ALU #{parts[0]} = {fn} out of range (0..0x3F).");
            return OpcodeTable.AluOpcodeRaw(fn & 0x3F);
        }
        if (parts.Count == 3)
        {
            int mBit = EvalNumber("ALU M", parts[0], symbols, line.Number, errors, labelsAllowed: true);
            int cn = EvalNumber("ALU CN", parts[1], symbols, line.Number, errors, labelsAllowed: true);
            int s = EvalNumber("ALU S", parts[2], symbols, line.Number, errors, labelsAllowed: true);
            if ((mBit & ~1) != 0 || (cn & ~1) != 0 || (s & ~0xF) != 0)
                errors.Add($"Line {line.Number}: ALU fields must be M=0/1, CN=0/1, S=0..15.");
            return OpcodeTable.AluOpcode(mBit != 0, cn != 0, s & 0xF);
        }

        errors.Add($"Line {line.Number}: ALU takes '#n' or 'M, CN, Ssss'.");
        return OpcodeTable.AluQuadrantBase;
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

    private static List<string> SplitOperands(string? operand)
    {
        var result = new List<string>();
        if (operand == null) return result;

        var current = new StringBuilder();
        bool inString = false;
        foreach (char c in operand)
        {
            if (c == '"') { inString = !inString; current.Append(c); }
            else if (c == ',' && !inString)
            {
                if (current.ToString().Trim().Length > 0) result.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.ToString().Trim().Length > 0) result.Add(current.ToString().Trim());
        return result;
    }

    private static int EvalByte(string context, string token,
        Dictionary<string, int> symbols, int lineNumber, List<string> errors)
    {
        int v = EvalNumber(context, token, symbols, lineNumber, errors, labelsAllowed: true);
        if (v < -128 || v > 255)
            errors.Add($"Line {lineNumber}: '{token}' = {v} is out of byte range (-128..255).");
        return v & 0xFF;
    }

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

        if (t.Length == 3 && t[0] == '\'' && t[2] == '\'')
            return t[1];

        if (symbols.TryGetValue(t, out int symbolValue))
            return symbolValue;

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
    // OUTPUT
    // ====================================================================

    private static string ToIntelHex(List<EmittedChunk> image)
    {
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

    private static bool IsHelp(string a) => a is "-h" or "--help" or "/?" or "-?";

    private static void PrintUsage()
    {
        Console.WriteLine(
@"Blinky-M assembler (rev 14)

Usage:
  bmasm <input> [-o <path>]

  <input>   an .asm file, or a folder (assembles every *.asm in it)
  -o <path> a folder (derives <base>.hex/.lst) or, for a single file,
            an explicit .hex target. Omit to write beside each source.

Output:
  <base>.hex   Intel HEX (real 16-bit addresses; loadable into TTLSim)
  <base>.lst   address/code listing + symbol table

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

Instructions (rev 14, Appendix A):
  Control: NOP RET BRK RTI CLI SEI HALT
  ALU:     ADD ADC SUB AND OR XOR NOT TST CMP   (named)
           ALU #n   or   ALU m, cn, ssss        (whole '181 quadrant)
  Shift:   SHL SHR ASR ROL ROR
  Stack:   DUP OVER TUCK SWAP DROP NIP ROT >R R> R@
  Mem/IO:  PUSH #ii  LOAD aaaa  STORE aaaa  FETCH  STORE!
           IN #pp  OUT #pp   (immediate port)   INS  OUTS  (port from TOS)
  Frame:   ENTER kk  LOCAL@ oo  LOCAL! oo
  Flow:    JUMP aaaa  CALL aaaa  BEQ BNE BCS BCC BMI BPL aaaa");
    }
}
