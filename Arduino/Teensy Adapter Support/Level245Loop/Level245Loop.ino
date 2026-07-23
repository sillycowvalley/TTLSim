// ============================================================================
//  Level245Loop.ino  -  74HCT245 / 74LVC245 8-bit level-translation loopback
//
//  All eight lines, Port A -> Port C, through the push-pull translation pair
//  that is intended to replace the TXS0108E path.
//
//      Port A (OUTPUT) --> 74HCT245  A1..A8 -> B1..B8 --+
//                          (Vcc 5V, DIR=H, A->B)        |
//                                                  5V bus + LED Thing
//                                                       |
//      Port C (INPUT)  <-- 74LVC245  A1..A8 <- B1..B8 --+
//                          (Vcc 3.3V, DIR=L, B->A)
//
//  Both '245s face B side to B side, so the 5V bus is a straight run:
//  pin 18 to pin 18, 17 to 17, down to 11 to 11. Whatever Port A writes must
//  appear on Port C. No switches in this build.
//
//  NOTE ON STYLE: no enums or other user-defined types appear in any function
//  signature. The Arduino build inserts generated prototypes above the point
//  where a sketch-level type is declared, so such a signature fails with
//  "variable or field declared void". Mode values are plain uint8_t constants
//  for that reason - do not "tidy" them into an enum.
//
//  --------------------------------------------------------- THE TWO PATHS --
//  Measured on this board, 200,000 vectors each, no commanded settle:
//
//      header accessors (AdapterPorts.h) : 158 ns/vector
//      banked GPIO (this sketch)         : 226 ns/vector
//
//  The header wins, and the reason is worth recording so nobody re-does the
//  experiment. digitalWriteFast/digitalReadFast with a compile-time-constant
//  pin fold to a single store or load, constant address, constant mask, on
//  GPIO6-9 - the CPU-bus fast ports, one or two cycles each. There is no
//  peripheral-bus penalty to remove. The banked path replaces those folded
//  constants with runtime table lookups and a stack-indexed snapshot, which
//  costs more than the redundant loads it eliminates. Port C in particular
//  sits entirely in one GPIO bank, so the header's eight constant loads are
//  already close to ideal.
//
//  The banked path is kept for one property the header cannot give:
//  SIMULTANEITY. The header issues eight separate stores, so bit 0 changes
//  roughly 70 ns before bit 7. Banked changes every bit within a bank on a
//  single store - here three groups a few ns apart. As a coupling aggressor
//  that is materially harsher, which is what you want when stressing a real
//  DUT with a clock or strobe in the loom. Use 'F' to select it; 'M' compares
//  them on the board in front of you; 'V' proves they agree.
//
//  Default is the header path: faster, simpler, and the verified mapping is
//  the single source of truth. Teensy 4.1 only - elsewhere the banked path
//  compiles away and 'F' does nothing.
//
//  --------------------------------------------------------------- SETTLE ---
//  settleNs must not be zero. At zero the readback samples mid-transition and
//  the slower channels return the PREVIOUS vector's value on that line - seen
//  as walking-1 0x08 reading back 0x0C after 0x04. Note that on Teensy 4 a
//  very small value is dominated by delayNanoseconds' own overhead (cycle
//  counter read plus compare loop, tens of ns), so a nominal "2" is really
//  that overhead rather than 2 ns. Run 'S' to find the real figure for this
//  wiring and set settleNs to several times it, rather than relying on call
//  overhead that a core or compiler change could shrink away.
//
//  ------------------------------------------------------------ BREADBOARD --
//  Pin numbers come from AdapterPorts.h and are printed at boot - check them
//  against the wiring before trusting a run. Bit 0 = white, bit 7 = red.
//
//  74x245 DIP-20 - NOTE the B pins descend:
//      1 = DIR   2..9 = A1..A8   10 = GND
//      19 = /OE  18..11 = B1..B8 20 = Vcc
//
//  U2  74HCT245   Vcc = +5V      pin 20 -> +5V, pin 10 -> GND, 100nF at pins
//      pin 1  (DIR) -> +5V       (A->B)
//      pin 19 (/OE) -> GND       *** see the /OE note below ***
//      pins 2..9   (A1..A8) <- Port A bits 0..7
//      pins 18..11 (B1..B8) -> 5V bus
//
//  U3  74LVC245   Vcc = +3.3V    pin 20 -> +3.3V, pin 10 -> GND, 100nF at pins
//      pin 1  (DIR) -> GND       (B->A)
//      pin 19 (/OE) -> GND       NOT to pin 20 - high here tri-states it
//      pins 18..11 (B1..B8) <- 5V bus
//      pins 2..9   (A1..A8) -> Port C bits 0..7
//
//  /OE NOTE: holding HCT245 pin 19 at +5V during power-up and flashing keeps
//  its outputs tri-stated while the Teensy pins are in an unknown state. It
//  also leaves the 5V bus - and the LVC245's B inputs - floating, so don't
//  sit there longer than the flash takes. The sketch detects it, names it,
//  and polls while idle: move pin 19 to GND and it announces the path is
//  alive. No re-flash needed. 'A' rechecks manually.
//
//  8-bit LED Thing on the 5V bus: power +5V, ground GND, all eight sense
//  inputs to the bus, mode pins unconnected (weak pull-ups give mode 7). Its
//  inputs are a floating ~1uA load, so it does not disturb the bus. It is a
//  sampled display and will miss runts entirely - the Port C readback is the
//  instrument; the LEDs are for park mode and the chaser.
//
//  Common all grounds. Port C is plain INPUT - the LVC drives it push-pull.
//
//  ------------------------------------------------------------- COMMANDS ---
//  W  walking 1 then walking 0 (16 vectors)   finds opens, shorts, swaps
//  E  exhaustive 0x00..0xFF (256 vectors)     complete for 8 bits
//  H  hammer 0x00<->0xFF, 2,000,000 vectors   all 8 lines slew together
//  L  long soak, same aggressor, until 'X'
//  S  settle sweep - find the real settle requirement
//  M  measure throughput, both I/O paths
//  V  verify banked path == header accessors (256 patterns)
//  C  chaser, verified, slow enough to watch
//  P  park a static pattern and hold it       press again to advance
//  F  toggle header/banked I/O path
//  A  recheck the path (/OE jumper)
//  X  stop        R  last summary        ?  help
// ============================================================================

