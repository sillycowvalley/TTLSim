// ============================================================================
//  ALUHarness.ino  -  Teensy 4.1 adapter test harness for the 8-bit TTL ALU
//
//  DUT (TTLSim "8_Bit_ALU", datapath layer), AS CURRENTLY BUILT:
//    U64 AL / U66 BL   '574  A and B registers, shared CLK_A rising edge
//    U63 IML           '541  immediate buffer, INPUTS HARDWIRED TO 0xA5,
//                            output to BBUS via /IMMOE
//    U50/U49           '86   SUB XOR stage on the B operand
//    U54/U53           '283  8-bit ripple adder pair, CIN in, COUT out
//    U44 ZDLO          '688  zero detect -> /EQL (low when D == 0)
//    U46 FMUX/U45 FLAGS '157+'74 flag register (Z, C), clocked by CLK_A
//
//  Operation: D = A + (Beff XOR SUB) + CIN, where Beff is the B register
//  (/BOE low), the CONSTANT 0xA5 via the '541 (/IMMOE low), or 0x00 via the
//  RN5 pulldowns when both enables are high.
//
//  Needs AdapterPorts.h (unmodified) in the same sketch folder.
//
//  --------------------------------------------------------------------------
//  Wiring AS BUILT (bit 0 = white ... bit 7 = red within each port group):
//
//    Port D (OUT) -> QA0..QA7            (A register D inputs, U64 pins 2..9)
//    Port C (OUT) -> QB0..QB7            (B register D inputs, U66 pins 2..9;
//                                         the old IR bridge is removed,
//                                         U63 inputs are strapped to 0xA5)
//    Port B (IN)  <- D0..D7              (result bus, '283 sum outputs)
//    Port A mixed:
//      PA0 white  OUT -> CLK_A           (U64 pin 11 net: U66 p11, U45 p3/p11)
//      PA1 grey   OUT -> SUB             (U50 pin 2; chained '86 inputs)
//      PA2 violet OUT -> CIN             (U54 pin 7)
//      PA3 blue   OUT -> /BOE            (U66 pin 1)
//      PA4 green  OUT -> /IMMOE          (U63 pin 1)
//      PA5 yellow IN  <- COUT            (U53 pin 9)
//      PA6 orange IN  <- /EQL            (U44 pin 19)
//      PA7 red    IN  <- Z flag          (U45 pin 5)
//
//  Straps on the DUT:
//    /AOE   -> GND   (U64 pin 1)
//    FLAGEN -> VCC   (U46 pin 1)
//    ZNEW   -> /EQL  (U46 pin 3 to U44 pin 19; Z latches ACTIVE-LOW zero)
//    U63 inputs (pins 2..9) -> hardwired pattern 0xA5
//
//  Commands (Serial, non-blocking, case-insensitive):
//    C                       connectivity check - per-wire, gentle, verbose.
//                            Run this first on a suspect rig; failures name
//                            the wire (bit, ribbon colour, DUT pin).
//    P                       operand-path static sweeps via the B register,
//                            256 values at A=00 and again at A=FF (the FF
//                            sweep forces a full carry ripple every step)
//    T                       full register-path test (262,144 vectors)
//    I                       immediate-constant test: 256 A values x 4
//                            SUB/CIN combos against Beff=A5, PLUS a
//                            /BOE handover check after every vector
//    F                       bus-release test (pulldowns -> Beff 0x00)
//    M <A> <B> <src> <s> <c> manual single op with pass/fail
//    R <A> <B> <src> <s> <c> repeatability probe (100 identical runs;
//                            operand lines only slew on run 1)
//    S <A> <B> <src> <s> <c> static probe: apply once, monitor until X
//    G <microseconds>        inter-vector gap for T/I/F and between R runs
//    U <microseconds>        operand-to-clock setup time, default 1;
//                            port writes staggered (A, wait, B, wait, clock)
//    X                       abort test / stop monitor
//    H                       help
//    SW1 starts the full test, SW2 aborts.
//
//  Args for M/R/S: A,B hex bytes; src R (register), I (immediate constant -
//  the B argument is ignored, Beff is always A5), F (float); s,c = SUB,CIN
//  as 0/1.   Example:  R 3C 5A R 0 0
// ============================================================================

#include "AdapterPorts.h"

// ------------------------------------------------- control/status on port A
constexpr uint8_t PIN_CLK     = PIN_PA0;  // out: CLK_A, rising edge
constexpr uint8_t PIN_SUB     = PIN_PA1;  // out: SUB
constexpr uint8_t PIN_CIN     = PIN_PA2;  // out: CIN
constexpr uint8_t PIN_BOE_N   = PIN_PA3;  // out: /BOE
constexpr uint8_t PIN_IMMOE_N = PIN_PA4;  // out: /IMMOE
constexpr uint8_t PIN_COUT    = PIN_PA5;  // in : COUT
constexpr uint8_t PIN_EQL_N   = PIN_PA6;  // in : /EQL
constexpr uint8_t PIN_ZFLAG   = PIN_PA7;  // in : Z flag (latched /EQL)

// The '541's inputs are hardwired on the DUT: /IMMOE low puts this constant
// on BBUS regardless of anything the Teensy drives.
constexpr uint8_t immConstant = 0xA5;

// ------------------------------------------------------------------- timing
constexpr uint32_t clockHighMicros = 1;
constexpr uint32_t settleMicros    = 5;
constexpr uint32_t gapMicrosMax    = 1000000UL;
constexpr uint32_t setupMicrosMax  = 10000UL;

static uint32_t gapMicros   = 0;    // inter-vector gap, 'G'
static uint32_t setupMicros = 1;    // operand-to-clock setup per stage, 'U'

enum BSource : uint8_t { BSRC_REG, BSRC_IMM, BSRC_FLOAT };

struct AluSample {
  uint8_t d;
  bool    cout;
  bool    eqlN;
  bool    zflag;
};

// ------------------------------------------------------------ test run state
enum RunState : uint8_t { RS_IDLE, RS_FULL, RS_IMM, RS_FLOAT };

constexpr uint32_t vectorsPerSliceFast = 512;
constexpr uint32_t vectorsPerSliceSlow = 32;
constexpr uint32_t maxFailPrints       = 20;

static RunState      runState       = RS_IDLE;
static uint32_t      vecIndex       = 0;
static uint32_t      vecTotal       = 0;
static uint32_t      failCount      = 0;
static uint32_t      checkCount     = 0;
static unsigned long runStartMillis = 0;

