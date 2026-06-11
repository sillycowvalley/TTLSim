// =====================================================================
// BlinkyGalTest -- exhaustive in-circuit test of the four Mini Blinky
// decode GALs (GAL16V8D) on an Arduino Mega 2560.
//
// Wiring: per Mega_GAL_harness.png (bottom-edge header, outer row).
// The expected values are computed from the same reference model that
// verified the JEDEC fuse maps, so a PASS here means the physical chip
// agrees with the master document's control tables.
//
// Usage: socket a chip, open Serial Monitor at 115200, type 1..4.
//   GAL 1 = ALU / shift select        GAL 2 = stack-top + D-mem
//   GAL 3 = pointers + clock          GAL 4 = control flow + I/O + Cn
// Power down (or at least press reset) before swapping chips.
// GAL 4 must be the REVISION 02 burn (WRPH on pin 1, N/Z/C on 11/12/13).
//
// Note: the reference-model code lives inside structs (Decode, Expect)
// rather than as free functions. This is deliberate: the Arduino IDE
// auto-generates prototypes for free functions and inserts them ABOVE
// the type definitions, which breaks any signature using a user type.
// Member functions are exempt. Do not refactor them back out.
// =====================================================================

// ------------------ harness pin map (GAL pin -> Mega pin) ------------
const uint8_t PIN_WRPH   = 53;                    // GAL pin 1
const uint8_t PIN_OP[4]  = { 51, 49, 47, 45 };    // GAL pins 2..5  = OP0..OP3
const uint8_t PIN_LIT[4] = { 43, 41, 39, 37 };    // GAL pins 6..9  = L0..L3
const uint8_t PIN_OUT[6] = { 35, 33, 31, 29, 27, 25 }; // GAL pins 14..19 = OUT0..OUT5
const uint8_t PIN_N      = 23;                    // GAL pin 11
const uint8_t PIN_Z      = 22;                    // GAL pin 12 (series 1k)
const uint8_t PIN_C      = 24;                    // GAL pin 13 (series 1k)

// ------------------ per-GAL definitions ------------------------------
// outName[k] is the signal on GAL pin 14+k. pin12/pin13 name the signals
// on GAL pins 12/13 when the GAL drives them (GAL 1 & 2); nullptr = not
// compared. megaDrivesPins1213: true = Mega drives D22/D24 (GAL 3 spares
// held low, GAL 4 flag inputs); false = Mega reads them (GAL 1 & 2).
// envCount: how many WRPH/N/Z/C combinations to sweep (1, 2 or 16).
struct GalDef {
  const char *name;
  const char *outName[6];
  const char *pin12Name;     // GAL pin 12 = D22
  const char *pin13Name;     // GAL pin 13 = D24
  bool megaDrivesPins1213;
  uint8_t envCount;
};

const GalDef GALS[4] = {
  { "GAL1 ALU/shift",
    { "C_SRC", "ALU_M", "ALU_S3", "ALU_S2", "ALU_S1", "ALU_S0" },
    "TOS_M1", "TOS_M0", false, 1 },
  { "GAL2 stack-top/D-mem",
    { "FLAG_WE", "NOS_SRC", "NOS_LD", "TOS_SRC2", "TOS_SRC1", "TOS_SRC0" },
    "DMEM_WE", "DMEM_CS", false, 2 },                 // sweeps WRPH
  { "GAL3 pointers/clock",
    { "CLK_RUN", "RDIN_SEL", "RSP_UD", "RSP_EN", "DSP_UD", "DSP_EN" },
    nullptr, nullptr, true, 1 },
  { "GAL4 flow/IO/Cn",
    { "IOADDR_SEL", "IO_WR", "IO_RD", "ALU_CN", "PCSEL1", "PCSEL0" },
    nullptr, nullptr, true, 16 },                     // sweeps N,Z,C,WRPH
};