#include "AdapterPorts.h"

#if defined(__IMXRT1062__)
  #define HAVE_BANKED_IO 1
#else
  #define HAVE_BANKED_IO 0
#endif

// Mode values - plain constants, never an enum (see STYLE note above).
static const uint8_t modeIdle       = 0;
static const uint8_t modeWalking    = 1;
static const uint8_t modeExhaustive = 2;
static const uint8_t modeHammer     = 3;
static const uint8_t modeSweep      = 4;
static const uint8_t modeMeasure    = 5;
static const uint8_t modeChaser     = 6;
static const uint8_t modePark       = 7;

static const long          serialBaud     = 115200L;  // ignored on Teensy USB CDC
static const uint16_t      settleNs       = 2;        // MUST be non-zero - see SETTLE note
static const unsigned long chaserStepMs   = 100;
static const uint16_t      vectorsPerPass = 2000;     // chunk - keeps loop() responsive
static const uint8_t       maxErrorPrints = 12;
static const uint32_t      hammerVectors  = 2000000UL;
static const uint32_t      soakVectors    = 0xFFFFFFFFUL;
static const uint32_t      progressEvery  = 1000000UL;
static const unsigned long alivePollMs    = 750;
static const uint32_t      measureVectors = 200000UL;
static const uint16_t      measurePerPass = 20000;

static const uint16_t sweepStepNs = 10;
static const uint16_t sweepMaxNs  = 400;
static const uint16_t sweepTrials = 500;   // 0x00/0xFF pairs per delay point

static const uint8_t parkPatterns[]   = { 0x00, 0xFF, 0x55, 0xAA, 0x0F, 0xF0 };
static const uint8_t parkPatternCount = (uint8_t)(sizeof(parkPatterns) / sizeof(parkPatterns[0]));

// ========================================================== banked GPIO ====
// Not the default. Kept for its simultaneity, not its speed - see THE TWO
// PATHS above before assuming this is an optimisation.
#if HAVE_BANKED_IO

static const uint8_t maxBanks = 4;

static volatile uint32_t  bankScratch = 0;

static volatile uint32_t *writeSetReg[maxBanks];
static volatile uint32_t *writeClrReg[maxBanks];
static uint32_t           writeSetTable[maxBanks][256];
static uint32_t           writeAllMask[maxBanks];
static uint8_t            writeBankCount = 0;

