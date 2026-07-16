using System.Globalization;
using System.Text;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Addy.Assembler;

/// <summary>
/// Addy assembler (design doc v1).
///
/// Source (.asm) -> Intel HEX (.hex) + listing (.lst). Addy is word-addressed:
/// every instruction is exactly one 16-bit word, and ORG / labels / the PC all
/// count words. Three HEX images are written per source:
///
///     base.hex      combined, little-endian, byte address = word address * 2
///                   (loadable into TTLSim's 16-bit-wide EEPROM pair)
///     base_lo.hex   low  bytes, byte address = word address  } one file per
///     base_hi.hex   high bytes, byte address = word address  } physical 28C256
///
/// The opcode map comes from OpcodeTable.cs, which mirrors the design doc and
/// the two GAL .pld sources. Addy has no jump opcodes — any write to r7 is a
/// jump — so the assembler provides them as pseudo instructions:
///
///     NOP               MOV r0, r0                          (0x1000)
///     JMP aa            LDI r7, aa        absolute, page 0 only
///     JZ aa / JNZ aa    LDIZ/LDINZ r7, aa absolute, page 0 only
///     JR rs             MOV r7, rs        computed jump
///     BRA label         ADDI/SUBI r7      relative, +/-255 words, relocatable
///     BRZ / BRNZ label  ADDIZ../SUBINZ r7 conditional relative
///
/// A branch's offset is measured from the *next* word (r7 reads as PC+1 at
/// execute time), which the assembler handles; you just name the target.
///
/// Command line (input may be a file or a folder):
///     addyasm file.asm                 -> file.hex (+_lo/_hi), file.lst beside it
///     addyasm file.asm -o out          -> out\file.hex ...
///     addyasm file.asm -o out\name.hex -> that exact file (+ siblings)
///     addyasm folder                   -> every *.asm assembled beside itself
///     addyasm folder   -o out          -> every *.asm assembled into out\
/// </summary>
internal static class Program
{
    private const int MemWords = 0x8000;      // 32K words (QA[14:0] wired)
    private const int ResetEntry = 0x0000;    // PC after reset; default ORG

    // ====================================================================
    // ENCODING TABLES — machine ops from OpcodeTable, pseudos local.
    // ====================================================================

    private sealed record OpInfo(byte Op5, OperandShape Shape);

    private static readonly Dictionary<string, OpInfo> Ops = BuildOpTable();

    private static Dictionary<string, OpInfo> BuildOpTable()
    {
        var map = new Dictionary<string, OpInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (OpcodeEntry e in OpcodeTable.Entries)
            map[e.Mnemonic] = new OpInfo(e.Op5, e.Shape);
        return map;
    }

    // Pseudo instructions: expansion targets, all single-word.
    private static readonly HashSet<string> Pseudos =
        new(StringComparer.OrdinalIgnoreCase)
    { "NOP", "JMP", "JZ", "JNZ", "JR", "BRA", "BRZ", "BRNZ" };

    // Relative branches: (opcode for forward/+, opcode for backward/-).
    private static readonly Dictionary<string, (byte Fwd, byte Back)> Branches =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["BRA"]  = (0b01000, 0b01001),   // ADDI  / SUBI
        ["BRZ"]  = (0b10100, 0b10110),   // ADDIZ / SUBIZ
        ["BRNZ"] = (0b10101, 0b10111),   // ADDINZ/ SUBINZ
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

        bool outputIsFolder = folderInput
            || (output != null && (Directory.Exists(output) || LooksLikeFolder(output)));

        int failed = 0;
        foreach (string src in sources)
        {
            string hexPath;
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

            if (!AssembleOne(src, hexPath)) failed++;
        }

