// =====================================================================
// TTLSimGalTest -- in-circuit test of the three TTLSim GAL16V8 test
// burns (simple / registered / complex mode) on an Arduino Mega 2560.
//
// Wiring: the SAME physical harness as BlinkyGalTest (bottom-edge
// header, outer row, per Mega_GAL_harness / TTLSim_GAL16V8_harness
// diagrams). No rewiring between the two sketches: GAL pins 12/13
// (D22/D24, series 470R-1k) are unused by all three burns, so the
// Mega keeps them as idle inputs throughout.
//
// The expected values are the design equations from the .pld sources
// (gal16v8_simple / _registered / _complex), computed independently of
// the fuse maps -- a PASS means the physical chip implements the
// design intent, closing the loop with the BlinkJED-vs-WinCUPL diff
// and the TTLSim live-simulation tests.
//
// Usage: socket a chip, open Serial Monitor at 115200, type 1..3.
//   1 = gal16v8_simple      (G16V8AS -- pins 1/11 are plain inputs)
//   2 = gal16v8_registered  (G16V8MS -- pin 1 = CLK, pin 11 = /OE)
//   3 = gal16v8_complex     (G16V8MA -- per-pin OE, feedback)
// Power down (or at least press reset) before swapping chips.
//
// Test techniques beyond a plain combinational sweep:
//   - Registered: clocked counter walk, synchronous-reset checks, and
//     a global /OE Hi-Z proof (internal state 000 + INPUT_PULLUP: a
//     driven pin would read 0, a floating pin reads 1). The carry
//     decode is watched while /OE is high to prove the register
//     feedback is internal (from Q), not from the pins.
//   - Complex: y2's per-pin tri-state is proven the same way -- with
//     ena=0 and a==b, a driven y2 would read 0; INPUT_PULLUP reads 1
//     only if the pin genuinely floated.
// =====================================================================

// ------------------ harness pin map (GAL pin -> Mega pin) ------------
const uint8_t PIN_G1     = 53;                    // GAL pin 1  (f / clk / low)
const uint8_t PIN_IN[4]  = { 51, 49, 47, 45 };    // GAL pins 2..5
const uint8_t PIN_HOLD[4]= { 43, 41, 39, 37 };    // GAL pins 6..9  (held low)
const uint8_t PIN_G11    = 23;                    // GAL pin 11 (g / /OE / ena)
const uint8_t PIN_P12    = 22;                    // GAL pin 12 (series R, idle)
const uint8_t PIN_P13    = 24;                    // GAL pin 13 (series R, idle)
const uint8_t PIN_P14    = 35;                    // GAL pin 14
const uint8_t PIN_P15    = 33;                    // GAL pin 15 (unused)
const uint8_t PIN_P16    = 31;                    // GAL pin 16
const uint8_t PIN_P17    = 29;                    // GAL pin 17
const uint8_t PIN_P18    = 27;                    // GAL pin 18
const uint8_t PIN_P19    = 25;                    // GAL pin 19

const uint8_t SETTLE_US  = 2;    // ~130x the 15 ns tpd, same as BlinkyGalTest

// ------------------ reporting ----------------------------------------
const uint8_t MAX_REPORT = 24;
uint32_t failures, checks;

// One failure line: "FAIL <vector>  <signal>: expected E got G"
void reportFail(const char *vector, const char *signalName, bool expected, bool got) {
  failures++;
  if (failures > MAX_REPORT) return;
  Serial.print(F("FAIL "));
  Serial.print(vector);
  Serial.print(F("  ")); Serial.print(signalName);
  Serial.print(F(": expected ")); Serial.print(expected);
  Serial.print(F(" got ")); Serial.println(got);
}

void check(const char *vector, const char *signalName, uint8_t megaPin, bool expected) {
  bool got = digitalRead(megaPin);
  checks++;
  if (got != expected) reportFail(vector, signalName, expected, got);
}

void summarize(const char *name, unsigned long t0) {
  unsigned long elapsed = millis() - t0;
  if (failures > MAX_REPORT) {
    Serial.print(F("... and ")); Serial.print(failures - MAX_REPORT);
    Serial.println(F(" more failures not shown."));
  }
  Serial.print(F("Checked ")); Serial.print(checks);
  Serial.print(F(" output values in ")); Serial.print(elapsed);
  Serial.println(F(" ms."));
  if (failures == 0) {
    Serial.print(F("*** PASS: ")); Serial.print(name);
    Serial.println(F(" matches the .pld design equations. ***"));
  } else {
    Serial.print(F("*** FAIL: ")); Serial.print(failures);
    Serial.println(F(" mismatches. ***"));
  }
}

