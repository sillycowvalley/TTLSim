// ============================================================================
//  Level245Loop.ino  -  74HCT245 / 74LVC245 8-bit level-translation loopback
//
//  All eight lines, Port A -> Port C, through the push-pull translation pair
//  that replaces the TXS0108E path.
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
//  appear on Port C. No buttons in this build.
//
//  All port plumbing - pin mapping, unrolled accessors, banked I/O, banner
//  helpers - lives in AdapterPorts.h. This sketch holds only test logic.
//
//  ------------------------------------------------------------- RESULTS ----
//  Campaign record for this board, 2026-07:
//    - W, E clean on both I/O paths.
//    - Hammer (0x00<->0xFF, all eight lines): 17,000,000 vectors clean on
//      the banked path, 10,000,000+ on the unrolled path. The TXS0108E path
//      failed the equivalent stress at up to ~90% per event. The HCT245 /
//      LVC245 pair removes that failure class.
//    - Banked and unrolled agree on all 256 patterns ('V').
//    - The two I/O paths cost the same in real use: 192 vs 187 ns/vector
//      measured with the read consumed, 285 vs 290 in a hammer run. Earlier
//      figures showing banked far ahead came from a microbenchmark that
//      discarded the read, letting the compiler delete banked's arithmetic.
//    - Settle requirement is non-zero but below software resolution here -
//      see SETTLE below.
//  Still open: there is no clock and no register in this loopback, so the
//  original phantom-capture failure is neither reproduced nor excluded. That
//  needs a '574 on the 5V bus clocked from a ninth line, running the chase.
//
//  NOTE ON STYLE: no enums or other sketch-level types appear in any function
//  signature. The Arduino build inserts generated prototypes above the point
//  where a sketch-level type is declared, so such a signature fails with
//  "variable or field declared void". Mode values are plain uint8_t constants
//  for that reason - do not "tidy" them into an enum. (Types defined in the
//  header are fine: the include sits above the generated prototypes.)
//
//  --------------------------------------------------------- THE TWO PATHS --
//  'F' switches between the header's unrolled accessors and its banked I/O.
//  They cost the same; banked exists for simultaneity, not speed - see the
//  banked I/O commentary in AdapterPorts.h. Default is unrolled.
//
//  'S' overrides that and forces the banked path for the duration of the
//  sweep, restoring the previous choice afterwards. A sweep on the unrolled
//  path measures nothing: its eight staggered stores already delay the
//  sample past what is being resolved, so every row reads zero.
//
//  --------------------------------------------------------------- SETTLE ---
//  settleNs = 50, from measurement rather than guesswork.
//
//  What the banked sweep established: a banked write followed immediately by
//  a banked read fails every trial, while the same pair separated by
//  delayNanoseconds(0) is clean. That call costs tens of ns in overhead
//  (cycle counter read plus compare loop) before it delays anything, so the
//  requirement lies inside that gap - non-zero, but below what this rig can
//  resolve. Consistent with the datasheet sum of ~15 ns (HCT245 ~9-13 ns at
//  5V, LVC245 ~3.5-5 ns, plus wiring).
//
//  An earlier sweep appeared to show a sharp cliff at 10 ns. That was an
//  artifact: the delay call was skipped entirely at zero, so row 0 omitted
//  the overhead every other row paid, and the overhead appearing looked like
//  propagation. The sweep now calls delayNanoseconds at every row.
//
//  Commanded ns therefore UNDERSTATES elapsed time. settleNs = 50 commanded
//  lands near 70-90 ns elapsed, roughly 2-4x the requirement. Empirically
//  2,000,000 hammer vectors run clean at it.
//  Raise it to 100 if more headroom is wanted; the cost is ~15% of vector
//  time.
//
//  Do not set it to zero. At zero the readback samples mid-transition and
//  slower channels return the PREVIOUS vector on that line - walking-1 0x08
//  read back as 0x0C after 0x04. A nominal "2" appeared to work only because
//  the call overhead was doing the work; a core or compiler change could
//  shrink that away silently.
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
//  S  settle sweep (forces banked for the run)
//  M  measure throughput, both I/O paths
//  V  verify banked path == unrolled accessors (256 patterns)
//  C  chaser, verified, slow enough to watch
//  P  park a static pattern and hold it       press again to advance
//  F  toggle unrolled/banked I/O path
//  A  recheck the path (/OE jumper)
//  X  stop        R  last summary        ?  help
// ============================================================================

