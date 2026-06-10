using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MiniBlinky.Assembler;

/// <summary>
/// Mini Blinky (W = 4) assembler.
///
/// Source (.asm) -> Intel HEX (.hex) + listing (.lst).
/// The .hex is byte-compatible with TTLSim's IntelHex.Parse: 16-byte type-00
/// data records from address 0, two's-complement checksum, type-01 EOF.
/// One 8-bit instruction word per I-memory cell, 16 cells (addresses 0..15).
///
/// Instruction word = (opcode &lt;&lt; 4) | literal, per Mini_Blinky_CPU.md.
/// </summary>
internal static class Program
{
    // ---- Top-level opcodes (Mini_Blinky_CPU.md "Top-Level Opcode Map") ----
    private const int OpSys = 0;
    private const int OpAlu = 1;
    private const int OpPush = 2;
    private const int OpLoad = 3;
    private const int OpStore = 4;
    private const int OpIn = 5;
    private const int OpOut = 6;
    private const int OpBranch = 7;
    private const int OpBeq = 8;
    private const int OpBcs = 9;
    private const int OpBmi = 10;
    private const int OpCall = 11;

    private const int IMemSize = 16;   // 16 instruction words at W = 4
    private const int LitMax = 15;     // 4-bit literal (W = 4)

    // ====================================================================
    // ENCODING TABLES
    //
    // The top-level opcodes (above) are fixed by the design docs. The SYS and
    // ALU sub-op NIBBLE values below are NOT pinned by Mini_Blinky_CPU.md or
    // Mini_Blinky_Control_Signals.md -- the docs leave them to the decode GAL.
    // These assignments follow the order the sub-ops are listed in those docs.
    // If the GAL ends up using different nibbles, edit ONLY these two tables.
    // ====================================================================