// ------------------ harness driving ----------------------------------
// Common setup: pins 1..9 and 11 driven low, pins 12..19 read.
// readPullup: bitmask over GAL pins 14..19 (bit k = pin 14+k) selecting
// which read pins get INPUT_PULLUP instead of plain INPUT.
void configurePins(uint8_t readPullupMask) {
  pinMode(PIN_G1, OUTPUT);  digitalWrite(PIN_G1, LOW);
  pinMode(PIN_G11, OUTPUT); digitalWrite(PIN_G11, LOW);
  for (uint8_t k = 0; k < 4; k++) {
    pinMode(PIN_IN[k], OUTPUT);   digitalWrite(PIN_IN[k], LOW);
    pinMode(PIN_HOLD[k], OUTPUT); digitalWrite(PIN_HOLD[k], LOW);
  }
  const uint8_t readPins[6] = { PIN_P14, PIN_P15, PIN_P16, PIN_P17, PIN_P18, PIN_P19 };
  for (uint8_t k = 0; k < 6; k++)
    pinMode(readPins[k], (readPullupMask >> k) & 1 ? INPUT_PULLUP : INPUT);
  pinMode(PIN_P12, INPUT);   // spares behind series R: idle on all three burns
  pinMode(PIN_P13, INPUT);
}

void idlePins() {                  // safe state for chip swapping
  const uint8_t all[12] = { PIN_G1, PIN_IN[0], PIN_IN[1], PIN_IN[2], PIN_IN[3],
                            PIN_HOLD[0], PIN_HOLD[1], PIN_HOLD[2], PIN_HOLD[3],
                            PIN_G11, PIN_P12, PIN_P13 };
  for (uint8_t k = 0; k < 12; k++) pinMode(all[k], INPUT);
}

// Format a vector description into a shared buffer (kept tiny; only one
// vector is ever being reported at a time).
char vecBuf[40];

// =====================================================================
// Test 1 -- gal16v8_simple (G16V8AS)
//   pin 2=a  3=b  4=c  5=d  1=f  11=g   (pins 1/11 are PLAIN INPUTS)
//   pin 19 yand = a&b&c&d   18 yor = a|b|c|d   17 yxor = a^b   14 yfg = f&g
// Sweep all 64 input combinations.
// =====================================================================
void runSimple() {
  Serial.println(F("\nTesting gal16v8_simple -- 64 input combinations..."));
  configurePins(0);                      // plain INPUT on all read pins
  failures = 0; checks = 0;
  unsigned long t0 = millis();

  for (uint8_t v = 0; v < 64; v++) {
    bool a = v & 1, b = (v >> 1) & 1, c = (v >> 2) & 1,
         d = (v >> 3) & 1, f = (v >> 4) & 1, g = (v >> 5) & 1;
    digitalWrite(PIN_IN[0], a);
    digitalWrite(PIN_IN[1], b);
    digitalWrite(PIN_IN[2], c);
    digitalWrite(PIN_IN[3], d);
    digitalWrite(PIN_G1,  f);            // proves pin 1 is a plain input
    digitalWrite(PIN_G11, g);            // proves pin 11 is a plain input
    delayMicroseconds(SETTLE_US);

    snprintf(vecBuf, sizeof vecBuf, "a=%d b=%d c=%d d=%d f=%d g=%d", a, b, c, d, f, g);
    check(vecBuf, "yand", PIN_P19, a && b && c && d);
    check(vecBuf, "yor",  PIN_P18, a || b || c || d);
    check(vecBuf, "yxor", PIN_P17, a != b);
    check(vecBuf, "yfg",  PIN_P14, f && g);
  }

  summarize("gal16v8_simple", t0);
  idlePins();
}

// =====================================================================
// Test 2 -- gal16v8_registered (G16V8MS)
//   pin 1 = clk (rising edge)   pin 11 = /OE (0 = drive)   pin 2 = rst
//   pin 19/18/17 = q0/q1/q2 (registered)   pin 14 carry = q0&q1&q2
//   q0.d = !q0&!rst   q1.d = (q1^q0)&!rst   q2.d = (q2^(q1&q0))&!rst
// Phases: reset, 24-step counter walk, mid-count reset, /OE Hi-Z proof.
// =====================================================================
void clockPulse() {
  digitalWrite(PIN_G1, HIGH); delayMicroseconds(SETTLE_US);
  digitalWrite(PIN_G1, LOW);  delayMicroseconds(SETTLE_US);
}

void checkState(const char *phase, uint8_t state) {
  snprintf(vecBuf, sizeof vecBuf, "%s (expect %d%d%d)",
           phase, (state >> 2) & 1, (state >> 1) & 1, state & 1);
  check(vecBuf, "q0", PIN_P19, state & 1);
  check(vecBuf, "q1", PIN_P18, (state >> 1) & 1);
  check(vecBuf, "q2", PIN_P17, (state >> 2) & 1);
  check(vecBuf, "carry", PIN_P14, state == 7);
}