#include "AdapterPorts.h"

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
static const uint16_t      settleNs       = 50;       // measured - see SETTLE note
static const unsigned long chaserStepMs   = 100;
static const uint16_t      vectorsPerPass = 2000;     // chunk - keeps loop() responsive
static const uint8_t       maxErrorPrints = 12;
static const uint32_t      hammerVectors  = 2000000UL;
static const uint32_t      soakVectors    = 0xFFFFFFFFUL;
static const uint32_t      progressEvery  = 1000000UL;
static const unsigned long alivePollMs    = 750;
static const uint32_t      measureVectors = 200000UL;
static const uint16_t      measurePerPass = 20000;

// Sweep resolution: the cliff sits at the bottom, so step finely and stop
// early - everything above 200 ns has always been clean.
static const uint16_t sweepStepNs = 5;
static const uint16_t sweepMaxNs  = 200;
static const uint16_t sweepTrials = 500;   // 0x00/0xFF pairs per delay point

static const uint8_t parkPatterns[]   = { 0x00, 0xFF, 0x55, 0xAA, 0x0F, 0xF0 };
static const uint8_t parkPatternCount = (uint8_t)(sizeof(parkPatterns) / sizeof(parkPatterns[0]));

// Banked maps for the two ports this harness touches.
static PortBanks banksA;
static PortBanks banksC;

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
static bool          sweepFailSeen   = false;
static uint16_t      sweepLastFail   = 0;
static bool          sweepSavedPath  = false;

static uint8_t       measurePhase    = 0;    // 0 = unrolled, 1 = banked
static uint32_t      measureIndex    = 0;
static uint32_t      measureStartUs  = 0;
static uint32_t      measureSlowNs   = 0;
static uint32_t      measureChecksum = 0;

// Default: the unrolled accessors. 'F' selects banked when the harsher
// simultaneous slew is wanted; 'S' forces banked for its own duration.
static bool          useBankedIo     = false;
static bool          pathWasAlive    = false;
static unsigned long lastAlivePoll   = 0;

// ============================================================= printing ====