// ------------------ instruction set (ratified encoding) --------------
enum class Ins : uint8_t {
  Nop, Halt, Ret, Dup, Drop, Swap, ToR, RFrom,        // SYS 0..7
  Add, Adc, Sub, Xor, Not, Tst, Shl, Shr,             // ALU 0..7
  Push, Load, Store, In, Out, Branch, Beq, Bcs, Bmi, Call,
  Idle                                                // reserved op/nibble
};

const char *const SYS_NAMES[8] = { "NOP", "HALT", "RET", "DUP", "DROP", "SWAP", ">R", "R>" };
const char *const ALU_NAMES[8] = { "ADD", "ADC", "SUB", "XOR", "NOT", "TST", "SHL", "SHR" };
const char *const TOP_NAMES[16] = { "SYS", "ALU", "PUSH", "LOAD", "STORE", "IN", "OUT",
                                    "BRANCH", "BEQ", "BCS", "BMI", "CALL",
                                    "rsvd", "rsvd", "rsvd", "rsvd" };

// Instruction classification, mirroring the master doc's control tables.
struct Decode {
  static Ins instrOf(uint8_t op, uint8_t nib) {
    if (op == 0) return nib < 8 ? (Ins)nib : Ins::Idle;
    if (op == 1) return nib < 8 ? (Ins)(8 + nib) : Ins::Idle;
    switch (op) {
      case 2: return Ins::Push;   case 3: return Ins::Load;
      case 4: return Ins::Store;  case 5: return Ins::In;
      case 6: return Ins::Out;    case 7: return Ins::Branch;
      case 8: return Ins::Beq;    case 9: return Ins::Bcs;
      case 10: return Ins::Bmi;   case 11: return Ins::Call;
      default: return Ins::Idle;
    }
  }
  static bool isAluFamily(Ins i) { return i >= Ins::Add && i <= Ins::Shr; }  // sets flags
  static bool is181Op(Ins i)     { return i >= Ins::Add && i <= Ins::Tst; }  // drives the '181
  static bool tosLoads(Ins i) {                                              // TOS_MODE = load
    switch (i) {
      case Ins::Drop: case Ins::Swap: case Ins::ToR: case Ins::RFrom:
      case Ins::Add: case Ins::Adc: case Ins::Sub: case Ins::Xor: case Ins::Not:
      case Ins::Push: case Ins::Load: case Ins::Store: case Ins::In: case Ins::Out:
        return true;
      default: return false;
    }
  }
  static bool nosLoads(Ins i) {
    switch (i) {
      case Ins::Dup: case Ins::Drop: case Ins::Swap: case Ins::ToR: case Ins::RFrom:
      case Ins::Add: case Ins::Adc: case Ins::Sub: case Ins::Xor:
      case Ins::Push: case Ins::Load: case Ins::Store: case Ins::Out:
        return true;
      default: return false;
    }
  }
  static bool nosFromTos(Ins i) {
    return i == Ins::Dup || i == Ins::Swap || i == Ins::RFrom ||
           i == Ins::Push || i == Ins::Load;
  }
  static bool dspPush(Ins i) {
    return i == Ins::Dup || i == Ins::RFrom || i == Ins::Push || i == Ins::Load;
  }
  static bool dspPop(Ins i) {
    switch (i) {
      case Ins::Drop: case Ins::ToR: case Ins::Add: case Ins::Adc:
      case Ins::Sub: case Ins::Xor: case Ins::Store: case Ins::Out: return true;
      default: return false;
    }
  }
  static bool rspUp(Ins i)   { return i == Ins::ToR || i == Ins::Call; }
  static bool rspDown(Ins i) { return i == Ins::Ret || i == Ins::RFrom; }
};

const char *instrName(uint8_t instr) {
  uint8_t op = instr >> 4, nib = instr & 0x0F;
  if (op == 0) return nib < 8 ? SYS_NAMES[nib] : "SYS rsvd";
  if (op == 1) return nib < 8 ? ALU_NAMES[nib] : "ALU rsvd";
  return TOP_NAMES[op];
}