static bool     monitorActive = false;
static uint16_t monitorLast   = 0;

static char    lineBuf[48];
static uint8_t lineLen  = 0;
static bool    lastSw1  = false;
static bool    lastSw2  = false;

// ------------------------------------------------------------ low-level ops

void setBSource(BSource src) {
  // Release both first - /BOE and /IMMOE must never be low together.
  digitalWriteFast(PIN_BOE_N, HIGH);
  digitalWriteFast(PIN_IMMOE_N, HIGH);
  if (src == BSRC_REG) {
    digitalWriteFast(PIN_BOE_N, LOW);
  } else if (src == BSRC_IMM) {
    digitalWriteFast(PIN_IMMOE_N, LOW);
  }
}

void pulseClock() {
  digitalWriteFast(PIN_CLK, HIGH);
  delayMicroseconds(clockHighMicros);
  digitalWriteFast(PIN_CLK, LOW);
}

void setupWait() {
  uint32_t remaining = setupMicros;
  while (remaining > 0) {
    uint32_t chunk = remaining > 1000 ? 1000 : remaining;
    delayMicroseconds(chunk);
    remaining -= chunk;
  }
}

// Load the A and B registers ('574s share the clock, different D buses).
void loadRegisters(uint8_t a, uint8_t bReg) {
  writePortD(a);
  setupWait();
  writePortC(bReg);
  setupWait();
  pulseClock();
}

AluSample sampleAlu() {
  AluSample s;
  s.d     = readPortB();
  s.cout  = digitalReadFast(PIN_COUT) == HIGH;
  s.eqlN  = digitalReadFast(PIN_EQL_N) == HIGH;
  s.zflag = digitalReadFast(PIN_ZFLAG) == HIGH;
  return s;
}

uint16_t packSample(const AluSample &s) {
  return (uint16_t)s.d
       | (uint16_t)(s.cout  ? 0x100 : 0)
       | (uint16_t)(s.eqlN  ? 0x200 : 0)
       | (uint16_t)(s.zflag ? 0x400 : 0);
}

void captureFlags(AluSample &s) {
  pulseClock();
  delayMicroseconds(2);
  s.zflag = digitalReadFast(PIN_ZFLAG) == HIGH;
}

void setMode(bool sub, bool cin) {
  digitalWriteFast(PIN_SUB, sub ? HIGH : LOW);
  digitalWriteFast(PIN_CIN, cin ? HIGH : LOW);
}

void idleBus() {
  setBSource(BSRC_FLOAT);
  setMode(false, false);
  writePortD(0);
  writePortC(0);
}

void interVectorGap() {
  uint32_t remaining = gapMicros;
  while (remaining > 0) {
    uint32_t chunk = remaining > 1000 ? 1000 : remaining;
    delayMicroseconds(chunk);
    remaining -= chunk;
  }
}

// Apply one complete vector from scratch. Returns the effective B operand.
// For BSRC_IMM the b argument still loads the B register (useful contrast
// value) but Beff is the hardwired constant.
uint8_t applyVector(uint8_t a, uint8_t b, BSource src, bool sub, bool cin) {
  uint8_t bEff = b;
  setMode(sub, cin);
  setBSource(BSRC_REG);
  loadRegisters(a, b);
  if (src == BSRC_IMM) {
    setBSource(BSRC_IMM);
    bEff = immConstant;
  } else if (src == BSRC_FLOAT) {
    setBSource(BSRC_FLOAT);
    bEff = 0x00;
  }
  delayMicroseconds(settleMicros);
  return bEff;
}

// ------------------------------------------------------------- expectations

void expectedAlu(uint8_t a, uint8_t bEff, bool sub, bool cin,
                 uint8_t &dExp, bool &coutExp) {
  uint8_t  x   = sub ? (uint8_t)~bEff : bEff;
  uint16_t sum = (uint16_t)a + (uint16_t)x + (cin ? 1 : 0);
  dExp    = (uint8_t)sum;
  coutExp = (sum & 0x100u) != 0;
}

// --------------------------------------------------------------- reporting

void printHex2(uint8_t v) {
  if (v < 0x10) Serial.print('0');
  Serial.print(v, HEX);
}

void printSample(const AluSample &s) {
  Serial.print("D=");     printHex2(s.d);
  Serial.print(" COUT="); Serial.print(s.cout ? 1 : 0);
  Serial.print(" /EQL="); Serial.print(s.eqlN ? 1 : 0);
  Serial.print(" Z=");    Serial.print(s.zflag ? 1 : 0);
}

void reportFail(const char *tag, uint8_t a, uint8_t bEff, bool sub, bool cin,
                const AluSample &s, uint8_t dExp, bool coutExp, bool haveZ) {
  failCount++;
  if (failCount > maxFailPrints) {
    if (failCount == maxFailPrints + 1) {
      Serial.println("(further failures suppressed)");
    }
    return;
  }
  Serial.print("FAIL ");
  Serial.print(tag);
  Serial.print(" A=");    printHex2(a);
  Serial.print(" Beff="); printHex2(bEff);
  Serial.print(" SUB=");  Serial.print(sub ? 1 : 0);
  Serial.print(" CIN=");  Serial.print(cin ? 1 : 0);
  Serial.print(" -> ");
  if (haveZ) {
    printSample(s);
  } else {
    Serial.print("D=");     printHex2(s.d);
    Serial.print(" COUT="); Serial.print(s.cout ? 1 : 0);
    Serial.print(" /EQL="); Serial.print(s.eqlN ? 1 : 0);
  }
  Serial.print("  expected D="); printHex2(dExp);
  Serial.print(" COUT=");        Serial.print(coutExp ? 1 : 0);
  Serial.print(" /EQL=");        Serial.println(dExp != 0 ? 1 : 0);
}

bool checkSample(const char *tag, uint8_t a, uint8_t bEff, bool sub, bool cin,
                 const AluSample &s, bool haveZ) {
  uint8_t dExp;
  bool    coutExp;
  expectedAlu(a, bEff, sub, cin, dExp, coutExp);
  bool eqlExp = (dExp != 0);
  bool ok = (s.d == dExp) && (s.cout == coutExp) && (s.eqlN == eqlExp);
  if (haveZ) {
    ok = ok && (s.zflag == eqlExp);
  }
  checkCount++;
  if (!ok) {
    reportFail(tag, a, bEff, sub, cin, s, dExp, coutExp, haveZ);
  }
  return ok;
}