static volatile uint32_t *readReg[maxBanks];
static uint8_t            readBankOf[8];
static uint32_t           readMaskOf[8];
static uint8_t            readBankCount = 0;

static void buildBanks() {
  uint8_t  bankFor[8];
  uint32_t maskFor[8];

  writeBankCount = 0;
  for (uint8_t i = 0; i < 8; i++) {
    uint8_t pin = PORT_A_PINS[i];
    volatile uint32_t *sr = portSetRegister(pin);
    uint32_t m = digitalPinToBitMask(pin);

    uint8_t b = 0xFF;
    for (uint8_t k = 0; k < writeBankCount; k++) {
      if (writeSetReg[k] == sr) { b = k; break; }
    }
    if (b == 0xFF) {
      b = writeBankCount++;
      writeSetReg[b]  = sr;
      writeClrReg[b]  = portClearRegister(pin);
      writeAllMask[b] = 0;
    }
    bankFor[i] = b;
    maskFor[i] = m;
    writeAllMask[b] |= m;
  }

  for (uint8_t b = 0; b < writeBankCount; b++) {
    for (uint16_t v = 0; v < 256; v++) {
      uint32_t s = 0;
      for (uint8_t i = 0; i < 8; i++) {
        if (bankFor[i] == b && (v & (1u << i))) {
          s |= maskFor[i];
        }
      }
      writeSetTable[b][v] = s;
    }
  }
  for (uint8_t b = writeBankCount; b < maxBanks; b++) {
    writeSetReg[b]  = &bankScratch;
    writeClrReg[b]  = &bankScratch;
    writeAllMask[b] = 0;
    for (uint16_t v = 0; v < 256; v++) {
      writeSetTable[b][v] = 0;
    }
  }

  readBankCount = 0;
  for (uint8_t i = 0; i < 8; i++) {
    uint8_t pin = PORT_C_PINS[i];
    volatile uint32_t *ir = portInputRegister(pin);

    uint8_t b = 0xFF;
    for (uint8_t k = 0; k < readBankCount; k++) {
      if (readReg[k] == ir) { b = k; break; }
    }
    if (b == 0xFF) {
      b = readBankCount++;
      readReg[b] = ir;
    }
    readBankOf[i] = b;
    readMaskOf[i] = digitalPinToBitMask(pin);
  }
  for (uint8_t b = readBankCount; b < maxBanks; b++) {
    readReg[b] = &bankScratch;
  }
}

static inline void fastWritePortA(uint8_t v) {
  uint32_t s0 = writeSetTable[0][v];
  uint32_t s1 = writeSetTable[1][v];
  uint32_t s2 = writeSetTable[2][v];
  uint32_t s3 = writeSetTable[3][v];
  *writeSetReg[0] = s0;  *writeClrReg[0] = writeAllMask[0] & ~s0;
  *writeSetReg[1] = s1;  *writeClrReg[1] = writeAllMask[1] & ~s1;
  *writeSetReg[2] = s2;  *writeClrReg[2] = writeAllMask[2] & ~s2;
  *writeSetReg[3] = s3;  *writeClrReg[3] = writeAllMask[3] & ~s3;
}

static inline uint8_t fastReadPortC() {
  uint32_t snap[maxBanks];
  snap[0] = *readReg[0];
  snap[1] = *readReg[1];
  snap[2] = *readReg[2];
  snap[3] = *readReg[3];

  uint8_t v = 0;
  if (snap[readBankOf[0]] & readMaskOf[0]) v |= 0x01;
  if (snap[readBankOf[1]] & readMaskOf[1]) v |= 0x02;
  if (snap[readBankOf[2]] & readMaskOf[2]) v |= 0x04;
  if (snap[readBankOf[3]] & readMaskOf[3]) v |= 0x08;
  if (snap[readBankOf[4]] & readMaskOf[4]) v |= 0x10;
  if (snap[readBankOf[5]] & readMaskOf[5]) v |= 0x20;
  if (snap[readBankOf[6]] & readMaskOf[6]) v |= 0x40;
  if (snap[readBankOf[7]] & readMaskOf[7]) v |= 0x80;
  return v;
}

#else   // ---------------------------------------------------- other targets