// ------------------ expected values (the reference model) ------------
struct Expect {
  uint8_t outMask;   // bit k set = OUT[k] is specified (not don't-care)
  uint8_t outVal;    // expected pin level for specified bits
  bool p12Specified, p12;   // GAL pin 12 (only checked on GAL 1 & 2)
  bool p13Specified, p13;   // GAL pin 13

  void set(uint8_t k, bool v) {
    outMask |= (1 << k);
    if (v) outVal |= (1 << k);
  }

  static Expect compute(uint8_t g, uint8_t instr, bool n, bool z, bool c, bool wrph) {
    Expect e = { 0, 0, false, false, false, false };
    Ins i = Decode::instrOf(instr >> 4, instr & 0x0F);
    bool load = Decode::tosLoads(i);

    if (g == 0) {                                 // ---- GAL 1 ----
      // OUT0 C_SRC, OUT1 ALU_M, OUT2..5 = ALU_S3..S0; p12 TOS_M1, p13 TOS_M0
      if (Decode::isAluFamily(i)) e.set(0, i == Ins::Shl || i == Ins::Shr);
      if (Decode::is181Op(i)) {
        uint8_t s3, s2, s1, s0; bool m;
        switch (i) {
          case Ins::Add: case Ins::Adc: s3 = 1; s2 = 0; s1 = 0; s0 = 1; m = 0; break;
          case Ins::Sub:                s3 = 0; s2 = 1; s1 = 1; s0 = 0; m = 0; break;
          case Ins::Xor:                s3 = 0; s2 = 1; s1 = 1; s0 = 0; m = 1; break;
          case Ins::Not:                s3 = 0; s2 = 0; s1 = 0; s0 = 0; m = 1; break;
          default:  /* Tst */           s3 = 1; s2 = 1; s1 = 1; s0 = 1; m = 1; break;
        }
        e.set(1, m); e.set(2, s3); e.set(3, s2); e.set(4, s1); e.set(5, s0);
      }
      e.p12Specified = true; e.p12 = load || i == Ins::Shl;   // '194 S1
      e.p13Specified = true; e.p13 = load || i == Ins::Shr;   // '194 S0
    }
    else if (g == 1) {                            // ---- GAL 2 ----
      // OUT0 FLAG_WE, OUT1 NOS_SRC, OUT2 NOS_LD, OUT3..5 TOS_SRC2..0;
      // p12 DMEM_WE (active low), p13 DMEM_CS (active low)
      e.set(0, Decode::isAluFamily(i));
      e.set(2, Decode::nosLoads(i));
      if (Decode::nosLoads(i)) e.set(1, Decode::nosFromTos(i));
      if (load) {
        uint8_t code;                             // NOS=0 ALU=1 LIT=2 DMEM=3 INP=4 RTOS=5
        switch (i) {
          case Ins::RFrom: code = 5; break;
          case Ins::Add: case Ins::Adc: case Ins::Sub: case Ins::Xor: case Ins::Not:
            code = 1; break;
          case Ins::Push: code = 2; break;
          case Ins::Load: code = 3; break;
          case Ins::In:   code = 4; break;
          default:        code = 0; break;        // Drop, Swap, ToR, Store, Out
        }
        e.set(3, (code >> 2) & 1); e.set(4, (code >> 1) & 1); e.set(5, code & 1);
      }
      e.p12Specified = true; e.p12 = !(i == Ins::Store && wrph);            // /WE
      e.p13Specified = true; e.p13 = !(i == Ins::Load || i == Ins::Store);  // /CS
    }
    else if (g == 2) {                            // ---- GAL 3 ----
      // OUT0 CLK_RUN, OUT1 RDIN_SEL, OUT2 RSP_UD, OUT3 RSP_EN(/),
      // OUT4 DSP_UD, OUT5 DSP_EN(/)
      e.set(0, i != Ins::Halt);
      if (i == Ins::Call) e.set(1, 1); else if (i == Ins::ToR) e.set(1, 0);
      bool ru = Decode::rspUp(i), rd = Decode::rspDown(i);
      e.set(3, !(ru || rd));
      if (ru) e.set(2, 1); else if (rd) e.set(2, 0);
      bool pu = Decode::dspPush(i), po = Decode::dspPop(i);
      e.set(5, !(pu || po));
      if (pu) e.set(4, 1); else if (po) e.set(4, 0);
    }
    else {                                        // ---- GAL 4 ----
      // OUT0 IOADDR_SEL, OUT1 IO_WR(/), OUT2 IO_RD(/), OUT3 ALU_CN,
      // OUT4 PCSEL1, OUT5 PCSEL0
      if (i != Ins::Halt) {                       // HALT: PCSEL don't-care
        bool sel0 = false, sel1 = false;
        if (i == Ins::Ret) sel1 = true;
        else if (i == Ins::Branch || i == Ins::Call) sel0 = true;
        else if (i == Ins::Beq) sel0 = z;
        else if (i == Ins::Bcs) sel0 = c;
        else if (i == Ins::Bmi) sel0 = n;
        e.set(5, sel0); e.set(4, sel1);
      }
      if (i == Ins::Add) e.set(3, 1);
      else if (i == Ins::Sub) e.set(3, 0);
      else if (i == Ins::Adc) e.set(3, !c);
      e.set(2, i != Ins::In);
      e.set(1, !(i == Ins::Out && wrph));
      if (i == Ins::Out) e.set(0, 1); else if (i == Ins::In) e.set(0, 0);
    }
    return e;
  }
};