// ======================================================= CONNECTIVITY CHECK
// Every step is isolated with milliseconds of quiet. Phase 1 walks Port C
// through the B register (the '541 no longer sees Port C).

static const char *const ribbonColour[8] =
  { "white", "grey", "violet", "blue", "green", "yellow", "orange", "red" };

// '283 sum output package pins in bit order (S1..S4 = pins 4,1,13,10)
static const uint8_t sumPins[4] = { 4, 1, 13, 10 };

static uint16_t connFails = 0;

void connQuiet() { delay(3); }

void gentleClock() {
  connQuiet();
  pulseClock();
  connQuiet();
}

int8_t singleBitIndex(uint8_t v) {           // -1 if not exactly one bit
  for (int8_t n = 0; n < 8; n++) {
    if (v == (uint8_t)(1u << n)) return n;
  }
  return -1;
}

void printPortCWire(uint8_t n) {
  Serial.print("PC");
  Serial.print(n);
  Serial.print(" (");
  Serial.print(ribbonColour[n]);
  Serial.print(") -> U66 pin ");
  Serial.print(2 + n);
}

void printPortDWire(uint8_t n) {
  Serial.print("PD");
  Serial.print(n);
  Serial.print(" (");
  Serial.print(ribbonColour[n]);
  Serial.print(") -> U64 pin ");
  Serial.print(2 + n);
}

void printDBusWire(uint8_t n) {
  Serial.print("PB");
  Serial.print(n);
  Serial.print(" (");
  Serial.print(ribbonColour[n]);
  Serial.print(") <- ");
  Serial.print(n < 4 ? "U54" : "U53");
  Serial.print(" pin ");
  Serial.print(sumPins[n & 3]);
}

void connResult(bool pass) {
  if (pass) {
    Serial.println("  PASS");
  } else {
    Serial.println("  *** FAIL ***");
    connFails++;
  }
}

// Analyse a wrong response: which D-bus bits differ from expected. These
// name where the wrong bit was OBSERVED - the cause may be upstream in the
// logic cone feeding that sum bit.
void analyseBitFault(uint8_t got, uint8_t expected) {
  uint8_t diff = got ^ expected;
  Serial.print("    got ");
  printHex2(got);
  Serial.print(", expected ");
  printHex2(expected);
  Serial.print(", differing D-bus bits:");
  for (uint8_t n = 0; n < 8; n++) {
    if (diff & (1u << n)) {
      Serial.print("  [");
      printDBusWire(n);
      Serial.print("]");
    }
  }
  Serial.println();
}