static void buildBanks() { }
static inline void fastWritePortA(uint8_t v) { writePortA(v); }
static inline uint8_t fastReadPortC() { return readPortC(); }

#endif

// ================================================================ state ====

static uint8_t       mode            = 0;   // modeIdle
static uint32_t      stepIndex       = 0;
static uint32_t      stepLimit       = 0;
static uint32_t      vectorsRun      = 0;
static uint32_t      errorCount      = 0;
static uint8_t       stickyDiff      = 0;
static uint8_t       errorPrints     = 0;
static uint32_t      runStartUs      = 0;
static uint32_t      nextProgressAt  = 0;
static unsigned long lastChaserStep  = 0;
static uint8_t       chaserBit       = 0;
static uint8_t       parkIndex       = 0;
static char          runLabel        = '-';

static uint16_t      sweepNs         = 0;
static bool          sweepCleanSeen  = false;
static uint16_t      sweepFirstClean = 0;

static uint8_t       measurePhase    = 0;    // 0 = header accessors, 1 = banked
static uint32_t      measureIndex    = 0;
static uint32_t      measureStartUs  = 0;
static uint32_t      measureSlowNs   = 0;

// Default: the header accessors. Faster here, and the verified mapping stays
// the single source of truth. 'F' selects the banked path when the harsher
// simultaneous slew is wanted.
static bool          useFastPath     = false;
static bool          pathWasAlive    = false;
static unsigned long lastAlivePoll   = 0;

// ============================================================= printing ====

static void printBin8(uint8_t v) {
  for (int8_t b = 7; b >= 0; b--) {
    Serial.write((v & (1u << b)) ? '1' : '0');
  }
}

static void printPinTable(const char *name, const uint8_t *pins) {
  Serial.print(name);
  for (uint8_t i = 0; i < 8; i++) {
    Serial.print(' ');
    Serial.print(pins[i]);
  }
  Serial.println();
}

static uint32_t nsPerVector(uint32_t elapsedUs, uint32_t vectors) {
  if (vectors == 0) {
    return 0;
  }
  return (uint32_t)(((uint64_t)elapsedUs * 1000ULL) / (uint64_t)vectors);
}

static void printHelp() {
  Serial.println(F("W walking 1/0   E exhaustive 256   H hammer 2M      L soak"));
  Serial.println(F("S settle sweep  M measure speed    V verify paths   C chaser"));
  Serial.println(F("P park pattern  F header/banked    A recheck path"));
  Serial.println(F("X stop          R last summary     ? help"));
}

static void printSummary() {
  uint32_t us = micros() - runStartUs;
  Serial.print(F("["));
  Serial.write(runLabel);
  Serial.print(F("] vectors="));
  Serial.print(vectorsRun);
  Serial.print(F("  errors="));
  Serial.print(errorCount);
  Serial.print(F("  us="));
  Serial.print(us);
  Serial.print(F("  ns/vec="));
  Serial.print(nsPerVector(us, vectorsRun));
  if (errorCount == 0) {
    Serial.println(F("  -> PASS"));
  } else {
    Serial.print(F("  bits ever wrong="));
    printBin8(stickyDiff);
    Serial.println(F("  -> FAIL"));
  }
}

// =========================================================== vector core ===

static inline void portWrite(uint8_t v) {
  if (useFastPath) {
    fastWritePortA(v);
  } else {
    writePortA(v);
  }
}

static inline uint8_t portRead() {
  return useFastPath ? fastReadPortC() : readPortC();
}

static bool applyVectorAt(uint8_t value, uint16_t delayNs) {
  portWrite(value);
  if (delayNs > 0) {
    delayNanoseconds(delayNs);
  }
  uint8_t got = portRead();

  vectorsRun++;
  uint8_t diff = (uint8_t)(got ^ value);
  if (diff == 0) {
    return true;
  }

  errorCount++;
  stickyDiff |= diff;
  if (errorPrints < maxErrorPrints) {
    errorPrints++;
    Serial.print(F("  MISMATCH sent="));
    printBin8(value);
    Serial.print(F(" got="));
    printBin8(got);
    Serial.print(F(" bad="));
    printBin8(diff);
    Serial.println();
    if (errorPrints == maxErrorPrints) {
      Serial.println(F("  (further mismatches counted, not printed)"));
    }
  }
  return false;
}