void runRegistered() {
  Serial.println(F("\nTesting gal16v8_registered -- counter walk, reset, /OE..."));
  // Pullups on q0/q1/q2 (GAL pins 19/18/17): irrelevant while /OE is low
  // (the GAL drives hard), decisive for the Hi-Z proof while /OE is high.
  configurePins((1 << 5) | (1 << 4) | (1 << 3));
  failures = 0; checks = 0;
  unsigned long t0 = millis();

  // Phase 1: synchronous reset into a known state. /OE low = outputs on.
  digitalWrite(PIN_G11, LOW);            // /OE asserted
  digitalWrite(PIN_IN[0], HIGH);         // rst = 1
  clockPulse(); clockPulse();
  checkState("reset", 0);

  // Phase 2: free-running count, three full wraps.
  digitalWrite(PIN_IN[0], LOW);          // rst = 0
  for (uint8_t step = 1; step <= 24; step++) {
    clockPulse();
    snprintf(vecBuf, sizeof vecBuf, "count step %u", step);
    checkState(vecBuf, step & 7);
  }

  // Phase 3: synchronous reset from mid-count (state is 24&7 = 0; count
  // to 5 first so the reset actually has bits to clear).
  for (uint8_t step = 0; step < 5; step++) clockPulse();
  checkState("pre-reset", 5);
  digitalWrite(PIN_IN[0], HIGH);         // rst = 1
  clockPulse();
  checkState("mid-count reset", 0);
  digitalWrite(PIN_IN[0], LOW);

  // Phase 4: /OE Hi-Z proof. Internal state is 000, so driven pins would
  // read 0 -- the pullups reading 1 proves the outputs floated.
  digitalWrite(PIN_G11, HIGH);           // /OE deasserted
  delayMicroseconds(SETTLE_US);
  check("/OE=1 state 000", "q0 (Hi-Z)", PIN_P19, 1);
  check("/OE=1 state 000", "q1 (Hi-Z)", PIN_P18, 1);
  check("/OE=1 state 000", "q2 (Hi-Z)", PIN_P17, 1);
  // carry is a combinational macrocell: not gated by the global /OE, and
  // fed from the registers' INTERNAL Q feedback. Clock 7 edges blind --
  // carry going high proves the counter kept counting with its pins off.
  check("/OE=1 state 000", "carry", PIN_P14, 0);
  for (uint8_t step = 0; step < 7; step++) clockPulse();
  check("/OE=1 state 111", "carry", PIN_P14, 1);
  // Re-enable and confirm the hidden state is exactly 111.
  digitalWrite(PIN_G11, LOW);            // /OE asserted again
  delayMicroseconds(SETTLE_US);
  checkState("/OE re-enabled", 7);
  clockPulse();
  checkState("wrap after /OE", 0);

  summarize("gal16v8_registered", t0);
  idlePins();
}

// =====================================================================
// Test 3 -- gal16v8_complex (G16V8MA)
//   pin 2=a  3=b  4=c  5=sel  11=ena   (pin 1 unused, held low)
//   pin 19 y1 = a&b | c            (output-only in complex mode)
//   pin 18 y2 = a^b, y2.oe = ena   (per-macrocell tri-state)
//   pin 17 fb = a&c                (drives pin AND feeds back)
//   pin 16 g  = fb | sel           (consumes the fb feedback)
// Sweep 32 combinations (a,b,c,sel x ena). With ena=0, y2 must float:
// INPUT_PULLUP on its Mega pin reads 1, and the a==b vectors (driven
// value would be 0) are the ones that prove it.
// =====================================================================
void runComplex() {
  Serial.println(F("\nTesting gal16v8_complex -- 32 combinations incl. tri-state..."));
  configurePins(1 << 4);                 // pullup on GAL pin 18 (y2) only
  failures = 0; checks = 0;
  unsigned long t0 = millis();

  for (uint8_t v = 0; v < 32; v++) {
    bool a = v & 1, b = (v >> 1) & 1, c = (v >> 2) & 1,
         sel = (v >> 3) & 1, ena = (v >> 4) & 1;
    digitalWrite(PIN_IN[0], a);
    digitalWrite(PIN_IN[1], b);
    digitalWrite(PIN_IN[2], c);
    digitalWrite(PIN_IN[3], sel);
    digitalWrite(PIN_G11, ena);
    delayMicroseconds(SETTLE_US);

    snprintf(vecBuf, sizeof vecBuf, "a=%d b=%d c=%d sel=%d ena=%d", a, b, c, sel, ena);
    check(vecBuf, "y1", PIN_P19, (a && b) || c);
    check(vecBuf, "fb", PIN_P17, a && c);
    check(vecBuf, "g",  PIN_P16, (a && c) || sel);
    // y2: driven a^b when ena=1; floating (pullup -> 1) when ena=0.
    check(vecBuf, ena ? "y2" : "y2 (Hi-Z)", PIN_P18, ena ? (a != b) : true);
  }

  summarize("gal16v8_complex", t0);
  idlePins();
}

// ------------------ menu ---------------------------------------------
void printMenu() {
  Serial.println(F("\n=============================================="));
  Serial.println(F("TTLSim GAL16V8 burn tester"));
  Serial.println(F("Socket the chip FIRST, then choose:"));
  Serial.println(F("  1 = gal16v8_simple      (G16V8AS)"));
  Serial.println(F("  2 = gal16v8_registered  (G16V8MS)"));
  Serial.println(F("  3 = gal16v8_complex     (G16V8MA)"));
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
  if (ch == '1') runSimple();
  else if (ch == '2') runRegistered();
  else if (ch == '3') runComplex();
  else if (ch > ' ') Serial.println(F("Type 1, 2 or 3."));
  if (ch > ' ') printMenu();
}