// ------------------ harness driving ----------------------------------
void configurePins(uint8_t g) {
  pinMode(PIN_WRPH, OUTPUT); digitalWrite(PIN_WRPH, LOW);
  pinMode(PIN_N, OUTPUT);    digitalWrite(PIN_N, LOW);
  for (uint8_t k = 0; k < 4; k++) { pinMode(PIN_OP[k], OUTPUT); pinMode(PIN_LIT[k], OUTPUT); }
  for (uint8_t k = 0; k < 6; k++) pinMode(PIN_OUT[k], INPUT);
  if (GALS[g].megaDrivesPins1213) {
    pinMode(PIN_Z, OUTPUT); digitalWrite(PIN_Z, LOW);
    pinMode(PIN_C, OUTPUT); digitalWrite(PIN_C, LOW);
  } else {
    pinMode(PIN_Z, INPUT);
    pinMode(PIN_C, INPUT);
  }
}

void idlePins() {                  // safe state for chip swapping
  pinMode(PIN_Z, INPUT);
  pinMode(PIN_C, INPUT);
}

void applyVector(uint8_t g, uint8_t instr, bool n, bool z, bool c, bool wrph) {
  for (uint8_t k = 0; k < 4; k++) {
    digitalWrite(PIN_OP[k], (instr >> (4 + k)) & 1);
    digitalWrite(PIN_LIT[k], (instr >> k) & 1);
  }
  digitalWrite(PIN_WRPH, wrph);
  digitalWrite(PIN_N, n);
  if (GALS[g].megaDrivesPins1213) {
    digitalWrite(PIN_Z, z);
    digitalWrite(PIN_C, c);
  }
}

// ------------------ reporting ----------------------------------------
const uint8_t MAX_REPORT = 24;
uint32_t failures, checks;

void reportFail(uint8_t g, uint8_t instr, bool n, bool z, bool c, bool wrph,
                const char *signalName, bool expected, bool got) {
  failures++;
  if (failures > MAX_REPORT) return;
  Serial.print(F("FAIL instr=0x"));
  if (instr < 0x10) Serial.print('0');
  Serial.print(instr, HEX);
  Serial.print(F(" (")); Serial.print(instrName(instr)); Serial.print(F(")"));
  if (GALS[g].envCount > 1) {
    Serial.print(F(" WRPH=")); Serial.print(wrph);
    if (GALS[g].envCount > 2) {
      Serial.print(F(" N=")); Serial.print(n);
      Serial.print(F(" Z=")); Serial.print(z);
      Serial.print(F(" C=")); Serial.print(c);
    }
  }
  Serial.print(F("  ")); Serial.print(signalName);
  Serial.print(F(": expected ")); Serial.print(expected);
  Serial.print(F(" got ")); Serial.println(got);
}