        if (sources.Count > 1)
            Console.WriteLine($"{sources.Count - failed}/{sources.Count} assembled" +
                              (failed > 0 ? $", {failed} failed" : ""));
        return failed > 0 ? 1 : 0;
    }

    private static bool AssembleOne(string src, string hexPath)
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

        string stem = Path.Combine(
            Path.GetDirectoryName(hexPath) ?? "",
            Path.GetFileNameWithoutExtension(hexPath));

        File.WriteAllText(hexPath, ToIntelHex(image, ByteImage.Combined));
        File.WriteAllText(stem + "_lo.hex", ToIntelHex(image, ByteImage.Low));
        File.WriteAllText(stem + "_hi.hex", ToIntelHex(image, ByteImage.High));
        File.WriteAllText(stem + ".lst", ToListing(image, symbols));

        int wordCount = image.Sum(c => c.Words.Length);
        Console.WriteLine($"{Path.GetFileName(src),-20} {wordCount,5} word(s) -> {hexPath}");
        return true;
    }

    private static bool LooksLikeFolder(string path)
    {
        char last = path.Length > 0 ? path[^1] : '\0';
        if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
            return true;
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
        public int Address;                       // word address
        public ushort[] Words = Array.Empty<ushort>();
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
        for (int i = 0; i < line.Length; i++)
            if (line[i] == ';') return line.Substring(0, i);
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
    // PASS 1 — addresses, labels, EQUs (all in words)
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
            if (address > MemWords)
            {
                errors.Add($"Line {line.Number}: address passed 0x{MemWords - 1:X4} (words).");
                address = MemWords;
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

        if (string.Equals(m, "DW", StringComparison.OrdinalIgnoreCase))
            return SplitOperands(line.Operand).Count;   // one word each

        if (Pseudos.Contains(m)) return 1;
        if (Ops.ContainsKey(m)) return 1;               // every instruction is one word

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
                                     line.Number, errors, labelsAllowed: false) & (MemWords - 1);
                continue;
            }

            ushort[] words;
            string comment = "";

            if (string.Equals(m, "DW", StringComparison.OrdinalIgnoreCase))
            {
                var data = new List<ushort>();
                foreach (string t in SplitOperands(line.Operand))
                    data.Add((ushort)EvalWord(m, t, symbols, line.Number, errors));
                if (data.Count == 0)
                    errors.Add($"Line {line.Number}: DW requires at least one value.");
                words = data.ToArray();
            }
            else if (Pseudos.Contains(m))
            {
                words = new[] { EncodePseudo(m, line, symbols, address, errors, ref comment) };
            }
            else if (Ops.TryGetValue(m, out OpInfo? info))
            {
                words = new[] { EncodeOp(m, info, line, symbols, errors) };
            }
            else
            {
                continue;   // unknown; reported in pass 1
            }

            for (int i = 0; i < words.Length; i++)
            {
                int a = address + i;
                if (a >= MemWords) break;
                if (occupied.TryGetValue(a, out int prior))
                    errors.Add($"Line {line.Number}: overwrites word 0x{a:X4} " +
                               $"(already emitted by line {prior}).");
                else
                    occupied[a] = line.Number;
            }

            image.Add(new EmittedChunk
            {
                Address = address,
                Words = words,
                Text = line.Operand == null ? m : $"{m} {line.Operand}",
                Comment = comment,
            });
            address = Math.Min(address + words.Length, MemWords);
        }

        return image;
    }

    private static ushort EncodeOp(string m, OpInfo info, SourceLine line,
        Dictionary<string, int> symbols, List<string> errors)
    {
        // HLT: all don't-care fields forced to 1 — matches erased EEPROM.
        if (info.Shape == OperandShape.None)
        {
            if (line.Operand != null)
                errors.Add($"Line {line.Number}: '{m}' takes no operand.");
            return string.Equals(m, "HLT", StringComparison.OrdinalIgnoreCase)
                ? OpcodeTable.HltWord
                : OpcodeTable.Word(info.Op5);
        }

        List<string> parts = SplitOperands(line.Operand);

        switch (info.Shape)
        {
            case OperandShape.RdRsRt:
                // 3 operands: rd, rs, rt.  2 operands: rd, rt with rs = rd
                // (the read-modify form: ADD r3, r1  =  r3 <- r3 + r1).
                if (parts.Count == 3)
                {
                    int rd = EvalReg(m, parts[0], line.Number, errors);
                    int rs = EvalReg(m, parts[1], line.Number, errors);
                    int rt = EvalReg(m, parts[2], line.Number, errors);
                    return OpcodeTable.Word(info.Op5, rd, rs, rt);
                }
                if (parts.Count == 2)
                {
                    int rd = EvalReg(m, parts[0], line.Number, errors);
                    int rt = EvalReg(m, parts[1], line.Number, errors);
                    return OpcodeTable.Word(info.Op5, rd, rd, rt);
                }
                errors.Add($"Line {line.Number}: '{m}' takes rd, rs, rt (or rd, rt).");
                return 0;

            case OperandShape.RdRs:
                if (parts.Count != 2) { Arity(m, "rd, rs", line, errors); return 0; }
                return OpcodeTable.Word(info.Op5,
                    EvalReg(m, parts[0], line.Number, errors),
                    EvalReg(m, parts[1], line.Number, errors));

            case OperandShape.RsRt:
                if (parts.Count != 2) { Arity(m, "rs, rt", line, errors); return 0; }
                return OpcodeTable.Word(info.Op5, 0,
                    EvalReg(m, parts[0], line.Number, errors),
                    EvalReg(m, parts[1], line.Number, errors));

            case OperandShape.Rs:
                if (parts.Count != 1) { Arity(m, "rs", line, errors); return 0; }
                return OpcodeTable.Word(info.Op5, 0,
                    EvalReg(m, parts[0], line.Number, errors));

            case OperandShape.Rd:
                if (parts.Count != 1) { Arity(m, "rd", line, errors); return 0; }
                return OpcodeTable.Word(info.Op5,
                    EvalReg(m, parts[0], line.Number, errors));

            case OperandShape.RdImm8:
                if (parts.Count != 2) { Arity(m, "rd, imm8", line, errors); return 0; }
                return OpcodeTable.Word(info.Op5,
                    EvalReg(m, parts[0], line.Number, errors),
                    imm8: EvalImm8(m, parts[1], symbols, line.Number, errors));

            default:
                errors.Add($"Line {line.Number}: '{m}' has no encoder (internal).");
                return 0;
        }
    }

    /// <summary>NOP, JR, and the jump/branch family — all sugar over r7.</summary>
    private static ushort EncodePseudo(string m, SourceLine line,
        Dictionary<string, int> symbols, int address, List<string> errors,
        ref string comment)
    {
        List<string> parts = SplitOperands(line.Operand);

        if (string.Equals(m, "NOP", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Operand != null)
                errors.Add($"Line {line.Number}: 'NOP' takes no operand.");
            return OpcodeTable.Word(0b00010);          // MOV r0, r0 = 0x1000
        }

        if (string.Equals(m, "JR", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count != 1) { Arity(m, "rs", line, errors); return 0; }
            int rs = EvalReg(m, parts[0], line.Number, errors);
            return OpcodeTable.Word(0b00010, 7, rs);   // MOV r7, rs
        }

        // Absolute jumps: LDI/LDIZ/LDINZ r7, aa — target must sit in page 0.
        if (m.Equals("JMP", StringComparison.OrdinalIgnoreCase) ||
            m.Equals("JZ", StringComparison.OrdinalIgnoreCase) ||
            m.Equals("JNZ", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count != 1) { Arity(m, "addr", line, errors); return 0; }
            int target = EvalNumber(m, parts[0], symbols, line.Number, errors,
                                    labelsAllowed: true);
            if (symbols.ContainsKey(Bare(parts[0]))) comment = $"{Bare(parts[0])} = {target:X4}";
            if (target < 0 || target > 0xFF)
                errors.Add($"Line {line.Number}: '{m}' target 0x{target:X4} is outside " +
                           "page 0 (0..0xFF); use BRA/BRZ/BRNZ (relative) or compose " +
                           "the address and JR.");
            byte op5 = m.Equals("JMP", StringComparison.OrdinalIgnoreCase) ? (byte)0b01010
                     : m.Equals("JZ", StringComparison.OrdinalIgnoreCase) ? (byte)0b10010
                     : (byte)0b10011;
            return OpcodeTable.Word(op5, 7, imm8: target & 0xFF);
        }

        // Relative branches: offset from the NEXT word (r7 reads as PC+1),
        // encoded as ADDIx r7 (forward) or SUBIx r7 (backward).
        if (Branches.TryGetValue(m, out (byte Fwd, byte Back) br))
        {
            if (parts.Count != 1) { Arity(m, "label", line, errors); return 0; }
            int target = EvalNumber(m, parts[0], symbols, line.Number, errors,
                                    labelsAllowed: true);
            if (symbols.ContainsKey(Bare(parts[0]))) comment = $"{Bare(parts[0])} = {target:X4}";

            int delta = target - (address + 1);
            if (delta < -255 || delta > 255)
            {
                errors.Add($"Line {line.Number}: '{m}' target is {delta} words away " +
                           "(range is -255..+255); compose the address and use JR/MOVZ/MOVNZ.");
                delta = 0;
            }
            return delta >= 0
                ? OpcodeTable.Word(br.Fwd, 7, imm8: delta)
                : OpcodeTable.Word(br.Back, 7, imm8: -delta);
        }

        errors.Add($"Line {line.Number}: '{m}' has no pseudo encoder (internal).");
        return 0;
    }

    private static void Arity(string m, string expected, SourceLine line, List<string> errors)
        => errors.Add($"Line {line.Number}: '{m}' takes {expected}.");

    private static string Bare(string token)
        => token.StartsWith("#") ? token.Substring(1).Trim() : token.Trim();

    // ====================================================================
    // OPERAND EVALUATION
    // ====================================================================

    private static List<string> SplitOperands(string? operand)
    {
        var result = new List<string>();
        if (operand == null) return result;
        foreach (string part in operand.Split(','))
            if (part.Trim().Length > 0) result.Add(part.Trim());
        return result;
    }

    /// <summary>r0..r7 (any case), or 'pc' as an alias for r7.</summary>
    private static int EvalReg(string context, string token,
        int lineNumber, List<string> errors)
    {
        string t = token.Trim();
        if (t.Equals("pc", StringComparison.OrdinalIgnoreCase)) return 7;
        if (t.Length == 2 && (t[0] == 'r' || t[0] == 'R') && t[1] >= '0' && t[1] <= '7')
            return t[1] - '0';
        errors.Add($"Line {lineNumber}: '{context}' expected a register " +
                   $"(r0..r7 or pc), got '{token}'.");
        return 0;
    }

    /// <summary>
    /// Immediate byte, 0..255 only. Addy zero-extends every immediate, so a
    /// negative value would silently mean +*(256+n); reject it and point at
    /// the subtracting mnemonic instead.
    /// </summary>
    private static int EvalImm8(string context, string token,
        Dictionary<string, int> symbols, int lineNumber, List<string> errors)
    {
        int v = EvalNumber(context, token, symbols, lineNumber, errors, labelsAllowed: true);
        if (v < 0)
            errors.Add($"Line {lineNumber}: '{token}' = {v}: immediates are " +
                       "zero-extended (no negatives); use SUBI/SUBIZ/SUBINZ or BRA.");
        else if (v > 255)
            errors.Add($"Line {lineNumber}: '{token}' = {v} is out of imm8 range " +
                       "(0..255); build 16-bit values with LDI + ADDIH.");
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

    private enum ByteImage { Combined, Low, High }

    private static string ToIntelHex(List<EmittedChunk> image, ByteImage kind)
    {
        var map = new SortedDictionary<int, byte>();
        foreach (EmittedChunk chunk in image)
        {
            for (int i = 0; i < chunk.Words.Length; i++)
            {
                int wordAddr = chunk.Address + i;
                ushort w = chunk.Words[i];
                switch (kind)
                {
                    case ByteImage.Combined:               // little-endian pairs
                        map[wordAddr * 2] = (byte)(w & 0xFF);
                        map[wordAddr * 2 + 1] = (byte)(w >> 8);
                        break;
                    case ByteImage.Low:                    // one 28C256 each:
                        map[wordAddr] = (byte)(w & 0xFF);  // byte addr = word addr
                        break;
                    case ByteImage.High:
                        map[wordAddr] = (byte)(w >> 8);
                        break;
                }
            }
        }

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
        sb.AppendLine("addr  code  source");
        sb.AppendLine("----  ----  ------");

        foreach (EmittedChunk chunk in image)
        {
            for (int i = 0; i < chunk.Words.Length; i++)
            {
                string source = i == 0
                    ? chunk.Text + (chunk.Comment.Length > 0 ? $"  ; {chunk.Comment}" : "")
                    : "";
                sb.AppendLine($"{chunk.Address + i:X4}  {chunk.Words[i]:X4}  {source}".TrimEnd());
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
@"Addy assembler (design doc v1)

Usage:
  addyasm <input> [-o <path>]

  <input>   an .asm file, or a folder (assembles every *.asm in it)
  -o <path> a folder (derives outputs) or, for a single file, an explicit
            .hex target. Omit to write beside each source.

Output (addresses are WORDS; one instruction = one 16-bit word):
  <base>.hex      combined image, little-endian (TTLSim EEPROM pair)
  <base>_lo.hex   low-byte  EEPROM image (byte addr = word addr)
  <base>_hi.hex   high-byte EEPROM image (byte addr = word addr)
  <base>.lst      address/code listing + symbol table

Syntax:
  ; comment to end of line
  label:            a name for the next word's address
  NAME EQU value    define a constant (numbers or earlier EQUs)
  ORG addr          set the assembly address in words (default 0 = reset)
  DW v, label, ...  emit 16-bit words
  MNEM operands     registers r0..r7 (pc = r7); numbers as below

Numbers: 12 (decimal), 0x0C / $0C (hex), 0b1100 / %1100 (binary), 'A' (char).
Immediates are 0..255 and ZERO-extended — no negatives; subtract instead.

Instructions (Addy v1; r7 is the PC — writing it jumps):
  Reg ALU: ADD SUB rd,rs,rt (or rd,rt)   MOV rd,rs   CMP CMN rs,rt   TST rs
  Imm ALU: ADDI SUBI LDI CMPI rd,imm8    ADDIH rd,imm8  (rd + imm<<8)
  Cond:    MOVZ MOVNZ rd,rs    LDIZ LDINZ ADDIZ ADDINZ SUBIZ SUBINZ rd,imm8
  I/O:     OUT rs   IN rd      HLT  (emits 0xFFFF = erased EEPROM)
Pseudo:
  NOP (MOV r0,r0)   JR rs (MOV r7,rs)
  JMP JZ JNZ addr   absolute, page 0 only
  BRA BRZ BRNZ label  relative +/-255 words, relocatable (offset from PC+1,
                      handled for you: just name the target)");
    }
}