static void printBin8(uint8_t v) {
  for (int8_t b = 7; b >= 0; b--) {
    Serial.write((v & (1u << b)) ? '1' : '0');
  }
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
  Serial.println(F("P park pattern  F unrolled/banked  A recheck path"));
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

static void reportIoPath() {
  Serial.print(F("I/O path: "));
  if (useBankedIo) {
    Serial.println(F("banked (simultaneous slew)"));
  } else {
    Serial.println(F("unrolled accessors (default)"));
  }
}

// =========================================================== vector core ===

static inline void portWrite(uint8_t v) {
  if (useBankedIo) {
    bankedWritePort(banksA, v);
  } else {
    writePortA(v);
  }
}

static inline uint8_t portRead() {
  return useBankedIo ? bankedReadPort(banksC) : readPortC();
}

// Normal vector: no delay call at all when delayNs is zero.
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

// Sweep vector: ALWAYS calls delayNanoseconds, even at zero. Skipping the
// call at zero made the first row structurally different from every other -
// it omitted tens of ns of call overhead - which turned the overhead
// appearing into a fake cliff between row 0 and row 1. Every row now pays
// the same overhead, so differences between rows are the delay itself.
static void applyVectorSweep(uint8_t value, uint16_t delayNs) {
  portWrite(value);
  delayNanoseconds(delayNs);
  uint8_t got = portRead();

  vectorsRun++;
  uint8_t diff = (uint8_t)(got ^ value);
  if (diff != 0) {
    errorCount++;
    stickyDiff |= diff;
  }
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
  if (mode == modeSweep) {
    useBankedIo = sweepSavedPath;   // sweep borrowed the banked path
  }
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
  sweepSavedPath  = useBankedIo;
  useBankedIo     = true;           // only the banked path can resolve this

  mode            = modeSweep;
  runLabel        = 'S';
  vectorsRun      = 0;
  errorCount      = 0;
  stickyDiff      = 0;
  errorPrints     = maxErrorPrints;
  sweepNs         = 0;
  sweepCleanSeen  = false;
  sweepFirstClean = 0;
  sweepFailSeen   = false;
  sweepLastFail   = 0;
  runStartUs      = micros();

  Serial.println(F("Settle sweep, 0x00<->0xFF - all eight lines slewing."));
  Serial.println(F("Forcing the banked path: the unrolled accessors stagger"));
  Serial.println(F("their stores enough to hide what is being measured."));
  Serial.println(F("Commanded ns EXCLUDES delayNanoseconds' call overhead"));
  Serial.println(F("(tens of ns), which every row pays equally."));
  Serial.println(F("  ns   errors"));
}

static void startMeasure() {
  mode            = modeMeasure;
  runLabel        = 'M';
  measurePhase    = 0;
  measureIndex    = 0;
  measureSlowNs   = 0;
  measureChecksum = 0;
  vectorsRun      = 0;
  errorCount      = 0;
  stickyDiff      = 0;
  errorPrints     = maxErrorPrints;
  runStartUs      = micros();
  measureStartUs  = runStartUs;
  Serial.print(F("Measuring "));
  Serial.print(measureVectors);
  Serial.print(F(" vectors per path at settleNs = "));
  Serial.println(settleNs);
  Serial.println(F("(reads accumulate into a checksum, so neither path's work"));
  Serial.println(F(" can be optimised away and both must read correctly)"));
}

static void verifyPaths() {
  uint16_t bad = 0;
  for (uint16_t v = 0; v < 256; v++) {
    uint8_t pattern = (uint8_t)v;

    writePortA(pattern);
    delayNanoseconds(2000);
    uint8_t unrUnr  = readPortC();
    uint8_t unrBank = bankedReadPort(banksC);

    bankedWritePort(banksA, pattern);
    delayNanoseconds(2000);
    uint8_t bankUnr  = readPortC();
    uint8_t bankBank = bankedReadPort(banksC);

    if (unrUnr != pattern || unrBank != pattern ||
        bankUnr != pattern || bankBank != pattern) {
      bad++;
      if (bad <= 8) {
        Serial.print(F("  pattern "));
        printBin8(pattern);
        Serial.print(F("  unr/unr "));
        printBin8(unrUnr);
        Serial.print(F("  unr/bank "));
        printBin8(unrBank);
        Serial.print(F("  bank/unr "));
        printBin8(bankUnr);
        Serial.print(F("  bank/bank "));
        printBin8(bankBank);
        Serial.println();
      }
    }
  }
  portWrite(0);
  if (bad == 0) {
    Serial.println(F("Verify PASS - banked path agrees with the unrolled accessors."));
  } else {
    Serial.print(F("Verify FAIL on "));
    Serial.print(bad);
    Serial.println(F(" of 256 patterns - stay on the unrolled path."));
  }
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

// ================================================================ setup ====

void setup() {
  Serial.begin(serialBaud);

  setPortMode(PORT_A_PINS, OUTPUT);
  setPortMode(PORT_C_PINS, INPUT);
  buildPortBanks(banksA, PORT_A_PINS);
  buildPortBanks(banksC, PORT_C_PINS);
  portWrite(0);

  while (!Serial && millis() < 3000) { }

  Serial.print(F("Level245Loop on "));
  Serial.println(F(PLATFORM_NAME));
  Serial.println(F("Port A -> HCT245 -> 5V bus (LED Thing) -> LVC245 -> Port C"));
  printPortPins("Port A out:", PORT_A_PINS);
  printPortPins("Port C in :", PORT_C_PINS);
  printPortBanks("Port A:", banksA);
  printPortBanks("Port C:", banksC);
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
      case 'F': useBankedIo = !useBankedIo;
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

  // Throughput measurement: phase 0 unrolled, phase 1 banked. Runs at the
  // real settleNs so both paths read correctly and the checksum means
  // something; the read is folded in so neither path's work is elidable.
  if (mode == modeMeasure) {
    bool savedPath = useBankedIo;
    useBankedIo = (measurePhase != 0);

    uint16_t budget = measurePerPass;
    while (budget-- > 0 && measureIndex < measureVectors) {
      portWrite((measureIndex & 1u) ? 0xFF : 0x00);
      delayNanoseconds(settleNs);
      measureChecksum += portRead();
      measureIndex++;
    }

    useBankedIo = savedPath;

    if (measureIndex >= measureVectors) {
      uint32_t us = micros() - measureStartUs;
      uint32_t ns = nsPerVector(us, measureVectors);
      uint32_t expected = (measureVectors / 2UL) * 255UL;

      if (measurePhase == 0) {
        Serial.print(F("  unrolled: "));
      } else {
        Serial.print(F("  banked  : "));
      }
      Serial.print(ns);
      Serial.print(F(" ns/vector   checksum "));
      Serial.print(measureChecksum);
      if (measureChecksum == expected) {
        Serial.println(F(" ok"));
      } else {
        Serial.print(F(" WRONG, expected "));
        Serial.println(expected);
      }

      if (measurePhase == 0) {
        measureSlowNs   = ns;
        measurePhase    = 1;
        measureIndex    = 0;
        measureChecksum = 0;
        measureStartUs  = micros();
      } else {
        if (ns > 0 && measureSlowNs > 0) {
          Serial.print(F("  unrolled is "));
          Serial.print((measureSlowNs * 100UL) / ns);
          Serial.println(F("% of banked cost (under 100 = unrolled wins)"));
        }
        stopRun(false);
      }
    }
    return;
  }

  if (mode == modeSweep) {
    uint32_t before = errorCount;
    for (uint16_t i = 0; i < sweepTrials; i++) {
      applyVectorSweep(0xFF, sweepNs);
      applyVectorSweep(0x00, sweepNs);
    }
    uint32_t hits = errorCount - before;

    Serial.print(F("  "));
    Serial.print(sweepNs);
    Serial.print(F("   "));
    Serial.println(hits);

    if (hits == 0) {
      if (!sweepCleanSeen) {
        sweepCleanSeen  = true;
        sweepFirstClean = sweepNs;
      }
    } else {
      sweepFailSeen   = true;
      sweepLastFail   = sweepNs;
      sweepCleanSeen  = false;    // a later failure invalidates an early clean
      sweepFirstClean = 0;
    }

    sweepNs = (uint16_t)(sweepNs + sweepStepNs);
    if (sweepNs > sweepMaxNs) {
      if (!sweepCleanSeen) {
        Serial.println(F("Never came clean - fix wiring, /OE and DIR first."));
      } else if (!sweepFailSeen) {
        Serial.println(F("Clean at every row, including zero commanded delay."));
        Serial.println(F("delayNanoseconds' call overhead alone already covers"));
        Serial.println(F("the pair, so the requirement is below what this rig"));
        Serial.println(F("can resolve. Keep settleNs explicit regardless."));
      } else {
        Serial.print(F("Cliff between "));
        Serial.print(sweepLastFail);
        Serial.print(F(" and "));
        Serial.print(sweepFirstClean);
        Serial.println(F(" ns commanded, plus the shared call overhead."));
        Serial.println(F("True elapsed requirement is larger than the commanded"));
        Serial.println(F("figure - budget settleNs against elapsed, not this."));
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