static bool applyVector(uint8_t value) {
  return applyVectorAt(value, settleNs);
}

static uint8_t vectorForStep(uint32_t n) {
  if (mode == modeWalking) {
    return (n < 8) ? (uint8_t)(1u << n) : (uint8_t)~(1u << (n - 8));
  }
  if (mode == modeExhaustive) {
    return (uint8_t)n;
  }
  if (mode == modeHammer) {
    return (n & 1u) ? 0xFF : 0x00;
  }
  return 0;
}

// ============================================================ path check ===

static bool pathAlive() {
  portWrite(0xFF);
  delayNanoseconds(2000);
  uint8_t hi = portRead();
  portWrite(0x00);
  delayNanoseconds(2000);
  uint8_t lo = portRead();
  return (hi == 0xFF && lo == 0x00);
}

static void reportPath(bool alive, bool verbose) {
  if (alive) {
    Serial.println(F("Path alive - both '245s enabled, readback follows Port A."));
  } else {
    Serial.println(F("Path DEAD - Port C does not follow Port A."));
    if (verbose) {
      Serial.println(F("  Most likely: HCT245 pin 19 (/OE) still tied to +5V."));
      Serial.println(F("  Move it to GND - this sketch will notice on its own."));
      Serial.println(F("  Otherwise check LVC245 /OE (pin 19 -> GND, not pin 20),"));
      Serial.println(F("  DIR polarity (HCT pin 1 high, LVC pin 1 low), and rails."));
    }
  }
}

// =========================================================== run control ===

static void stopRun(bool announce) {
  mode = modeIdle;
  portWrite(0);
  if (announce) {
    Serial.println(F("Stopped."));
  }
}

static void startTest(uint8_t which, char label, uint32_t limit) {
  mode           = which;
  runLabel       = label;
  stepIndex      = 0;
  stepLimit      = limit;
  vectorsRun     = 0;
  errorCount     = 0;
  stickyDiff     = 0;
  errorPrints    = 0;
  chaserBit      = 0;
  nextProgressAt = progressEvery;
  runStartUs     = micros();
  lastChaserStep = millis();

  Serial.print(F("Running ["));
  Serial.write(label);
  Serial.print(F("] "));
  if (limit == soakVectors) {
    Serial.println(F("until stopped..."));
  } else {
    Serial.print(limit);
    Serial.println(F(" vectors..."));
  }
}

static void startSweep() {
  mode            = modeSweep;
  runLabel        = 'S';
  vectorsRun      = 0;
  errorCount      = 0;
  stickyDiff      = 0;
  errorPrints     = maxErrorPrints;   // no per-vector spam during a sweep
  sweepNs         = 0;
  sweepCleanSeen  = false;
  sweepFirstClean = 0;
  runStartUs      = micros();

  Serial.println(F("Settle sweep, 0x00<->0xFF - all eight lines slewing."));
  Serial.println(F("Low rows should show errors. The first clean row is the"));
  Serial.println(F("real settle requirement for this wiring - use several"));
  Serial.println(F("times that as settleNs."));
#if !HAVE_BANKED_IO
  Serial.println(F("NOTE: no banked I/O on this target - resolution is poor."));
#endif
  Serial.println(F("  ns   errors"));
}

static void startMeasure() {
  mode           = modeMeasure;
  runLabel       = 'M';
  measurePhase   = 0;
  measureIndex   = 0;
  measureSlowNs  = 0;
  vectorsRun     = 0;
  errorCount     = 0;
  stickyDiff     = 0;
  errorPrints    = maxErrorPrints;
  runStartUs     = micros();
  measureStartUs = runStartUs;
  Serial.print(F("Measuring "));
  Serial.print(measureVectors);
  Serial.println(F(" vectors per path, no commanded settle..."));
}