void connectivityCheck() {
  connFails = 0;
  Serial.println();
  Serial.println("=== CONNECTIVITY CHECK (isolated gentle steps) ===");

  // Known starting state: registers loaded with zero, source floating.
  setMode(false, false);
  setBSource(BSRC_FLOAT);
  writePortD(0x00);
  writePortC(0x00);
  gentleClock();                      // A=00, B=00

  // ---- Phase 0: baseline sanity in float mode: D must read 00 ----
  Serial.println();
  Serial.println("Phase 0: baseline (A=00, Beff=00) - expects D=00, /EQL=0");
  connQuiet();
  {
    uint8_t d0 = readPortB();
    bool eql0  = digitalReadFast(PIN_EQL_N) == HIGH;
    Serial.print("  D=");
    printHex2(d0);
    Serial.print(" /EQL=");
    Serial.print(eql0 ? 1 : 0);
    bool pass = (d0 == 0x00) && !eql0;
    connResult(pass);
    if (d0 != 0x00) {
      Serial.println("    non-zero baseline: stuck D-bus read bit(s), stuck register");
      Serial.println("    bit(s), or CLK never loaded the registers (see Phase 2):");
      analyseBitFault(d0, 0x00);
    }
  }

  // ---- Phase 1: Port C -> B register -> BBUS -> XOR -> adders -> D ----
  Serial.println();
  Serial.println("Phase 1: Port C (QB) walk via B register - one gentle clock each");
  Serial.println("  D should equal A(=00) + pattern exactly");
  setBSource(BSRC_REG);
  writePortC(0x00);
  gentleClock();
  connQuiet();
  uint8_t base = readPortB();
  Serial.print("  baseline (pattern 00): D=");
  printHex2(base);
  connResult(base == 0x00);

  static const uint8_t walkPatterns[11] =
    { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x55, 0xAA, 0xFF };

  for (uint8_t i = 0; i < 11; i++) {
    uint8_t x = walkPatterns[i];
    writePortC(x);
    gentleClock();
    connQuiet();
    uint8_t d = readPortB();
    uint8_t delta = (uint8_t)(d - base);
    int8_t  bitN  = singleBitIndex(x);
    Serial.print("  pattern ");
    printHex2(x);
    if (bitN >= 0) {
      Serial.print("  [");
      printPortCWire((uint8_t)bitN);
      Serial.print("]");
    }
    Serial.print("  D=");
    printHex2(d);
    bool pass = (delta == x) && (d == x);   // base should be 0, so both hold
    connResult(pass);
    if (!pass) {
      if (delta == 0x00 && bitN >= 0) {
        Serial.println("    no effect at all - this Port C wire is likely open");
      }
      analyseBitFault(d, x);
    }
    writePortC(0x00);
    gentleClock();
    connQuiet();
  }
  {
    uint8_t back = readPortB();
    Serial.print("  baseline restored: D=");
    printHex2(back);
    connResult(back == base);
  }

  // ---- Phase 2: Port D -> QA -> '574 -> ABUS -> adders -> D. One gentle ----
  // clock per pattern, done twice; float mode so D = register A directly.
  Serial.println();
  Serial.println("Phase 2: Port D (QA) walk via register A - one gentle clock each,");
  Serial.println("  loaded twice per pattern; float mode so D = A exactly");
  setBSource(BSRC_FLOAT);
  writePortC(0x00);
  connQuiet();

  uint8_t phase2Reads[12];
  static const uint8_t walk2[12] =
    { 0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x55, 0xAA, 0xFF };

  bool allSame = true;
  for (uint8_t i = 0; i < 12; i++) {
    uint8_t x = walk2[i];
    writePortD(x);
    gentleClock();
    uint8_t d1 = readPortB();
    gentleClock();                     // reload identical data
    uint8_t d2 = readPortB();
    phase2Reads[i] = d1;
    if (i > 0 && d1 != phase2Reads[0]) allSame = false;
    int8_t bitN = singleBitIndex(x);
    Serial.print("  pattern ");
    printHex2(x);
    if (bitN >= 0) {
      Serial.print("  [");
      printPortDWire((uint8_t)bitN);
      Serial.print("]");
    }
    Serial.print("  D=");
    printHex2(d1);
    if (d1 != d2) {
      Serial.print(" then ");
      printHex2(d2);
      Serial.print(" (UNSTABLE reload!)");
    }
    bool pass = (d1 == x) && (d2 == x);
    connResult(pass);
    if (!pass && d1 == d2) {
      uint8_t delta = d1 ^ x;
      if (bitN >= 0 && (d1 & (1u << bitN)) == 0 && singleBitIndex(delta) == bitN) {
        Serial.println("    bit never arrived - this Port D wire is likely open");
      }
      analyseBitFault(d1, x);
    }
  }
  if (allSame) {
    Serial.println("  *** D never changed across all patterns: CLK_A is not");
    Serial.println("      reaching the registers. Check PA0 (white) -> U64 pin 11. ***");
    connFails++;
  }

  // ---- Phase 3: control and status lines, one at a time ----
  Serial.println();
  Serial.println("Phase 3: control/status lines");

  // Float mode; load A = 0x10 for arithmetic probes.
  writePortD(0x10);
  gentleClock();
  connQuiet();

  // CIN: D should go 10 -> 11
  digitalWriteFast(PIN_CIN, HIGH);
  connQuiet();
  {
    uint8_t d = readPortB();
    Serial.print("  CIN=1  [PA2 (violet) -> U54 pin 7]           D=");
    printHex2(d);
    Serial.print(" (want 11)");
    connResult(d == 0x11);
  }
  digitalWriteFast(PIN_CIN, LOW);
  connQuiet();
  {
    uint8_t d = readPortB();
    Serial.print("  CIN=0                                        D=");
    printHex2(d);
    Serial.print(" (want 10)");
    connResult(d == 0x10);
  }

  // SUB: float Beff=00 -> XOR gives FF: D = 10+FF = 0F with COUT=1
  digitalWriteFast(PIN_SUB, HIGH);
  connQuiet();
  {
    uint8_t d = readPortB();
    bool c   = digitalReadFast(PIN_COUT) == HIGH;
    Serial.print("  SUB=1  [PA1 (grey) -> U50 pin 2]             D=");
    printHex2(d);
    Serial.print(" COUT=");
    Serial.print(c ? 1 : 0);
    Serial.print(" (want 0F,1)");
    connResult(d == 0x0F && c);
    if (!c) {
      Serial.println("    COUT stuck low? [PA5 (yellow) <- U53 pin 9]");
    }
  }
  digitalWriteFast(PIN_SUB, LOW);
  connQuiet();
  {
    bool c = digitalReadFast(PIN_COUT) == HIGH;
    Serial.print("  SUB=0: COUT back to 0                        COUT=");
    Serial.print(c ? 1 : 0);
    connResult(!c);
  }

  // /EQL: A=00 float -> D=00 -> /EQL low; CIN=1 -> D=01 -> /EQL high
  writePortD(0x00);
  gentleClock();
  connQuiet();
  {
    bool e = digitalReadFast(PIN_EQL_N) == HIGH;
    Serial.print("  D=00: /EQL  [PA6 (orange) <- U44 pin 19]     /EQL=");
    Serial.print(e ? 1 : 0);
    Serial.print(" (want 0)");
    connResult(!e);
  }
  digitalWriteFast(PIN_CIN, HIGH);
  connQuiet();
  {
    bool e = digitalReadFast(PIN_EQL_N) == HIGH;
    Serial.print("  D=01: /EQL                                   /EQL=");
    Serial.print(e ? 1 : 0);
    Serial.print(" (want 1)");
    connResult(e);
  }
  digitalWriteFast(PIN_CIN, LOW);
  connQuiet();

  // Z flag: latches /EQL on the clock. D=00 now -> clock -> Z=0.
  gentleClock();
  {
    bool z = digitalReadFast(PIN_ZFLAG) == HIGH;
    Serial.print("  Z after clock at D=00  [PA7 (red) <- U45 p5] Z=");
    Serial.print(z ? 1 : 0);
    Serial.print(" (want 0)");
    connResult(!z);
  }
  writePortD(0x01);
  gentleClock();                       // A=01 -> D=01, /EQL=1
  connQuiet();
  gentleClock();                       // capture /EQL=1 into Z
  {
    bool z = digitalReadFast(PIN_ZFLAG) == HIGH;
    Serial.print("  Z after clock at D=01                        Z=");
    Serial.print(z ? 1 : 0);
    Serial.print(" (want 1)");
    connResult(z);
    if (!z) {
      Serial.println("    Z stuck: check PA7 (red) <- U45 pin 5, and the");
      Serial.println("    ZNEW strap U46 pin 3 -> U44 pin 19, FLAGEN -> VCC");
    }
  }

  // /BOE: load A=00, B=55 gently; register drive should put 55 on D.
  writePortD(0x00);
  connQuiet();
  writePortC(0x55);
  gentleClock();                       // A=00, B=55
  setBSource(BSRC_REG);
  connQuiet();
  {
    uint8_t d = readPortB();
    Serial.print("  /BOE=0, B reg=55  [PA3 (blue) -> U66 pin 1]  D=");
    printHex2(d);
    Serial.print(" (want 55)");
    connResult(d == 0x55);
    if (d == 0x00) {
      Serial.println("    still reads the pulldown value - /BOE wire not reaching");
      Serial.println("    U66 pin 1, or the '574 outputs never enable");
    }
  }

  // /IMMOE handover: the '541 now carries the hardwired constant A5.
  setBSource(BSRC_IMM);
  connQuiet();
  {
    uint8_t d = readPortB();
    Serial.print("  /IMMOE=0, const   [PA4 (green) -> U63 pin 1] D=");
    printHex2(d);
    Serial.print(" (want A5)");
    connResult(d == immConstant);
    if (d != immConstant) {
      Serial.println("    wrong constant: check the A5 straps on U63 pins 2..9,");
      Serial.println("    U63 pin 19 to GND, and U63 power (pins 10/20)");
    }
  }
  setBSource(BSRC_REG);
  connQuiet();
  {
    uint8_t d = readPortB();
    Serial.print("  back to /BOE: register held                  D=");
    printHex2(d);
    Serial.print(" (want 55)");
    connResult(d == 0x55);
    if (d == immConstant) {
      Serial.println("    bus still shows the constant - U63 did not release:");
      Serial.println("    check /IMMOE at U63 pin 1 and U63 pin 19 to GND");
    }
  }
  setBSource(BSRC_FLOAT);
  connQuiet();
  {
    uint8_t d = readPortB();
    Serial.print("  both released: pulldowns                     D=");
    printHex2(d);
    Serial.print(" (want 00)");
    connResult(d == 0x00);
  }

  // ---- Summary ----
  Serial.println();
  Serial.print("=== CONNECTIVITY CHECK: ");
  if (connFails == 0) {
    Serial.println("all checks PASSED ===");
    Serial.println("Every wire verified. Remaining failures in T are dynamic");
    Serial.println("(timing / signal integrity), not connectivity.");
  } else {
    Serial.print(connFails);
    Serial.println(" FAILURES - see wire names above ===");
  }
  idleBus();
}

