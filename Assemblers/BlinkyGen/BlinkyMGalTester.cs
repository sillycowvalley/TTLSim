// =====================================================================
// BlinkyMGalTester -- BlinkyMGen module that emits the Blinky-M rev 14
// in-circuit GAL burn tester (BlinkyMGalTest.ino) as one more generator
// output. Reads the eleven emitted .pld sources, so the PLDs remain the
// single source of truth and the tester can never drift from the burns.
//
// Every Blinky-M GAL is purely combinational: for each device this builds
// a PROGMEM truth table by evaluating the .pld equations over all used
// input combinations (active-low polarity and GND constants folded into the
// expected pin level). The emitted sketch just drives inputs, reads OLMC
// pins, and compares.
//
// HOOK: after BlinkyMGen writes the .pld files (pldDir == outDir), add one
// line. The Arduino IDE requires a sketch to live in a folder of its own
// name, so target a BlinkyMGalTest subfolder; Write creates it if missing:
//   BlinkyMGalTester.Write(outDir, Path.Combine(outDir, "BlinkyMGalTest", "BlinkyMGalTest.ino"));
// (Build(pldDir) returns the sketch text if you prefer to write it yourself.)
// =====================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BlinkyMGen
{
    public static class BlinkyMGalTester
    {

        // Entry point for BlinkyMGen: build the tester sketch text from the
        // folder holding the eleven emitted .pld sources.
        public static string Build(string pldDir, Action<string> log = null)
        {
            return Generate(pldDir, log ?? delegate { });
        }

        // Convenience: build and write the .ino in one call. Creates the
        // target folder if it does not exist (the Arduino IDE needs the sketch
        // in a folder of its own name, e.g. BlinkyMGalTest/BlinkyMGalTest.ino).
        public static void Write(string pldDir, string outInoPath, Action<string> log = null)
        {
            string dir = Path.GetDirectoryName(outInoPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outInoPath, Build(pldDir, log));
        }

        sealed class Literal { public string Name; public bool Neg; }

        sealed class Equation
        {
            public string Name;
            public bool ActiveLow;
            public List<List<Literal>> Terms = new List<List<Literal>>();
        }

        sealed class Parsed
        {
            public Dictionary<string, int> PinByName = new Dictionary<string, int>();
            public Dictionary<int, string> NameByPin = new Dictionary<int, string>();
            public List<Equation> Equations = new List<Equation>();
            public bool HasBanken;
        }

        sealed class GalOut { public string Name; public int Pin; public byte[] Level; }

        sealed class Gal
        {
            public string LogicalName;
            public bool HasBanken;
            public int N;
            public List<string> InputNames;
            public List<int> InPins;
            public List<GalOut> Outputs;
        }

        // GAL physical pin -> Arduino Mega pin. Matches the TTLSim GAL22V10
        // harness, with ONE addition: GAL pin 17 -> Mega D34 (the harness
        // left pin 17 unwired; every Blinky-M GAL drives it).
        public static readonly Dictionary<int, int> Harness = new Dictionary<int, int>
        {
            { 1, 53 }, { 2, 51 }, { 3, 49 }, { 4, 47 }, { 5, 45 }, { 6, 43 }, { 7, 41 },
            { 8, 39 }, { 9, 37 }, { 10, 52 }, { 11, 50 }, { 13, 23 },
            { 14, 26 }, { 15, 28 }, { 16, 30 }, { 17, 34 }, { 18, 35 }, { 19, 33 },
            { 20, 31 }, { 21, 29 }, { 22, 27 }, { 23, 25 }
        };

        // Menu order and single-char selector.
        public static readonly string[] Order =
        {
            "SEQ_ENTRY", "SEQ_CTL", "SEQ_CTLX", "SEQ_ALU", "SEQ_MEM",
            "SEQ_STK", "SEQ_SHIFT", "SEQ_FLOW", "SEQ_FRM", "UOPA", "UOPB"
        };
        public static readonly string[] SelCh =
        {
            "1", "2", "3", "4", "5", "6", "7", "8", "9", "A", "B"
        };

        // Source filename stem -> logical GAL name.
        public static readonly Dictionary<string, string> FileMap = new Dictionary<string, string>
        {
            { "BLINKY_M_SEQ_ENTRY", "SEQ_ENTRY" }, { "BLINKY_M_SEQ_CTL", "SEQ_CTL" },
            { "BLINKY_M_SEQ_CTLX", "SEQ_CTLX" }, { "BLINKY_M_SEQ_ALU", "SEQ_ALU" },
            { "BLINKY_M_SEQ_MEM", "SEQ_MEM" }, { "BLINKY_M_SEQ_STK", "SEQ_STK" },
            { "BLINKY_M_SEQ_SHIFT", "SEQ_SHIFT" }, { "BLINKY_M_SEQ_FLOW", "SEQ_FLOW" },
            { "BLINKY_M_SEQ_FRM", "SEQ_FRM" }, { "BLINKY_M_UOPA", "UOPA" }, { "BLINKY_M_UOPB", "UOPB" }
        };

        // Fixed boilerplate of the emitted sketch (helpers + generic runner),
        // stored one source line per array element so no literal spans a line.
        static readonly string[] Boilerplate =
        {
        "// =====================================================================",
        "// BlinkyMGalTest -- in-circuit test of the Blinky-M rev 14 sequencer and",
        "// micro-op-decoder GAL22V10 burns on an Arduino Mega 2560. Fuse-compatible",
        "// with the ATF22V10C.",
        "//",
        "//   *** GENERATED by BlinkyGalTestGen from the .pld sources. Do not",
        "//       hand-edit -- regenerate when the PLDs change. ***",
        "//",
        "// All eleven Blinky-M GALs are purely COMBINATIONAL, so this tester needs",
        "// no clocking: for each GAL it sweeps every used input combination, reads",
        "// the OLMC pins, and compares against the design equations taken straight",
        "// from the .pld files (active-low polarity and GND constants already folded",
        "// into the expected pin level). A PASS means the physical chip implements",
        "// the .pld intent.",
        "//",
        "// HARNESS: reuses TTLSim22V10GalTest.ino wiring, with ONE addition --",
        "// GAL pin 17 (previously unwired) must be connected to Mega D34, because",
        "// every Blinky-M GAL drives pin 17 (IDX3 / SRC1 / AMODE1). All GAL inputs",
        "// are driven by the Mega and all OLMC pins are read; no series resistors.",
        "// The nine sequencer burns gate their outputs Hi-Z via BANKEN (pin 11); the",
        "// tester holds BANKEN high for the functional sweep and adds a short Hi-Z",
        "// phase (BANKEN low) to prove the tri-state bank gating. The two UOP burns",
        "// drive their outputs unconditionally (no BANKEN).",
        "//",
        "// Usage: socket ONE chip, open Serial Monitor at 115200, pick a menu key.",
        "// Power down (or press reset) before swapping chips.",
        "// =====================================================================",
        "",
        "#include <avr/pgmspace.h>",
        "",
        "// ---- harness ---------------------------------------------------------",
        "// GAL input-capable pins (GAL 1..11,13) -> Mega, fixed order. Held LOW",
        "// unless a GAL sweeps them. BANKEN is GAL pin 11 = Mega D50.",
        "const uint8_t IN_CAPABLE[12] = { 53, 51, 49, 47, 45, 43, 41, 39, 37, 52, 50, 23 };",
        "const uint8_t BANKEN_PIN = 50;",
        "const uint8_t SETTLE_US  = 2;     // generous vs a 15-25 ns part",
        "",
        "// ---- reporting -------------------------------------------------------",
        "const uint8_t MAX_REPORT = 24;",
        "uint32_t failures, checks;",
        "char vecBuf[24];",
        "",
        "// ---- generic per-GAL descriptor (defined before any function so the",
        "// IDE's auto-generated prototypes can see it) -------------------------",
        "struct GalOutput { uint8_t pin; const char *name; const uint8_t *tt; };",
        "struct GalTest {",
        "  const char *name;",
        "  uint8_t  inCount;              // vectors = 1 << inCount",
        "  const uint8_t *inPins;         // Mega drive pin per input bit",
        "  const char *inLegend;          // bit-order legend, printed once",
        "  uint8_t  outCount;",
        "  const GalOutput *outputs;",
        "  bool     hasBanken;",
        "};",
        "",
        "void reportFail(const char *vector, const char *sig, bool expected, bool got) {",
        "  failures++;",
        "  if (failures > MAX_REPORT) return;",
        "  Serial.print(F(\"FAIL \")); Serial.print(vector);",
        "  Serial.print(F(\"  \")); Serial.print(sig);",
        "  Serial.print(F(\": expected \")); Serial.print(expected);",
        "  Serial.print(F(\" got \")); Serial.println(got);",
        "}",
        "void check(const char *vector, const char *sig, uint8_t pin, bool expected) {",
        "  bool got = digitalRead(pin);",
        "  checks++;",
        "  if (got != expected) reportFail(vector, sig, expected, got);",
        "}",
        "void summarize(const char *name, unsigned long t0) {",
        "  unsigned long ms = millis() - t0;",
        "  if (failures > MAX_REPORT) {",
        "    Serial.print(F(\"... and \")); Serial.print(failures - MAX_REPORT);",
        "    Serial.println(F(\" more failures not shown.\"));",
        "  }",
        "  Serial.print(F(\"Checked \")); Serial.print(checks);",
        "  Serial.print(F(\" output values in \")); Serial.print(ms);",
        "  Serial.println(F(\" ms.\"));",
        "  if (failures == 0) {",
        "    Serial.print(F(\"*** PASS: \")); Serial.print(name);",
        "    Serial.println(F(\" matches the .pld design equations. ***\"));",
        "  } else {",
        "    Serial.print(F(\"*** FAIL: \")); Serial.print(failures);",
        "    Serial.println(F(\" mismatches. ***\"));",
        "  }",
        "}",
        "",
        "void idlePins() {                          // safe state for chip swapping",
        "  for (uint8_t k = 0; k < 12; k++) pinMode(IN_CAPABLE[k], INPUT);",
        "  const uint8_t io[10] = { 26,28,30,34,35,33,31,29,27,25 };",
        "  for (uint8_t k = 0; k < 10; k++) pinMode(io[k], INPUT);",
        "}",
        "",
        "void beginGal(const GalTest &g) {",
        "  for (uint8_t k = 0; k < 12; k++) { pinMode(IN_CAPABLE[k], OUTPUT); digitalWrite(IN_CAPABLE[k], LOW); }",
        "  for (uint8_t o = 0; o < g.outCount; o++) pinMode(g.outputs[o].pin, INPUT);",
        "  if (g.hasBanken) digitalWrite(BANKEN_PIN, HIGH);   // bank selected",
        "}",
        "",
        "bool ttBit(const uint8_t *tt, uint16_t v) {",
        "  return (pgm_read_byte(tt + (v >> 3)) >> (v & 7)) & 1;",
        "}",
        "",
        "void hiZPhase(const GalTest &g) {",
        "  digitalWrite(BANKEN_PIN, LOW);                     // deselect bank",
        "  for (uint8_t o = 0; o < g.outCount; o++) pinMode(g.outputs[o].pin, INPUT_PULLUP);",
        "  uint16_t N = (uint16_t)1 << g.inCount;",
        "  uint16_t probes[3] = { 0, (uint16_t)(N - 1), (uint16_t)(N >> 1) };",
        "  for (uint8_t p = 0; p < 3; p++) {",
        "    uint16_t v = probes[p];",
        "    for (uint8_t i = 0; i < g.inCount; i++) digitalWrite(g.inPins[i], (v >> i) & 1);",
        "    delayMicroseconds(SETTLE_US);",
        "    snprintf(vecBuf, sizeof vecBuf, \"Hi-Z v=0x%03X\", v);",
        "    for (uint8_t o = 0; o < g.outCount; o++)",
        "      check(vecBuf, g.outputs[o].name, g.outputs[o].pin, true);  // pullup -> 1",
        "  }",
        "}",
        "",
        "void runGal(const GalTest &g) {",
        "  Serial.print(F(\"\\nTesting \")); Serial.print(g.name);",
        "  Serial.print(F(\" -- \")); Serial.print((uint16_t)1 << g.inCount);",
        "  Serial.println(F(\" input vectors...\"));",
        "  Serial.print(F(\"  bit order (b0..): \")); Serial.println(g.inLegend);",
        "  beginGal(g);",
        "  failures = 0; checks = 0;",
        "  unsigned long t0 = millis();",
        "  uint16_t N = (uint16_t)1 << g.inCount;",
        "  for (uint16_t v = 0; v < N; v++) {",
        "    for (uint8_t i = 0; i < g.inCount; i++) digitalWrite(g.inPins[i], (v >> i) & 1);",
        "    delayMicroseconds(SETTLE_US);",
        "    snprintf(vecBuf, sizeof vecBuf, \"v=0x%03X\", v);",
        "    for (uint8_t o = 0; o < g.outCount; o++)",
        "      check(vecBuf, g.outputs[o].name, g.outputs[o].pin, ttBit(g.outputs[o].tt, v));",
        "  }",
        "  if (g.hasBanken) hiZPhase(g);",
        "  summarize(g.name, t0);",
        "  idlePins();",
        "}",
        "",
        "// =====================================================================",
        "// Per-GAL generated data",
        "// =====================================================================",
        ""
        };

        static Parsed ParsePld(string text)
        {
            string src = Regex.Replace(text, "/\\*[\\s\\S]*?\\*/", " ");
            Parsed p = new Parsed();
            foreach (string raw in src.Split(';'))
            {
                string st = raw.Trim();
                if (st.Length == 0) continue;

                if (Regex.IsMatch(st, "^PIN\\b", RegexOptions.IgnoreCase))
                {
                    Match m = Regex.Match(st, "^PIN\\s+(\\d+)\\s*=\\s*([A-Za-z_]\\w*)$", RegexOptions.IgnoreCase);
                    if (!m.Success) throw new Exception("bad PIN stmt: " + st);
                    int pin = int.Parse(m.Groups[1].Value);
                    string nm = m.Groups[2].Value;
                    p.PinByName[nm] = pin;
                    p.NameByPin[pin] = nm;
                    if (nm == "BANKEN") p.HasBanken = true;
                    continue;
                }

                int eq = st.IndexOf('=');
                if (eq < 0) continue;                        // header keyword lines
                string lhs = st.Substring(0, eq).Trim();
                string rhs = st.Substring(eq + 1).Trim();

                if (lhs.EndsWith(".oe", StringComparison.OrdinalIgnoreCase)) { p.HasBanken = true; continue; }
                if (rhs.IndexOf('(') >= 0 || rhs.IndexOf(')') >= 0) throw new Exception("parentheses unsupported: " + st);

                bool activeLow = lhs.StartsWith("!");
                string name = (activeLow ? lhs.Substring(1) : lhs).Trim();
                Equation e = new Equation { Name = name, ActiveLow = activeLow };

                foreach (string rawTerm in rhs.Split('#'))
                {
                    List<Literal> lits = new List<Literal>();
                    bool dead = false;
                    foreach (string tokRaw in rawTerm.Split('&'))
                    {
                        string tok = tokRaw.Trim();
                        if (tok.Length == 0) continue;
                        bool neg = tok.StartsWith("!");
                        string lit = neg ? tok.Substring(1).Trim() : tok;
                        if (lit == "GND") { dead = true; break; }   // constant-0 kills term
                        if (lit == "VCC") continue;                 // constant-1: no constraint
                        lits.Add(new Literal { Name = lit, Neg = neg });
                    }
                    if (!dead) e.Terms.Add(lits);
                }
                p.Equations.Add(e);
            }
            return p;
        }

        static int PinOr(Parsed p, string name)
        {
            int pin;
            return p.PinByName.TryGetValue(name, out pin) ? pin : 99;
        }

        static Gal BuildGal(string logicalName, Parsed p)
        {
            // Swept inputs = distinct literal names across all equations,
            // ordered by GAL pin ascending. BANKEN never appears in an equation.
            List<string> inputNames = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (Equation e in p.Equations)
                foreach (List<Literal> term in e.Terms)
                    foreach (Literal lit in term)
                        if (seen.Add(lit.Name)) inputNames.Add(lit.Name);
            inputNames.Sort((a, b) => PinOr(p, a).CompareTo(PinOr(p, b)));

            Dictionary<string, int> bitOf = new Dictionary<string, int>();
            for (int i = 0; i < inputNames.Count; i++) bitOf[inputNames[i]] = i;
            int n = inputNames.Count;
            int vectorCount = 1 << n;

            // Outputs = equations whose pin is an I/O pin (14..23), ordered by pin.
            List<Equation> outs = p.Equations
                .Where(e => PinOr(p, e.Name) >= 14 && PinOr(p, e.Name) <= 23)
                .OrderBy(e => p.PinByName[e.Name])
                .ToList();

            List<GalOut> outputs = new List<GalOut>();
            foreach (Equation e in outs)
            {
                byte[] level = new byte[vectorCount];
                for (int v = 0; v < vectorCount; v++)
                {
                    int sop = 0;
                    foreach (List<Literal> term in e.Terms)
                    {
                        int prod = 1;
                        foreach (Literal lit in term)
                        {
                            int bit = (v >> bitOf[lit.Name]) & 1;
                            if (lit.Neg) bit ^= 1;
                            if (bit == 0) { prod = 0; break; }
                        }
                        if (prod == 1) { sop = 1; break; }
                    }
                    level[v] = (byte)(e.ActiveLow ? (sop ^ 1) : sop);   // physical pin level
                }
                outputs.Add(new GalOut { Name = e.Name, Pin = Harness[p.PinByName[e.Name]], Level = level });
            }

            List<int> inPins = inputNames.Select(nm => Harness[p.PinByName[nm]]).ToList();
            return new Gal
            {
                LogicalName = logicalName,
                HasBanken = p.HasBanken,
                N = n,
                InputNames = inputNames,
                InPins = inPins,
                Outputs = outputs
            };
        }

        static string HexByteTable(byte[] level)
        {
            int nBytes = (level.Length + 7) / 8;
            List<string> parts = new List<string>();
            for (int b = 0; b < nBytes; b++)
            {
                int val = 0;
                for (int k = 0; k < 8; k++)
                {
                    int v = b * 8 + k;
                    if (v < level.Length && level[v] != 0) val |= (1 << k);
                }
                parts.Add("0x" + val.ToString("X2"));
            }
            List<string> lines = new List<string>();
            for (int i = 0; i < parts.Count; i += 12)
                lines.Add("  " + string.Join(", ", parts.Skip(i).Take(12)));
            return string.Join(",\n", lines);
        }

        static string EmitGal(Gal g)
        {
            string stem = g.LogicalName;
            StringBuilder o = new StringBuilder();
            o.Append("\n// ---- " + stem + " : " + g.N + " swept input(s) -> " + g.Outputs.Count + " output(s) ----\n");
            o.Append("// bit order (b0..b" + (g.N - 1) + "): " + (g.InputNames.Count > 0 ? string.Join(", ", g.InputNames) : "(none)") + "\n");
            foreach (GalOut ou in g.Outputs)
                o.Append("const uint8_t " + stem + "_" + ou.Name + "_TT[] PROGMEM = {\n" + HexByteTable(ou.Level) + "\n};\n");
            o.Append("const uint8_t " + stem + "_IN[] = { " + (g.InPins.Count > 0 ? string.Join(", ", g.InPins) : "0") + " };\n");
            o.Append("const GalOutput " + stem + "_OUT[] = {\n");
            o.Append(string.Join(",\n", g.Outputs.Select(ou => "  { " + ou.Pin + ", \"" + ou.Name + "\", " + stem + "_" + ou.Name + "_TT }")));
            o.Append("\n};\n");
            string legend = g.InputNames.Count > 0 ? string.Join(" ", g.InputNames) : "(none)";
            o.Append("const GalTest " + stem + " = { \"" + stem + "\", " + g.N + ", " + stem + "_IN, \"" + legend + "\", " + g.Outputs.Count + ", " + stem + "_OUT, " + (g.HasBanken ? "true" : "false") + " };\n");
            return o.ToString();
        }

        static string EmitMenuAndMain(List<Gal> gals)
        {
            StringBuilder s = new StringBuilder();
            s.Append("\n// =====================================================================\n// Menu / dispatch\n// =====================================================================\n");
            s.Append("const GalTest *const TESTS[] = { " + string.Join(", ", Order.Select(x => "&" + x)) + " };\n");
            s.Append("const char SELECT[] = \"" + string.Join("", SelCh) + "\";\n");
            s.Append("const uint8_t NTESTS = " + gals.Count + ";\n\n");
            s.Append("void printMenu() {\n");
            s.Append("  Serial.println(F(\"\\n==============================================\"));\n");
            s.Append("  Serial.println(F(\"Blinky-M rev 14 GAL burn tester\"));\n");
            s.Append("  Serial.println(F(\"Socket ONE chip FIRST, then choose:\"));\n");
            for (int i = 0; i < gals.Count; i++)
                s.Append("  Serial.println(F(\"  " + SelCh[i] + " = " + Order[i] + "\"));\n");
            s.Append("  Serial.println(F(\"  * = run every test in sequence\"));\n");
            s.Append("  Serial.println(F(\"(power down or reset before swapping chips)\"));\n");
            s.Append("  Serial.println(F(\"==============================================\"));\n");
            s.Append("}\n\n");
            s.Append("void setup() {\n  Serial.begin(115200);\n  idlePins();\n  printMenu();\n}\n\n");
            s.Append("void loop() {\n");
            s.Append("  if (!Serial.available()) return;\n");
            s.Append("  char ch = Serial.read();\n");
            s.Append("  if (ch >= 'a' && ch <= 'z') ch -= 32;   // accept lower-case keys\n");
            s.Append("  if (ch == '*') { for (uint8_t i = 0; i < NTESTS; i++) runGal(*TESTS[i]); printMenu(); return; }\n");
            s.Append("  for (uint8_t i = 0; i < NTESTS; i++) {\n");
            s.Append("    if (ch == SELECT[i]) { runGal(*TESTS[i]); printMenu(); return; }\n");
            s.Append("  }\n");
            s.Append("  if (ch > ' ') { Serial.println(F(\"Unknown key.\")); printMenu(); }\n");
            s.Append("}\n");
            return s.ToString();
        }

        // Parses every PLD, builds the tester text, and reports a per-GAL
        // summary through the supplied logger.
        static string Generate(string pldDir, Action<string> log)
        {
            Dictionary<string, Gal> byName = new Dictionary<string, Gal>();
            foreach (KeyValuePair<string, string> kv in FileMap)
            {
                string path = Path.Combine(pldDir, kv.Key + ".pld");
                if (!File.Exists(path)) throw new FileNotFoundException("Missing PLD: " + path);
                Parsed parsed = ParsePld(File.ReadAllText(path));
                byName[kv.Value] = BuildGal(kv.Value, parsed);
            }

            List<Gal> gals = Order.Select(nm => byName[nm]).ToList();

            StringBuilder file = new StringBuilder();
            file.Append(string.Join("\n", Boilerplate));
            foreach (Gal g in gals) file.Append(EmitGal(g));
            file.Append(EmitMenuAndMain(gals));

            foreach (Gal g in gals)
            {
                int vectors = 1 << g.N;
                log(g.LogicalName.PadRight(10) + " inputs=" + g.N
                    + " (" + string.Join(",", g.InputNames) + ")"
                    + "  vectors=" + vectors
                    + "  outputs=" + string.Join(",", g.Outputs.Select(o => o.Name))
                    + "  banken=" + (g.HasBanken ? "true" : "false"));
            }
            return file.ToString();
        }
    }
}