static void verifyPaths() {
#if !HAVE_BANKED_IO
  Serial.println(F("No banked I/O on this target - nothing to verify."));
#else
  uint16_t bad = 0;
  for (uint16_t v = 0; v < 256; v++) {
    uint8_t pattern = (uint8_t)v;

    writePortA(pattern);
    delayNanoseconds(2000);
    uint8_t slowSlow = readPortC();
    uint8_t slowFast = fastReadPortC();

    fastWritePortA(pattern);
    delayNanoseconds(2000);
    uint8_t fastSlow = readPortC();
    uint8_t fastFast = fastReadPortC();

    if (slowSlow != pattern || slowFast != pattern ||
        fastSlow != pattern || fastFast != pattern) {
      bad++;
      if (bad <= 8) {
        Serial.print(F("  pattern "));
        printBin8(pattern);
        Serial.print(F("  hdr/hdr "));
        printBin8(slowSlow);
        Serial.print(F("  hdr/fast "));
        printBin8(slowFast);
        Serial.print(F("  fast/hdr "));
        printBin8(fastSlow);
        Serial.print(F("  fast/fast "));
        printBin8(fastFast);
        Serial.println();
      }
    }
  }
  portWrite(0);
  if (bad == 0) {
    Serial.println(F("Verify PASS - banked path agrees with the header accessors."));
  } else {
    Serial.print(F("Verify FAIL on "));
    Serial.print(bad);
    Serial.println(F(" of 256 patterns - stay on the header path."));
  }
#endif
}

static void parkNext() {
  if (mode != modePark) {
    mode        = modePark;
    runLabel    = 'P';
    parkIndex   = 0;
    vectorsRun  = 0;
    errorCount  = 0;
    stickyDiff  = 0;
    errorPrints = 0;
    runStartUs  = micros();
  } else {
    parkIndex = (uint8_t)((parkIndex + 1) % parkPatternCount);
  }

  uint8_t pattern = parkPatterns[parkIndex];
  portWrite(pattern);
  delayNanoseconds(2000);
  uint8_t got = portRead();

  Serial.print(F("Parked "));
  printBin8(pattern);
  Serial.print(F("  readback "));
  printBin8(got);
  if (got == pattern) {
    Serial.println(F("  ok"));
  } else {
    errorCount++;
    stickyDiff |= (uint8_t)(got ^ pattern);
    Serial.print(F("  MISMATCH bad="));
    printBin8((uint8_t)(got ^ pattern));
    Serial.println();
  }
  vectorsRun++;
}

static void reportIoPath() {
  Serial.print(F("I/O path: "));
  if (useFastPath) {
    Serial.println(F("banked (simultaneous slew, slower)"));
  } else {
    Serial.println(F("header accessors (default, faster)"));
  }
}

// ================================================================ setup ====

void setup() {
  Serial.begin(serialBaud);

  setPortMode(PORT_A_PINS, OUTPUT);
  setPortMode(PORT_C_PINS, INPUT);
  buildBanks();
  portWrite(0);

  while (!Serial && millis() < 3000) { }

  Serial.print(F("Level245Loop on "));
  Serial.println(F(PLATFORM_NAME));
  Serial.println(F("Port A -> HCT245 -> 5V bus (LED Thing) -> LVC245 -> Port C"));
  printPinTable("Port A out:", PORT_A_PINS);
  printPinTable("Port C in :", PORT_C_PINS);
#if HAVE_BANKED_IO
  Serial.print(F("GPIO banks: "));
  Serial.print(writeBankCount);
  Serial.print(F(" write, "));
  Serial.print(readBankCount);
  Serial.println(F(" read"));
#endif
  reportIoPath();
  Serial.print(F("settleNs = "));
  Serial.println(settleNs);

  pathWasAlive  = pathAlive();
  lastAlivePoll = millis();
  reportPath(pathWasAlive, true);

  printHelp();
}

// ================================================================= loop ====