// ------------------ the test -----------------------------------------
void runTest(uint8_t g) {
  const GalDef &def = GALS[g];
  Serial.print(F("\nTesting ")); Serial.print(def.name);
  Serial.print(F(" -- 256 instructions x ")); Serial.print(def.envCount);
  Serial.println(F(" flag/WRPH state(s)..."));
  configurePins(g);
  failures = 0; checks = 0;
  unsigned long t0 = millis();

  for (uint16_t iv = 0; iv < 256; iv++) {
    uint8_t instr = (uint8_t)iv;
    for (uint8_t env = 0; env < def.envCount; env++) {
      bool wrph = env & 1, n = (env >> 1) & 1, z = (env >> 2) & 1, c = (env >> 3) & 1;
      applyVector(g, instr, n, z, c, wrph);
      delayMicroseconds(2);                      // ~130x the 15 ns tpd

      Expect e = Expect::compute(g, instr, n, z, c, wrph);
      for (uint8_t k = 0; k < 6; k++) {
        if (!(e.outMask & (1 << k))) continue;   // don't-care: unchecked
        bool got = digitalRead(PIN_OUT[k]);
        bool want = (e.outVal >> k) & 1;
        checks++;
        if (got != want) reportFail(g, instr, n, z, c, wrph, def.outName[k], want, got);
      }
      if (!def.megaDrivesPins1213) {
        if (e.p12Specified) {
          bool got = digitalRead(PIN_Z);
          checks++;
          if (got != e.p12) reportFail(g, instr, n, z, c, wrph, def.pin12Name, e.p12, got);
        }
        if (e.p13Specified) {
          bool got = digitalRead(PIN_C);
          checks++;
          if (got != e.p13) reportFail(g, instr, n, z, c, wrph, def.pin13Name, e.p13, got);
        }
      }
    }
  }

  unsigned long elapsed = millis() - t0;
  if (failures > MAX_REPORT) {
    Serial.print(F("... and ")); Serial.print(failures - MAX_REPORT);
    Serial.println(F(" more failures not shown."));
  }
  Serial.print(F("Checked ")); Serial.print(checks);
  Serial.print(F(" specified output values in ")); Serial.print(elapsed);
  Serial.println(F(" ms."));
  if (failures == 0) {
    Serial.print(F("*** PASS: ")); Serial.print(def.name);
    Serial.println(F(" matches the control tables. ***"));
  } else {
    Serial.print(F("*** FAIL: ")); Serial.print(failures);
    Serial.println(F(" mismatches. ***"));
  }
  idlePins();
}

// ------------------ menu ---------------------------------------------
void printMenu() {
  Serial.println(F("\n=============================================="));
  Serial.println(F("Mini Blinky decode GAL tester"));
  Serial.println(F("Socket the chip FIRST, then choose:"));
  for (uint8_t g = 0; g < 4; g++) {
    Serial.print(F("  ")); Serial.print(g + 1);
    Serial.print(F(" = ")); Serial.println(GALS[g].name);
  }
  Serial.println(F("(power down or reset before swapping chips)"));
  Serial.println(F("=============================================="));
}

void setup() {
  Serial.begin(115200);
  idlePins();
  printMenu();
}

void loop() {
  if (!Serial.available()) return;
  char ch = Serial.read();
  if (ch >= '1' && ch <= '4') runTest(ch - '1');
  else if (ch > ' ') Serial.println(F("Type 1, 2, 3 or 4."));
  if (ch > ' ') printMenu();
}