    // SYS family: opcode 0, literal = sub-op nibble.
    private static readonly Dictionary<string, int> SysSubOp =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "NOP", 0 }, { "HALT", 1 }, { "RET", 2 }, { "DUP", 3 },
        { "DROP", 4 }, { "SWAP", 5 }, { ">R", 6 }, { "R>", 7 },
    };

    // ALU family: opcode 1, literal = op-select nibble.
    private static readonly Dictionary<string, int> AluSubOp =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "ADD", 0 }, { "ADC", 1 }, { "SUB", 2 }, { "XOR", 3 },
        { "NOT", 4 }, { "CMP", 5 }, { "SHL", 6 }, { "SHR", 7 },
    };

    // Mnemonics that take an address/immediate operand. BRA is the unconditional
    // branch (opcode 7); BRANCH accepted as an alias.
    private static readonly Dictionary<string, int> OperandOps =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "PUSH", OpPush }, { "LOAD", OpLoad }, { "STORE", OpStore },
        { "IN", OpIn }, { "BRA", OpBranch }, { "BRANCH", OpBranch },
        { "BEQ", OpBeq }, { "BCS", OpBcs }, { "BMI", OpBmi }, { "CALL", OpCall },
    };

    // ROT is a macro on both machines (Mini_Blinky_CPU.md): >R SWAP R> SWAP.
    private static readonly string[] RotExpansion = { ">R", "SWAP", "R>", "SWAP" };

    private sealed class SourceLine
    {
        public int Number;
        public string Raw = "";
        public string? Label;
        public string? Mnemonic;
        public string? Operand;
    }

    private sealed class EmittedWord
    {
        public int Address;
        public byte Code;
        public string Text = "";
        public string Comment = "";
    }

    private static int Main(string[] args)
    {
        int exitCode = Run(args);

        // Keep the console window open when launched from Explorer or a shortcut.
        // Skipped when input is redirected (piped or scripted), where there is no
        // interactive key to wait for and Console.ReadKey would otherwise throw.
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Press any key to exit...");
            Console.ReadKey(intercept: true);
            Console.WriteLine();
        }

        return exitCode;
    }

    private static int Run(string[] args)
    {
        if (args.Length < 1 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length < 1 ? 1 : 0;
        }

        string inputPath = args[0];
        string? hexPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
                hexPath = args[++i];
            else
            {
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                PrintUsage();
                return 1;
            }
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 1;
        }

        hexPath ??= Path.ChangeExtension(inputPath, ".hex");
        string lstPath = Path.ChangeExtension(hexPath, ".lst");

        string[] rawLines = File.ReadAllLines(inputPath);
        var errors = new List<string>();

        List<SourceLine> lines = ParseAll(rawLines);
        Dictionary<string, int> labels = ResolveLabels(lines, errors);
        List<EmittedWord> image = Encode(lines, labels, errors);

        if (errors.Count > 0)
        {
            foreach (string e in errors) Console.Error.WriteLine(e);
            Console.Error.WriteLine($"\nAssembly failed: {errors.Count} error(s).");
            return 1;
        }

        var data = new byte[image.Count];
        foreach (EmittedWord w in image) data[w.Address] = w.Code;

        string hex = ToIntelHex(data);
        string listing = BuildListing(image);

        File.WriteAllText(hexPath, hex);
        File.WriteAllText(lstPath, listing);

        Console.Write(listing);
        Console.WriteLine($"\n{image.Count} word(s) -> {hexPath}");
        Console.WriteLine($"Listing       -> {lstPath}");
        return 0;
    }

    // ---- Pass 0: parse each line into label / mnemonic / operand ----
    private static List<SourceLine> ParseAll(string[] rawLines)
    {
        var result = new List<SourceLine>();
        for (int i = 0; i < rawLines.Length; i++)
        {
            var line = new SourceLine { Number = i + 1, Raw = rawLines[i].TrimEnd() };
            string text = StripComment(rawLines[i]).Trim();
            if (text.Length == 0) { result.Add(line); continue; }

            // Leading "label:" (an identifier followed by a colon).
            int colon = text.IndexOf(':');
            if (colon > 0 && IsIdentifier(text.Substring(0, colon)))
            {
                line.Label = text.Substring(0, colon);
                text = text.Substring(colon + 1).Trim();
            }

            if (text.Length > 0)
            {
                string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                line.Mnemonic = parts[0];
                if (parts.Length >= 2) line.Operand = parts[1];
                if (parts.Length > 2) line.Operand = "(too many tokens)";
            }
            result.Add(line);
        }
        return result;
    }

    // ---- Pass 1: assign addresses, build the label map ----
    private static Dictionary<string, int> ResolveLabels(
        List<SourceLine> lines, List<string> errors)
    {
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int address = 0;

        foreach (SourceLine line in lines)
        {
            if (line.Label != null)
            {
                if (labels.ContainsKey(line.Label))
                    errors.Add($"Line {line.Number}: duplicate label '{line.Label}'.");
                else
                    labels[line.Label] = address;   // address of the next word
            }

            if (line.Mnemonic != null)
                address += WordCount(line.Mnemonic);
        }

        if (address > IMemSize)
            errors.Add($"Program is {address} words; I-memory holds only {IMemSize}.");

        return labels;
    }

    // ---- Pass 2: encode to instruction words ----
    private static List<EmittedWord> Encode(
        List<SourceLine> lines, Dictionary<string, int> labels, List<string> errors)
    {
        var image = new List<EmittedWord>();
        int address = 0;

        foreach (SourceLine line in lines)
        {
            if (line.Mnemonic == null) continue;
            string m = line.Mnemonic;

            if (string.Equals(m, "ROT", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Operand != null)
                    errors.Add($"Line {line.Number}: ROT takes no operand.");
                foreach (string micro in RotExpansion)
                {
                    int n = SysSubOp[micro];
                    image.Add(new EmittedWord
                    {
                        Address = address++,
                        Code = (byte)((OpSys << 4) | n),
                        Text = micro,
                        Comment = "ROT",
                    });
                }
                continue;
            }

            byte code;
            string text;
            string comment = "";

            if (SysSubOp.TryGetValue(m, out int sysNibble))
            {
                RequireNoOperand(line, errors);
                code = (byte)((OpSys << 4) | sysNibble);
                text = m;
            }
            else if (AluSubOp.TryGetValue(m, out int aluNibble))
            {
                RequireNoOperand(line, errors);
                code = (byte)((OpAlu << 4) | aluNibble);
                text = m;
            }
            else if (string.Equals(m, "OUT", StringComparison.OrdinalIgnoreCase))
            {
                RequireNoOperand(line, errors);
                code = (byte)(OpOut << 4);   // literal don't-care, encoded 0
                text = "OUT";
            }
            else if (OperandOps.TryGetValue(m, out int opcode))
            {
                int literal = 0;
                if (line.Operand == null)
                {
                    errors.Add($"Line {line.Number}: '{m}' requires an operand.");
                }
                else
                {
                    literal = ParseLiteral(line.Operand, labels, line.Number, errors);

                    // When the operand is a label, show the address it resolved to.
                    string bare = line.Operand.StartsWith("#")
                        ? line.Operand.Substring(1)
                        : line.Operand;
                    if (labels.TryGetValue(bare, out int target))
                        comment = $"{bare} = {target:X2}";
                }
                code = (byte)((opcode << 4) | (literal & 0xF));
                text = $"{m} {line.Operand}";
            }
            else
            {
                errors.Add($"Line {line.Number}: unknown mnemonic '{m}'.");
                code = 0;
                text = m;
            }

            image.Add(new EmittedWord
            {
                Address = address++,
                Code = code,
                Text = text,
                Comment = comment,
            });
        }

        return image;
    }

    private static void RequireNoOperand(SourceLine line, List<string> errors)
    {
        if (line.Operand != null)
            errors.Add($"Line {line.Number}: '{line.Mnemonic}' takes no operand.");
    }

    private static int ParseLiteral(
        string token, Dictionary<string, int> labels, int lineNumber, List<string> errors)
    {
        string t = token.StartsWith("#") ? token.Substring(1) : token;

        if (labels.TryGetValue(t, out int labelAddr))
            return CheckRange(labelAddr, t, lineNumber, errors);

        int value;
        try
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToInt32(t.Substring(2), 16);
            else if (t.StartsWith("$"))
                value = Convert.ToInt32(t.Substring(1), 16);
            else if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToInt32(t.Substring(2), 2);
            else if (t.StartsWith("%"))
                value = Convert.ToInt32(t.Substring(1), 2);
            else
                value = int.Parse(t, CultureInfo.InvariantCulture);
        }
        catch
        {
            errors.Add($"Line {lineNumber}: cannot parse operand '{token}' (unknown label or number).");
            return 0;
        }

        return CheckRange(value, token, lineNumber, errors);
    }

    private static int CheckRange(int value, string token, int lineNumber, List<string> errors)
    {
        if (value < 0 || value > LitMax)
            errors.Add($"Line {lineNumber}: '{token}' = {value} is out of range (0..{LitMax}).");
        return value;
    }

    private static int WordCount(string mnemonic) =>
        string.Equals(mnemonic, "ROT", StringComparison.OrdinalIgnoreCase) ? 4 : 1;

    // ---- Intel HEX, matching TTLSim.Core.IntelHex.Write framing ----
    private static string ToIntelHex(byte[] data)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            int len = Math.Min(16, data.Length - offset);
            var rec = new List<byte>(len + 4)
            {
                (byte)len, (byte)(offset >> 8), (byte)offset, 0x00,
            };
            for (int i = 0; i < len; i++) rec.Add(data[offset + i]);

            int sum = 0;
            foreach (byte b in rec) sum += b;
            rec.Add((byte)((0x100 - (sum & 0xFF)) & 0xFF));   // two's-complement checksum

            sb.Append(':');
            foreach (byte b in rec) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        sb.Append(":00000001FF\n");   // EOF
        return sb.ToString();
    }

    private static string BuildListing(List<EmittedWord> image)
    {
        var sb = new StringBuilder();
        sb.Append("ADDR  CODE  SOURCE\n");
        sb.Append("----  ----  ------\n");
        foreach (EmittedWord w in image)
        {
            string src = w.Comment.Length > 0
                ? $"{w.Text.PadRight(14)}; {w.Comment}"
                : w.Text;
            sb.Append($"{w.Address:X2}    {w.Code:X2}    {src}\n");
        }
        return sb.ToString();
    }

    private static string StripComment(string line)
    {
        int semicolon = line.IndexOf(';');
        return semicolon >= 0 ? line.Substring(0, semicolon) : line;
    }

    private static bool IsIdentifier(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return false;
        char first = s[0];
        if (!(char.IsLetter(first) || first == '_' || first == '.')) return false;
        foreach (char c in s)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) return false;
        return true;
    }

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "/?" or "-?";

    private static void PrintUsage()
    {
        Console.WriteLine(
@"Mini Blinky assembler (W = 4)

Usage:
  mbasm <input.asm> [-o <output.hex>]

Output:
  <input>.hex   Intel HEX, loadable into the I-memory chip
  <input>.lst   address/code listing

Syntax:
  ; comment to end of line
  label:        a name for the next instruction's address (0..15)
  MNEM          a no-operand instruction
  MNEM operand  operand is a number or a label (0..15)

Numbers: 12 (decimal), 0x0C / $0C (hex), 0b1100 / %1100 (binary).
PUSH/LOAD/STORE/IN may write the operand as #5 or 5.

Instructions:
  SYS:   NOP HALT RET DUP DROP SWAP >R R>
  ALU:   ADD ADC SUB XOR NOT CMP SHL SHR
  Other: PUSH #imm  LOAD addr  STORE addr  IN port  OUT
  Flow:  BRA addr  BEQ addr  BCS addr  BMI addr  CALL addr
  Macro: ROT  (expands to: >R SWAP R> SWAP)");
    }
}