void loop() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c >= 'a' && c <= 'z') {
      c = (char)(c - 'a' + 'A');
    }
    switch (c) {
      case 'W': startTest(modeWalking,    'W', 16);            break;
      case 'E': startTest(modeExhaustive, 'E', 256);           break;
      case 'H': startTest(modeHammer,     'H', hammerVectors); break;
      case 'L': startTest(modeHammer,     'L', soakVectors);   break;
      case 'S': startSweep();                                  break;
      case 'M': startMeasure();                                break;
      case 'V': verifyPaths();                                 break;
      case 'C': startTest(modeChaser,     'C', 0);             break;
      case 'P': parkNext();                                    break;
      case 'F': useFastPath = !useFastPath;
                reportIoPath();                                break;
      case 'A': pathWasAlive = pathAlive();
                reportPath(pathWasAlive, true);                break;
      case 'X': stopRun(true);                                 break;
      case 'R': printSummary();                                break;
      case '?': printHelp();                                   break;
      default:  break;
    }
  }

  // While idle, watch for the /OE jumper moving to GND and say so.
  if (mode == modeIdle) {
    if (millis() - lastAlivePoll >= alivePollMs) {
      lastAlivePoll = millis();
      bool alive = pathAlive();
      if (alive != pathWasAlive) {
        pathWasAlive = alive;
        reportPath(alive, !alive);
      }
      portWrite(0);
    }
    return;
  }

  if (mode == modePark) {
    return;
  }

  if (mode == modeChaser) {
    if (millis() - lastChaserStep >= chaserStepMs) {
      lastChaserStep = millis();
      applyVector((uint8_t)(1u << chaserBit));
      chaserBit = (uint8_t)((chaserBit + 1) & 7);
    }
    return;
  }

  // Throughput measurement: phase 0 header accessors, phase 1 banked.
  if (mode == modeMeasure) {
    bool savedPath = useFastPath;
    useFastPath = (measurePhase != 0);

    uint16_t budget = measurePerPass;
    while (budget-- > 0 && measureIndex < measureVectors) {
      portWrite((measureIndex & 1u) ? 0xFF : 0x00);
      (void)portRead();
      measureIndex++;
    }

    useFastPath = savedPath;

    if (measureIndex >= measureVectors) {
      uint32_t us = micros() - measureStartUs;
      uint32_t ns = nsPerVector(us, measureVectors);
      if (measurePhase == 0) {
        Serial.print(F("  header accessors: "));
      } else {
        Serial.print(F("  banked          : "));
      }
      Serial.print(ns);
      Serial.println(F(" ns/vector"));

      if (measurePhase == 0) {
        measureSlowNs  = ns;
        measurePhase   = 1;
        measureIndex   = 0;
        measureStartUs = micros();
#if !HAVE_BANKED_IO
        Serial.println(F("  (no banked I/O here - second figure is the same path)"));
#endif
      } else {
        if (ns > 0 && measureSlowNs > 0) {
          Serial.print(F("  header is "));
          Serial.print((ns * 100UL) / measureSlowNs);
          Serial.println(F("% of banked cost (under 100 = header wins)"));
        }
        stopRun(false);
      }
    }
    return;
  }

  if (mode == modeSweep) {
    uint32_t before = errorCount;
    for (uint16_t i = 0; i < sweepTrials; i++) {
      applyVectorAt(0xFF, sweepNs);
      applyVectorAt(0x00, sweepNs);
    }
    uint32_t hits = errorCount - before;

    Serial.print(F("  "));
    Serial.print(sweepNs);
    Serial.print(F("   "));
    Serial.println(hits);

    if (hits == 0 && !sweepCleanSeen) {
      sweepCleanSeen  = true;
      sweepFirstClean = sweepNs;
    }

    sweepNs = (uint16_t)(sweepNs + sweepStepNs);
    if (sweepNs > sweepMaxNs) {
      if (sweepCleanSeen && sweepFirstClean == 0) {
        Serial.println(F("Clean even at 0 ns commanded - the call overhead of"));
        Serial.println(F("delayNanoseconds alone is covering the pair. Margin is"));
        Serial.println(F("unmeasured; do not read this as zero requirement."));
      } else if (sweepCleanSeen) {
        Serial.print(F("First clean settle: "));
        Serial.print(sweepFirstClean);
        Serial.println(F(" ns. Set settleNs to several times this."));
      } else {
        Serial.println(F("Never came clean - fix wiring, /OE and DIR before timing."));
      }
      stopRun(false);
    }
    return;
  }

  // Batch tests.
  uint16_t budget = vectorsPerPass;
  while (budget-- > 0 && stepIndex < stepLimit) {
    applyVector(vectorForStep(stepIndex));
    stepIndex++;
  }

  if (vectorsRun >= nextProgressAt) {
    nextProgressAt += progressEvery;
    uint32_t us = micros() - runStartUs;
    Serial.print(F("  ... "));
    Serial.print(vectorsRun);
    Serial.print(F(" vectors, "));
    Serial.print(errorCount);
    Serial.print(F(" errors, "));
    Serial.print(nsPerVector(us, vectorsRun));
    Serial.println(F(" ns/vec"));
  }

  if (stepIndex >= stepLimit) {
    printSummary();
    stopRun(false);
  }
}