// 'P': full static sweeps of the B-register operand path - all 256 values,
// gentle timing, one gentle clock per value. Runs TWICE: A = 00 (D = value)
// and A = FF (D = value-1 mod 256; every step forces a full carry ripple
// through both '283s - the maximum-internal-switching stress case).
uint16_t runOperandSweep(uint8_t aVal) {
  setMode(false, false);
  setBSource(BSRC_FLOAT);
  writePortD(aVal);
  writePortC(0x00);
  gentleClock();                      // register A = aVal, B = 00
  setBSource(BSRC_REG);
  connQuiet();

  uint16_t sweepFails = 0;
  uint16_t bitErr[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
  for (uint16_t v = 0; v < 256; v++) {
    writePortC((uint8_t)v);
    gentleClock();                    // latch the value into B
    connQuiet();
    uint8_t d    = readPortB();
    uint8_t dExp = (uint8_t)(aVal + v);
    if (d != dExp) {
      sweepFails++;
      uint8_t diff = d ^ dExp;
      for (uint8_t n = 0; n < 8; n++) {
        if (diff & (1u << n)) bitErr[n]++;
      }
      if (sweepFails <= 40) {
        Serial.print("  ");
        printHex2((uint8_t)v);
        Serial.print(" -> ");
        printHex2(d);
        Serial.print(" want ");
        printHex2(dExp);
        Serial.print("  (D-bus bits wrong:");
        for (uint8_t n = 0; n < 8; n++) {
          if (diff & (1u << n)) {
            Serial.print(' ');
            Serial.print(n);
          }
        }
        Serial.println(")");
      } else if (sweepFails == 41) {
        Serial.println("  (further failures suppressed)");
      }
    }
  }
  Serial.print("Sweep (A=");
  printHex2(aVal);
  Serial.print(") done: ");
  Serial.print(sweepFails);
  Serial.println(" of 256 wrong");
  if (sweepFails > 0) {
    Serial.print("D-bus error counts, bits 0..7: ");
    for (uint8_t n = 0; n < 8; n++) {
      Serial.print(bitErr[n]);
      Serial.print(n < 7 ? " " : "\n");
    }
  }
  return sweepFails;
}

void patternSweep() {
  Serial.println("Operand-path static sweeps via B register (one gentle clock per value)");
  Serial.println("--- Sweep 1: A=00, expect D = value ---");
  uint16_t f0 = runOperandSweep(0x00);
  Serial.println("--- Sweep 2: A=FF, expect D = value-1 (mod 256), full ripple ---");
  uint16_t f1 = runOperandSweep(0xFF);
  Serial.println();
  Serial.print("Verdict: A=00 sweep ");
  Serial.print(f0);
  Serial.print(" wrong, A=FF sweep ");
  Serial.print(f1);
  Serial.println(" wrong.");
  if (f0 == 0 && f1 == 0) {
    Serial.println("Both sweeps clean.");
  } else if (f0 == 0 && f1 > 0) {
    Serial.println("Errors only under full carry ripple (A=FF): dynamic margin,");
    Serial.println("not connectivity. Re-run P - wandering failure values mean a");
    Serial.println("residual intermittent; fixed values mean a specific weak node");
    Serial.println("in the carry chain.");
  } else if (f1 * 4 < f0) {
    Serial.println("FF sweep much cleaner -> ABUS side floating: check the /AOE");
    Serial.println("strap (U64 pin 1 leg = 0V), RN3 pin 1 to GND, U64 power legs.");
  } else {
    Serial.println("Both sweeps comparably bad -> B side: check U66 power/enable");
    Serial.println("legs, RN5 pin 1 to GND, XOR power, and U63's outputs on BBUS.");
  }
  idleBus();
}

// 'B <v1> <v2>': boundary hammer. Alternates the B register between two
// values, 100 loads each, at gentle timing with A = FF (maximum internal
// carry churn on every transition). Turns a sporadic rollover failure like
// DF<->E0 into a repeatable error-rate measurement. All reads are static,
// milliseconds after the clock - any wrong read means a wrong LATCHED value.
void boundaryHammer(uint8_t v1, uint8_t v2) {
  Serial.print("Boundary hammer: B alternating ");
  printHex2(v1);
  Serial.print(" <-> ");
  printHex2(v2);
  Serial.println(", A=FF, 100 loads each, gentle timing");

  setMode(false, false);
  setBSource(BSRC_FLOAT);
  writePortD(0xFF);
  writePortC(v1);
  gentleClock();                      // A = FF, B = v1
  setBSource(BSRC_REG);
  connQuiet();

  uint16_t err1 = 0;
  uint16_t err2 = 0;
  uint8_t  exp1 = (uint8_t)(0xFF + v1);
  uint8_t  exp2 = (uint8_t)(0xFF + v2);
  uint8_t  shown = 0;

  for (uint8_t i = 0; i < 100; i++) {
    writePortC(v2);
    gentleClock();
    connQuiet();
    uint8_t d = readPortB();
    if (d != exp2) {
      err2++;
      if (shown < 10) {
        shown++;
        Serial.print("  load ");
        printHex2(v2);
        Serial.print(" -> D=");
        printHex2(d);
        Serial.print(" want ");
        printHex2(exp2);
        Serial.println();
      }
    }
    writePortC(v1);
    gentleClock();
    connQuiet();
    d = readPortB();
    if (d != exp1) {
      err1++;
      if (shown < 10) {
        shown++;
        Serial.print("  load ");
        printHex2(v1);
        Serial.print(" -> D=");
        printHex2(d);
        Serial.print(" want ");
        printHex2(exp1);
        Serial.println();
      }
    }
  }
  Serial.print("Result: ");
  printHex2(v1);
  Serial.print(" wrong ");
  Serial.print(err1);
  Serial.print("/100, ");
  printHex2(v2);
  Serial.print(" wrong ");
  Serial.print(err2);
  Serial.println("/100");
  idleBus();
}

// ------------------------------------------------------------ test slices

uint32_t sliceSize() {
  return (gapMicros > 0 || setupMicros > 100) ? vectorsPerSliceSlow
                                              : vectorsPerSliceFast;
}

void sliceFullTest() {
  uint32_t end = vecIndex + sliceSize();
  if (end > vecTotal) end = vecTotal;
  setBSource(BSRC_REG);
  for (; vecIndex < end; vecIndex++) {
    bool    sub = (vecIndex & 0x20000u) != 0;
    bool    cin = (vecIndex & 0x10000u) != 0;
    uint8_t a   = (uint8_t)(vecIndex >> 8);
    uint8_t b   = (uint8_t)vecIndex;
    setMode(sub, cin);
    loadRegisters(a, b);
    delayMicroseconds(settleMicros);
    AluSample s = sampleAlu();
    captureFlags(s);
    checkSample("REG", a, b, sub, cin, s, true);
    interVectorGap();
  }
  if ((vecIndex & 0xFFFFu) == 0 && vecIndex < vecTotal) {
    Serial.print(vecIndex >> 16);
    Serial.println("/4 mode blocks done");
  }
}

// Immediate-constant test: 256 A values x SUB x CIN against Beff = A5,
// with a /BOE handover check after every vector (the B register holds ~A,
// which must return intact when /IMMOE releases). Index: bits 9..8 =
// SUB/CIN, bits 7..0 = A. Total 1024 vectors, 2048 checks.
void sliceImmTest() {
  uint32_t end = vecIndex + sliceSize();
  if (end > vecTotal) end = vecTotal;
  for (; vecIndex < end; vecIndex++) {
    uint8_t a    = (uint8_t)vecIndex;
    bool    sub  = (vecIndex & 0x200u) != 0;
    bool    cin  = (vecIndex & 0x100u) != 0;
    uint8_t bReg = (uint8_t)~a;
    setMode(sub, cin);
    setBSource(BSRC_REG);
    loadRegisters(a, bReg);
    setBSource(BSRC_IMM);
    delayMicroseconds(settleMicros);
    checkSample("IMM", a, immConstant, sub, cin, sampleAlu(), false);
    setBSource(BSRC_REG);              // handover back: register intact?
    delayMicroseconds(settleMicros);
    checkSample("HND", a, bReg, sub, cin, sampleAlu(), false);
    interVectorGap();
  }
}

void sliceFloatTest() {
  uint32_t end = vecIndex + sliceSize();
  if (end > vecTotal) end = vecTotal;
  for (; vecIndex < end; vecIndex++) {
    uint8_t a   = (uint8_t)vecIndex;
    bool    sub = (vecIndex & 0x200u) != 0;
    bool    cin = (vecIndex & 0x100u) != 0;
    setMode(sub, cin);
    setBSource(BSRC_REG);
    loadRegisters(a, 0xA5);            // must NOT appear in the result
    setBSource(BSRC_FLOAT);
    delayMicroseconds(settleMicros);
    checkSample("FLT", a, 0x00, sub, cin, sampleAlu(), false);
    interVectorGap();
  }
}

// --------------------------------------------------------- run control

void startTest(RunState which, uint32_t total, const char *name) {
  if (runState != RS_IDLE || monitorActive) {
    Serial.println("Busy - X first.");
    return;
  }
  runState       = which;
  vecIndex       = 0;
  vecTotal       = total;
  failCount      = 0;
  checkCount     = 0;
  runStartMillis = millis();
  Serial.print("Running ");
  Serial.print(name);
  Serial.print(" (");
  Serial.print(total);
  Serial.print(" vectors, gap ");
  Serial.print(gapMicros);
  Serial.print(" us, setup ");
  Serial.print(setupMicros);
  Serial.println(" us)...");
}

void finishTest(bool aborted) {
  unsigned long elapsed = millis() - runStartMillis;
  runState = RS_IDLE;
  idleBus();
  Serial.print(aborted ? "ABORTED after " : "Done: ");
  Serial.print(checkCount);
  Serial.print(" checks, ");
  Serial.print(failCount);
  Serial.print(" failures, ");
  Serial.print(elapsed);
  Serial.println(" ms");
  Serial.println(failCount == 0 && !aborted ? "*** PASS ***" : "*** FAIL ***");
}

// -------------------------------------------------- manual / diagnostic ops

bool parseHexByte(const char *tok, uint8_t &out) {
  uint16_t v   = 0;
  uint8_t  len = 0;
  for (; *tok != '\0'; tok++, len++) {
    char c = *tok;
    uint8_t nib;
    if (c >= '0' && c <= '9')      nib = (uint8_t)(c - '0');
    else if (c >= 'A' && c <= 'F') nib = (uint8_t)(c - 'A' + 10);
    else if (c >= 'a' && c <= 'f') nib = (uint8_t)(c - 'a' + 10);
    else return false;
    v = (uint16_t)((v << 4) | nib);
  }
  if (len == 0 || len > 2) return false;
  out = (uint8_t)v;
  return true;
}

bool parseDecimal(const char *tok, uint32_t &out) {
  uint32_t v = 0;
  uint8_t  len = 0;
  for (; *tok != '\0'; tok++, len++) {
    if (*tok < '0' || *tok > '9' || len > 7) return false;
    v = v * 10 + (uint32_t)(*tok - '0');
  }
  if (len == 0) return false;
  out = v;
  return true;
}

bool parseOpArgs(char *line, uint8_t &a, uint8_t &b, BSource &src,
                 bool &sub, bool &cin) {
  const uint8_t maxTokens = 6;
  char *tokens[maxTokens];
  uint8_t nTokens = 0;
  for (char *p = line; *p != '\0' && nTokens < maxTokens; ) {
    while (*p == ' ') p++;
    if (*p == '\0') break;
    tokens[nTokens++] = p;
    while (*p != '\0' && *p != ' ') p++;
    if (*p == ' ') { *p = '\0'; p++; }
  }
  if (nTokens != 6 || !parseHexByte(tokens[1], a) || !parseHexByte(tokens[2], b)) {
    return false;
  }
  char srcChar = tokens[3][0];
  if (srcChar >= 'a') srcChar = (char)(srcChar - 'a' + 'A');
  if      (srcChar == 'R') src = BSRC_REG;
  else if (srcChar == 'I') src = BSRC_IMM;
  else if (srcChar == 'F') src = BSRC_FLOAT;
  else return false;
  sub = tokens[4][0] == '1';
  cin = tokens[5][0] == '1';
  return true;
}

void printUsage() {
  Serial.println("Usage: M|R|S <A hex> <B hex> <R|I|F> <SUB 0/1> <CIN 0/1>");
  Serial.println("(src I: Beff is the hardwired constant A5; B loads the register)");
}

void manualOp(uint8_t a, uint8_t b, BSource src, bool sub, bool cin) {
  uint8_t bEff = applyVector(a, b, src, sub, cin);
  AluSample s = sampleAlu();
  captureFlags(s);

  uint8_t dExp;
  bool    coutExp;
  expectedAlu(a, bEff, sub, cin, dExp, coutExp);
  Serial.print("A=");     printHex2(a);
  Serial.print(" Beff="); printHex2(bEff);
  Serial.print(" SUB=");  Serial.print(sub ? 1 : 0);
  Serial.print(" CIN=");  Serial.print(cin ? 1 : 0);
  Serial.print(" -> ");
  printSample(s);
  bool ok = (s.d == dExp) && (s.cout == coutExp)
         && (s.eqlN == (dExp != 0)) && (s.zflag == (dExp != 0));
  Serial.println(ok ? "  PASS" : "  FAIL");
  if (!ok) {
    Serial.print("expected D="); printHex2(dExp);
    Serial.print(" COUT=");      Serial.println(coutExp ? 1 : 0);
  }
}

constexpr uint8_t repeatCount = 100;
constexpr uint8_t maxDistinct = 12;

void repeatProbe(uint8_t a, uint8_t b, BSource src, bool sub, bool cin) {
  uint16_t values[maxDistinct];
  uint8_t  counts[maxDistinct];
  uint8_t  nDistinct = 0;
  bool     overflow  = false;

  for (uint8_t i = 0; i < repeatCount; i++) {
    applyVector(a, b, src, sub, cin);
    AluSample s = sampleAlu();
    captureFlags(s);
    interVectorGap();
    uint16_t packed = packSample(s);
    bool found = false;
    for (uint8_t k = 0; k < nDistinct; k++) {
      if (values[k] == packed) {
        counts[k]++;
        found = true;
        break;
      }
    }
    if (!found) {
      if (nDistinct < maxDistinct) {
        values[nDistinct] = packed;
        counts[nDistinct] = 1;
        nDistinct++;
      } else {
        overflow = true;
      }
    }
  }

  uint8_t dExp;
  bool    coutExp;
  uint8_t bEff = (src == BSRC_FLOAT) ? 0x00
               : (src == BSRC_IMM   ? immConstant : b);
  expectedAlu(a, bEff, sub, cin, dExp, coutExp);
  Serial.print("Repeat probe, ");
  Serial.print(repeatCount);
  Serial.print(" runs, gap ");
  Serial.print(gapMicros);
  Serial.print(" us, setup ");
  Serial.print(setupMicros);
  Serial.print(" us, expected D=");
  printHex2(dExp);
  Serial.print(" COUT=");
  Serial.println(coutExp ? 1 : 0);

  for (uint8_t k = 0; k < nDistinct; k++) {
    AluSample s;
    s.d     = (uint8_t)values[k];
    s.cout  = (values[k] & 0x100) != 0;
    s.eqlN  = (values[k] & 0x200) != 0;
    s.zflag = (values[k] & 0x400) != 0;
    Serial.print("  x");
    Serial.print(counts[k]);
    Serial.print("  ");
    printSample(s);
    Serial.println();
  }
  if (overflow) {
    Serial.println("  (more distinct results than table space - very unstable)");
  }
  Serial.print(nDistinct);
  Serial.println(nDistinct == 1
    ? " distinct result: STABLE at this rate"
    : " distinct results: UNSTABLE at this rate");
}

void startStaticProbe(uint8_t a, uint8_t b, BSource src, bool sub, bool cin) {
  uint8_t bEff = applyVector(a, b, src, sub, cin);
  AluSample s = sampleAlu();
  captureFlags(s);
  monitorLast   = packSample(s);
  monitorActive = true;
  Serial.print("Static probe: A=");
  printHex2(a);
  Serial.print(" Beff=");
  printHex2(bEff);
  Serial.print(" -> ");
  printSample(s);
  Serial.println();
  Serial.println("Monitoring - every change prints. Silence is good. X to stop.");
}

void serviceMonitor() {
  AluSample s = sampleAlu();
  uint16_t packed = packSample(s);
  if (packed != monitorLast) {
    monitorLast = packed;
    Serial.print("t=");
    Serial.print(millis());
    Serial.print("  ");
    printSample(s);
    Serial.println();
  }
}

// --------------------------------------------------------- command handling

void printHelp() {
  Serial.println("ALU harness commands ('541 inputs hardwired to A5):");
  Serial.println("  C                      connectivity check - per-wire, gentle, verbose");
  Serial.println("  P                      operand sweeps via B register, A=00 and A=FF");
  Serial.println("  B <v1> <v2>            boundary hammer: alternate B between two hex");
  Serial.println("                         values 100x each, A=FF, count wrong latches");
  Serial.println("  T                      full register-path test (262,144 vectors)");
  Serial.println("  I                      immediate-constant test: 256 A x SUB x CIN vs A5,");
  Serial.println("                         with a /BOE handover check every vector");
  Serial.println("  F                      bus-release / pulldown test");
  Serial.println("  M <A> <B> <src> <s> <c>  manual op with pass/fail");
  Serial.println("  R <A> <B> <src> <s> <c>  repeatability probe (100 identical runs)");
  Serial.println("  S <A> <B> <src> <s> <c>  static probe, monitors until X");
  Serial.println("  G <us>                 inter-vector gap (G alone shows current)");
  Serial.println("  U <us>                 operand-to-clock setup time, default 1");
  Serial.println("  X                      abort test / stop monitor");
  Serial.println("  H                      this help");
  Serial.println("  args: A,B hex; src R/I/F (I: Beff=A5); s,c = SUB,CIN 0/1");
  Serial.println("  SW1 = start full test, SW2 = abort");
}

void handleOpCommand(char cmd, char *line) {
  uint8_t a = 0;
  uint8_t b = 0;
  BSource src = BSRC_REG;
  bool sub = false;
  bool cin = false;
  if (!parseOpArgs(line, a, b, src, sub, cin)) {
    printUsage();
    return;
  }
  if (runState != RS_IDLE || monitorActive) {
    Serial.println("Busy - X first.");
    return;
  }
  if      (cmd == 'M') manualOp(a, b, src, sub, cin);
  else if (cmd == 'R') repeatProbe(a, b, src, sub, cin);
  else                 startStaticProbe(a, b, src, sub, cin);
}

void handleTimingCommand(char cmd, char *line) {
  char *p = line + 1;
  while (*p == ' ') p++;
  bool isGap = (cmd == 'G');
  if (*p == '\0') {
    Serial.print(isGap ? "Gap is " : "Setup is ");
    Serial.print(isGap ? gapMicros : setupMicros);
    Serial.println(" us");
    return;
  }
  uint32_t v = 0;
  uint32_t maxV = isGap ? gapMicrosMax : setupMicrosMax;
  if (!parseDecimal(p, v) || v > maxV) {
    Serial.print("Usage: ");
    Serial.print(cmd);
    Serial.print(" <microseconds 0..");
    Serial.print(maxV);
    Serial.println(">");
    return;
  }
  if (isGap) gapMicros = v;
  else       setupMicros = v;
  Serial.print(isGap ? "Gap set to " : "Setup set to ");
  Serial.print(v);
  Serial.println(" us");
  uint64_t perVector = (uint64_t)gapMicros + 2ULL * setupMicros + 10ULL;
  uint32_t seconds = (uint32_t)((perVector * 262144ULL) / 1000000ULL);
  Serial.print("(full test will take about ");
  Serial.print(seconds + 1);
  Serial.println(" s at these settings)");
}

void handleBoundaryCommand(char *line) {
  // Tokenize: B <v1> <v2>
  const uint8_t maxTokens = 3;
  char *tokens[maxTokens];
  uint8_t nTokens = 0;
  for (char *p = line; *p != '\0' && nTokens < maxTokens; ) {
    while (*p == ' ') p++;
    if (*p == '\0') break;
    tokens[nTokens++] = p;
    while (*p != '\0' && *p != ' ') p++;
    if (*p == ' ') { *p = '\0'; p++; }
  }
  uint8_t v1 = 0;
  uint8_t v2 = 0;
  if (nTokens != 3 || !parseHexByte(tokens[1], v1) || !parseHexByte(tokens[2], v2)) {
    Serial.println("Usage: B <v1 hex> <v2 hex>   e.g. B DF E0");
    return;
  }
  if (runState != RS_IDLE || monitorActive) {
    Serial.println("Busy - X first.");
    return;
  }
  boundaryHammer(v1, v2);
}

void handleLine(char *line) {
  while (*line == ' ') line++;
  if (*line == '\0') return;
  char cmd = *line;
  if (cmd >= 'a' && cmd <= 'z') cmd = (char)(cmd - 'a' + 'A');
  switch (cmd) {
    case 'C':
      if (runState != RS_IDLE || monitorActive) {
        Serial.println("Busy - X first.");
      } else {
        connectivityCheck();
      }
      break;
    case 'P':
      if (runState != RS_IDLE || monitorActive) {
        Serial.println("Busy - X first.");
      } else {
        patternSweep();
      }
      break;
    case 'T': startTest(RS_FULL,  262144UL,    "full register-path test");     break;
    case 'I': startTest(RS_IMM,   4UL * 256UL, "immediate-constant test");     break;
    case 'F': startTest(RS_FLOAT, 4UL * 256UL, "bus-release test");            break;
    case 'B': handleBoundaryCommand(line); break;
    case 'M':
    case 'R':
    case 'S': handleOpCommand(cmd, line); break;
    case 'G':
    case 'U': handleTimingCommand(cmd, line); break;
    case 'X':
      if (monitorActive) {
        monitorActive = false;
        idleBus();
        Serial.println("Monitor stopped.");
      } else if (runState != RS_IDLE) {
        finishTest(true);
      } else {
        Serial.println("Nothing running.");
      }
      break;
    case 'H': printHelp(); break;
    default:
      Serial.println("? (H for help)");
      break;
  }
}

// ------------------------------------------------------------------- setup

void setup() {
  Serial.begin(115200);              // decorative - native USB CDC

  setPortMode(PORT_D_PINS, OUTPUT);  // QA
  setPortMode(PORT_C_PINS, OUTPUT);  // QB
  setPortMode(PORT_B_PINS, INPUT);   // D bus, always driven by the '283s

  pinMode(PIN_CLK,     OUTPUT);
  pinMode(PIN_SUB,     OUTPUT);
  pinMode(PIN_CIN,     OUTPUT);
  pinMode(PIN_BOE_N,   OUTPUT);
  pinMode(PIN_IMMOE_N, OUTPUT);
  pinMode(PIN_COUT,    INPUT);
  pinMode(PIN_EQL_N,   INPUT);
  pinMode(PIN_ZFLAG,   INPUT);

  pinMode(PIN_SW1, INPUT);           // board has 4.7k pull-ups
  pinMode(PIN_SW2, INPUT);

  digitalWriteFast(PIN_CLK, LOW);
  idleBus();

  lastSw1 = sw1Pressed();
  lastSw2 = sw2Pressed();

  while (!Serial && millis() < 3000) { }
  Serial.println("8-bit ALU test harness ready ('541 constant = A5).");
  printHelp();
}

// -------------------------------------------------------------------- loop

void loop() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      if (lineLen > 0) {
        lineBuf[lineLen] = '\0';
        lineLen = 0;
        handleLine(lineBuf);
      }
    } else if (lineLen < sizeof(lineBuf) - 1) {
      lineBuf[lineLen++] = c;
    }
  }

  bool sw1 = sw1Pressed();
  if (sw1 && !lastSw1 && runState == RS_IDLE && !monitorActive) {
    startTest(RS_FULL, 262144UL, "full register-path test");
  }
  lastSw1 = sw1;

  bool sw2 = sw2Pressed();
  if (sw2 && !lastSw2) {
    if (monitorActive) {
      monitorActive = false;
      idleBus();
      Serial.println("Monitor stopped.");
    } else if (runState != RS_IDLE) {
      finishTest(true);
    }
  }
  lastSw2 = sw2;

  if (monitorActive) {
    serviceMonitor();
  }

  switch (runState) {
    case RS_FULL:  sliceFullTest();  break;
    case RS_IMM:   sliceImmTest();   break;
    case RS_FLOAT: sliceFloatTest(); break;
    default: break;
  }
  if (runState != RS_IDLE && vecIndex >= vecTotal) {
    finishTest(false);
  }
}
