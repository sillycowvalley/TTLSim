// ============================================================================
//  Disassembler.cs  -  Blinky-M Virtual Front Panel
//
//  Folds the raw capture log into annotated disassembly steps.
//
//  The probe samples on every CLKG rising edge, so the log is one row per
//  T-state. /FETCH asserted marks T0 of an instruction: the address bus holds
//  the PC and the data bus holds the opcode byte. The opcode's length says how
//  many operand bytes follow; those are harvested from the T-states before the
//  next /FETCH by matching the address bus against PC+1 and PC+2.
//
//  Nothing here consults the .hex image, so the listing shows what the machine
//  actually executed - not what you meant to burn.
//
//  Sample frame bit order (matches Program.cs and the firmware):
//     bits  0-15 : A0..A15
//     bits 16-23 : D0..D7
//     bit  24    : CLKG
//     bit  25    : /RESET   (0 = asserted)
//     bit  26    : /FETCH   (0 = asserted)
//     bits 27-29 : T0 T1 T2
//     bit  30    : HALT
//     bit  31    : N
//
//  Output (verbose):
//
//      E000  30 2A        PUSH #0x2A               4T     2.480 us
//            T0  /FETCH  A=E000  D=30
//            T1          A=E001  D=2A
//            T2          A=0140  D=2A
//            T3          A=0141  D=00
//      E002  49           ADD                      4T     2.480 us
//      E003  FF           HALT                     2T     1.240 us  [HALT]
//
//  Rev 14 opcode map, mirroring BlinkyMGen's generated OpcodeTable.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BlinkyMFrontPanel
{
    internal enum Shape { None, Imm8, Port8, Count8, Offset8, Addr16 }

    internal struct Op
    {
        public string Mnemonic;
        public Shape Shape;
        public int Length;

        public Op(string mnemonic, Shape shape, int length)
        {
            Mnemonic = mnemonic;
            Shape = shape;
            Length = length;
        }
    }

    internal static class Disassembler
    {
        // ------------------------------------------------------------ frame bits
        private static int AddrOf(uint w) { return (int)(w & 0xFFFFu); }
        private static int DataOf(uint w) { return (int)((w >> 16) & 0xFFu); }
        private static bool ResetAsserted(uint w) { return ((w >> 25) & 1u) == 0u; }
        private static bool FetchAsserted(uint w) { return ((w >> 26) & 1u) == 0u; }
        private static int TStateOf(uint w) { return (int)((w >> 27) & 7u); }
        private static bool HaltOf(uint w) { return ((w >> 30) & 1u) != 0u; }
        private static bool NegOf(uint w) { return ((w >> 31) & 1u) != 0u; }

        // ------------------------------------------------------------ opcode map
        private static readonly Dictionary<int, Op> Ops = BuildOps();

        private static Dictionary<int, Op> BuildOps()
        {
            Dictionary<int, Op> map = new Dictionary<int, Op>();

            // CTL 0x0_
            map[0x00] = new Op("NOP", Shape.None, 1);
            map[0x01] = new Op("RET", Shape.None, 1);
            map[0x02] = new Op("BRK", Shape.None, 1);
            map[0x03] = new Op("RTI", Shape.None, 1);
            map[0x04] = new Op("CLI", Shape.None, 1);
            map[0x05] = new Op("SEI", Shape.None, 1);

            // SHIFT 0x1_
            map[0x10] = new Op("SHL", Shape.None, 1);
            map[0x11] = new Op("SHR", Shape.None, 1);
            map[0x13] = new Op("ASR", Shape.None, 1);
            map[0x14] = new Op("ROL", Shape.None, 1);
            map[0x15] = new Op("ROR", Shape.None, 1);

            // STK 0x2_
            map[0x20] = new Op("DUP", Shape.None, 1);
            map[0x21] = new Op("OVER", Shape.None, 1);
            map[0x22] = new Op("TUCK", Shape.None, 1);
            map[0x23] = new Op("SWAP", Shape.None, 1);
            map[0x24] = new Op("DROP", Shape.None, 1);
            map[0x25] = new Op("NIP", Shape.None, 1);
            map[0x26] = new Op("ROT", Shape.None, 1);
            map[0x28] = new Op(">R", Shape.None, 1);
            map[0x29] = new Op("R>", Shape.None, 1);
            map[0x2A] = new Op("R@", Shape.None, 1);

            // MEM 0x3_
            map[0x30] = new Op("PUSH", Shape.Imm8, 2);
            map[0x31] = new Op("LOAD", Shape.Addr16, 3);
            map[0x32] = new Op("STORE", Shape.Addr16, 3);
            map[0x33] = new Op("FETCH", Shape.None, 1);
            map[0x34] = new Op("STORE!", Shape.None, 1);
            map[0x35] = new Op("IN", Shape.Port8, 2);
            map[0x36] = new Op("OUT", Shape.Port8, 2);
            map[0x37] = new Op("INS", Shape.None, 1);
            map[0x38] = new Op("OUTS", Shape.None, 1);

            // ALU 0x40-0x7F: the named subset; the rest resolves to ALU #n.
            map[0x49] = new Op("ADD", Shape.None, 1);
            map[0x56] = new Op("SUB", Shape.None, 1);
            map[0x59] = new Op("ADC", Shape.None, 1);
            map[0x60] = new Op("NOT", Shape.None, 1);
            map[0x66] = new Op("XOR", Shape.None, 1);
            map[0x6B] = new Op("AND", Shape.None, 1);
            map[0x6E] = new Op("OR", Shape.None, 1);
            map[0x6F] = new Op("TST", Shape.None, 1);

            // FRM 0x8_
            map[0x80] = new Op("ENTER", Shape.Count8, 2);
            map[0x81] = new Op("LOCAL@", Shape.Offset8, 2);
            map[0x82] = new Op("LOCAL!", Shape.Offset8, 2);

            // FLOW 0x9_
            map[0x90] = new Op("JUMP", Shape.Addr16, 3);
            map[0x91] = new Op("CALL", Shape.Addr16, 3);
            map[0x96] = new Op("CMP", Shape.None, 1);
            map[0x9A] = new Op("BEQ", Shape.Addr16, 3);
            map[0x9B] = new Op("BNE", Shape.Addr16, 3);
            map[0x9C] = new Op("BCS", Shape.Addr16, 3);
            map[0x9D] = new Op("BCC", Shape.Addr16, 3);
            map[0x9E] = new Op("BMI", Shape.Addr16, 3);
            map[0x9F] = new Op("BPL", Shape.Addr16, 3);

            map[0xFF] = new Op("HALT", Shape.None, 1);

            return map;
        }

        private static bool IsAlu(int opcode)
        {
            return opcode >= 0x40 && opcode <= 0x7F;
        }

        private static int LengthOf(int opcode)
        {
            Op op;
            if (Ops.TryGetValue(opcode, out op)) return op.Length;
            return 1;    // ALU quadrant and unknown bytes: one byte, resync on next /FETCH
        }

        // ------------------------------------------------------------ steps
        private sealed class Step
        {
            public bool IsMarker;
            public string Marker;

            public int Pc;
            public List<int> Bytes = new List<int>();
            public List<Sample> Frames = new List<Sample>();
            public bool Halted;
            public bool Truncated;    // the log ended before the operand bytes arrived
        }

        private static List<Step> Fold(Sample[] samples)
        {
            List<Step> steps = new List<Step>();
            Step current = null;
            bool wasReset = false;
            bool seenFetch = false;
            bool first = true;

            for (int i = 0; i < samples.Length; i++)
            {
                Sample s = samples[i];
                bool inReset = ResetAsserted(s.Word);

                if (first || inReset != wasReset)
                {
                    if (!first || inReset)
                    {
                        current = null;
                        Step m = new Step();
                        m.IsMarker = true;
                        m.Marker = inReset ? "/RESET asserted" : "/RESET released";
                        steps.Add(m);
                    }
                    wasReset = inReset;
                    first = false;
                }

                if (inReset) continue;

                if (FetchAsserted(s.Word))
                {
                    current = new Step();
                    current.Pc = AddrOf(s.Word);
                    current.Bytes.Add(DataOf(s.Word));
                    current.Frames.Add(s);
                    if (HaltOf(s.Word)) current.Halted = true;
                    steps.Add(current);
                    seenFetch = true;
                    continue;
                }

                // Anything before the first /FETCH is stream leftover; drop it.
                if (!seenFetch || current == null) continue;

                current.Frames.Add(s);
                if (HaltOf(s.Word)) current.Halted = true;

                int wanted = LengthOf(current.Bytes[0]);
                if (current.Bytes.Count < wanted)
                {
                    int expect = (current.Pc + current.Bytes.Count) & 0xFFFF;
                    if (AddrOf(s.Word) == expect) current.Bytes.Add(DataOf(s.Word));
                }
            }

            foreach (Step s in steps)
            {
                if (s.IsMarker) continue;
                if (s.Bytes.Count < LengthOf(s.Bytes[0])) s.Truncated = true;
            }

            return steps;
        }

        // ------------------------------------------------------------ rendering
        private static string Text(Step s)
        {
            int opcode = s.Bytes[0];
            Op op;

            if (Ops.TryGetValue(opcode, out op))
            {
                string operand = Operand(op, s);
                return operand.Length == 0 ? op.Mnemonic : op.Mnemonic + " " + operand;
            }

            if (IsAlu(opcode))
            {
                int fn = opcode & 0x3F;
                int m = (fn >> 5) & 1;
                int cn = (fn >> 4) & 1;
                int sel = fn & 0xF;
                return "ALU #0x" + fn.ToString("X2", CultureInfo.InvariantCulture)
                     + "   ; M=" + m.ToString(CultureInfo.InvariantCulture)
                     + " CN=" + cn.ToString(CultureInfo.InvariantCulture)
                     + " S=" + Convert.ToString(sel, 2).PadLeft(4, '0');
            }

            return "??? 0x" + opcode.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static string Operand(Op op, Step s)
        {
            switch (op.Shape)
            {
                case Shape.None:
                    return "";

                case Shape.Imm8:
                case Shape.Port8:
                    if (s.Bytes.Count < 2) return "#??";
                    return "#0x" + s.Bytes[1].ToString("X2", CultureInfo.InvariantCulture);

                case Shape.Count8:
                    if (s.Bytes.Count < 2) return "??";
                    return s.Bytes[1].ToString(CultureInfo.InvariantCulture);

                case Shape.Offset8:
                    if (s.Bytes.Count < 2) return "??";
                    return ((sbyte)s.Bytes[1]).ToString(CultureInfo.InvariantCulture);

                case Shape.Addr16:
                    if (s.Bytes.Count < 3) return "0x????";
                    int a = s.Bytes[1] | (s.Bytes[2] << 8);
                    return "0x" + a.ToString("X4", CultureInfo.InvariantCulture);
            }
            return "";
        }

        private static string HexBytes(Step s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Bytes.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(s.Bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ build
        public static string Build(Sample[] samples, bool verbose)
        {
            StringBuilder sb = new StringBuilder();
            List<Step> steps = Fold(samples);

            int instructions = 0;
            int resets = 0;
            int unknown = 0;
            foreach (Step s in steps)
            {
                if (s.IsMarker)
                {
                    if (s.Marker == "/RESET asserted") resets++;
                    continue;
                }
                instructions++;
                if (!Ops.ContainsKey(s.Bytes[0]) && !IsAlu(s.Bytes[0])) unknown++;
            }

            sb.Append("; Blinky-M front-panel disassembly\n");
            sb.Append("; Saved: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");
            sb.Append("; " + samples.Length.ToString(CultureInfo.InvariantCulture) + " samples, "
                    + instructions.ToString(CultureInfo.InvariantCulture) + " instructions, "
                    + resets.ToString(CultureInfo.InvariantCulture) + " reset(s), "
                    + unknown.ToString(CultureInfo.InvariantCulture) + " unknown opcode(s)\n");
            sb.Append(";\n");
            sb.Append("; Reconstructed from the capture alone: /FETCH marks T0 (address = PC,\n");
            sb.Append("; data = opcode); operand bytes are the bus reads at PC+1 / PC+2.\n");
            sb.Append("; Elapsed times are host arrival times and carry USB/OS jitter.\n");
            sb.Append(";\n");
            sb.Append("; addr  bytes     instruction               T    elapsed\n");
            sb.Append("\n");

            foreach (Step s in steps)
            {
                if (s.IsMarker)
                {
                    sb.Append("---- " + s.Marker + " ----\n");
                    continue;
                }

                long span = s.Frames.Count > 1
                    ? s.Frames[s.Frames.Count - 1].RawMicros - s.Frames[0].RawMicros
                    : 0L;

                string flags = "";
                if (s.Halted) flags += "  [HALT]";
                if (s.Truncated) flags += "  [truncated]";

                sb.Append(s.Pc.ToString("X4", CultureInfo.InvariantCulture));
                sb.Append("  ");
                sb.Append(HexBytes(s).PadRight(8));
                sb.Append("  ");
                sb.Append(Text(s).PadRight(24));
                sb.Append(s.Frames.Count.ToString(CultureInfo.InvariantCulture).PadLeft(2));
                sb.Append("T  ");
                sb.Append(span.ToString(CultureInfo.InvariantCulture).PadLeft(8));
                sb.Append(" us");
                sb.Append(flags);
                sb.Append("\n");

                if (!verbose) continue;

                for (int i = 0; i < s.Frames.Count; i++)
                {
                    uint w = s.Frames[i].Word;
                    sb.Append("      T");
                    sb.Append(TStateOf(w).ToString(CultureInfo.InvariantCulture));
                    sb.Append("  ");
                    sb.Append(FetchAsserted(w) ? "/FETCH" : "      ");
                    sb.Append("  A=");
                    sb.Append(AddrOf(w).ToString("X4", CultureInfo.InvariantCulture));
                    sb.Append("  D=");
                    sb.Append(DataOf(w).ToString("X2", CultureInfo.InvariantCulture));
                    if (HaltOf(w)) sb.Append("  HALT");
                    if (NegOf(w)) sb.Append("  N");
                    sb.Append("\n");
                }
            }

            return sb.ToString();
        }
    }
}