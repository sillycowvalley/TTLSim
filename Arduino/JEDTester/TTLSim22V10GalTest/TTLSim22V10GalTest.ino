// =====================================================================
// TTLSim22V10GalTest -- in-circuit test of the two TTLSim GAL22V10
// geometry-validation burns (combinational / registered) on an Arduino
// Mega 2560. Also fits the fuse-compatible ATF22V10.
//
// Wiring: per TTLSim_GAL22V10_harness.svg. The combinational burn uses
// ALL 12 dedicated inputs, so this harness needs the whole outer row
// plus five inner-row pins (D52/D50 = inputs i/j, D30/D28/D26 = OLMC
// pins 16/15/14). All pin directions are fixed across both burns: the
// Mega only ever drives GAL inputs and only ever reads OLMC pins, so
// no series resistors are involved. GAL pin 17 (unused OLMC in both
// burns) is left unconnected.
//
// Note the 22V10 architecture differences this sketch exercises:
//   - NO global /OE: pin 13 is a plain array input (signal k). The
//     tri-state test targets y21's private .oe product term instead.
//   - AR product term (rst): ASYNCHRONOUS, level-sensitive clear of
//     every register -- proven by clearing with the clock held low.
//   - SP product term (pre): SYNCHRONOUS preset to 1 of every register,
//     taking effect only on a rising clock edge -- proven by asserting
//     pre with no clock (state must NOT change) and then clocking once.
//   - qb0 (pin 20, registered active-low) pipelines q0 by one clock,
//     exercising registered polarity and register feedback.
//
// The expected values are the design equations from the .pld sources,
// computed independently of the fuse maps -- a PASS means the physical
// chip implements the design intent.
//
// Usage: socket a chip, open Serial Monitor at 115200, type 1..2.
//   1 = gal22v10_combinational   (4096-vector exhaustive sweep)
//   2 = gal22v10_registered      (counter / AR / SP / qb0 walk)
// Power down (or at least press reset) before swapping chips.
// =====================================================================

// ------------------ harness pin map (GAL pin -> Mega pin) ------------
const uint8_t PIN_G1     = 53;                        // GAL pin 1  (m / clk)
const uint8_t PIN_IN[10] = { 51, 49, 47, 45, 43,      // GAL pins 2..6  (a..e)
                             41, 39, 37, 52, 50 };    // GAL pins 7..11 (f..j)
const uint8_t PIN_G13    = 23;                        // GAL pin 13 (k)
const uint8_t PIN_P14    = 26;                        // GAL pin 14 (inner row)
const uint8_t PIN_P15    = 28;                        // GAL pin 15 (inner row)
const uint8_t PIN_P16    = 30;                        // GAL pin 16 (inner row)
const uint8_t PIN_P18    = 35;                        // GAL pin 18
const uint8_t PIN_P19    = 33;                        // GAL pin 19
const uint8_t PIN_P20    = 31;                        // GAL pin 20
const uint8_t PIN_P21    = 29;                        // GAL pin 21
const uint8_t PIN_P22    = 27;                        // GAL pin 22
const uint8_t PIN_P23    = 25;                        // GAL pin 23
// GAL pin 17: unconnected (unused OLMC in both burns).

const uint8_t SETTLE_US  = 2;    // generous vs a 15-25 ns part

// ------------------ reporting ----------------------------------------
const uint8_t MAX_REPORT = 24;
uint32_t failures, checks;

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
// Common setup: pins 1..11 and 13 driven low, the nine OLMC pins read.
// readPullupMask: bit k selects INPUT_PULLUP for readPins[k], in the
// order { P14, P15, P16, P18, P19, P20, P21, P22, P23 } (bits 0..8).
void configurePins(uint16_t readPullupMask) {
  pinMode(PIN_G1, OUTPUT);  digitalWrite(PIN_G1, LOW);
  pinMode(PIN_G13, OUTPUT); digitalWrite(PIN_G13, LOW);
  for (uint8_t k = 0; k < 10; k++) {
    pinMode(PIN_IN[k], OUTPUT); digitalWrite(PIN_IN[k], LOW);
  }
  const uint8_t readPins[9] = { PIN_P14, PIN_P15, PIN_P16, PIN_P18, PIN_P19,
                                PIN_P20, PIN_P21, PIN_P22, PIN_P23 };
  for (uint8_t k = 0; k < 9; k++)
    pinMode(readPins[k], (readPullupMask >> k) & 1 ? INPUT_PULLUP : INPUT);
}

void idlePins() {                  // safe state for chip swapping
  const uint8_t all[21] = { PIN_G1,
                            PIN_IN[0], PIN_IN[1], PIN_IN[2], PIN_IN[3], PIN_IN[4],
                            PIN_IN[5], PIN_IN[6], PIN_IN[7], PIN_IN[8], PIN_IN[9],
                            PIN_G13,
                            PIN_P14, PIN_P15, PIN_P16, PIN_P18, PIN_P19,
                            PIN_P20, PIN_P21, PIN_P22, PIN_P23 };
  for (uint8_t k = 0; k < 21; k++) pinMode(all[k], INPUT);
}

char vecBuf[44];

// =====================================================================
// Test 1 -- gal22v10_combinational
//   Inputs: a..j = GAL pins 2..11, k = pin 13, m = pin 1 (ALL 12 used).
//   Vector bit order: a=b0 .. j=b9, k=b10, m=b11.
//   pin 23  y23 = a & b
//   pin 21  y21 = a # d,  y21.oe = e & k   (Hi-Z unless e AND k)
//   pin 19  y19 = OR of all twelve inputs  (i.e. v != 0)
//   pin 18  reads !(a&b # c&d)             (active-low output)
//   pin 16  y16 = a $ b $ c
//   pin 15  y15 = y16 & j                  (pin feedback of y16)
//   pin 14  y14 = k & m
// Exhaustive sweep: 4096 vectors, 7 outputs checked each.
// =====================================================================
void runCombinational() {
  Serial.println(F("\nTesting gal22v10_combinational -- 4096 input vectors..."));
  configurePins(1 << 6);                 // pullup on GAL pin 21 (y21) only
  failures = 0; checks = 0;
  unsigned long t0 = millis();

  for (uint16_t v = 0; v < 4096; v++) {
    // f,g,h,i (bits 5..8) are driven by the loop below but appear only in
    // y19 = OR of everything, which is checked as (v != 0) -- no need to
    // decode them individually.
    bool a = v & 1,        b = (v >> 1) & 1,  c = (v >> 2) & 1,
         d = (v >> 3) & 1, e = (v >> 4) & 1,
         j = (v >> 9) & 1, k = (v >> 10) & 1, m = (v >> 11) & 1;
    for (uint8_t bit = 0; bit < 10; bit++)
      digitalWrite(PIN_IN[bit], (v >> bit) & 1);   // a..j
    digitalWrite(PIN_G13, k);
    digitalWrite(PIN_G1,  m);            // proves pin 1 is a plain input here
    delayMicroseconds(SETTLE_US);

    snprintf(vecBuf, sizeof vecBuf, "v=0x%03X (m,k,j..a = b11..b0)", v);
    check(vecBuf, "y23", PIN_P23, a && b);
    // y21 drives a#d only while its .oe term (e & k) is true; otherwise the
    // pin floats and the pullup reads 1. The a=d=0 vectors with e&k=0 are
    // the ones that PROVE Hi-Z (driven value would have been 0).
    check(vecBuf, (e && k) ? "y21" : "y21 (Hi-Z)",
          PIN_P21, (e && k) ? (a || d) : true);
    check(vecBuf, "y19", PIN_P19, v != 0);
    check(vecBuf, "!y18 pin", PIN_P18, !((a && b) || (c && d)));
    check(vecBuf, "y16", PIN_P16, (a != b) != c);
    check(vecBuf, "y15", PIN_P15, ((a != b) != c) && j);
    check(vecBuf, "y14", PIN_P14, k && m);
  }

  summarize("gal22v10_combinational", t0);
  idlePins();
}

// =====================================================================
// Test 2 -- gal22v10_registered
//   pin 1 = clk, pin 2 = rst (AR, async), pin 3 = pre (SP, sync),
//   pin 4 = en (count enable).
//   pin 23/22/21 = q0/q1/q2 (registered):
//     q0.d = q0 $ en    q1.d = q1 $ (q0&en)    q2.d = q2 $ (q1&q0&en)
//   pin 20 = !qb0 (registered active-low), qb0.d = q0  -> the PIN shows
//     NOT(q0 delayed one clock).
//   pin 14 = carry = q0&q1&q2 (combinational, never Hi-Z).
// Phases: AR async proof, enabled count (3 wraps), hold (en=0),
//         SP sync proof, wrap after preset, AR from mid-count.
// =====================================================================
void clockPulse() {
  digitalWrite(PIN_G1, HIGH); delayMicroseconds(SETTLE_US);
  digitalWrite(PIN_G1, LOW);  delayMicroseconds(SETTLE_US);
}

// Shadow model of the register file, mirrored from the .pld equations.
uint8_t regState;    // q2..q0
bool    regQb0;      // qb0's register (pin 20 shows its complement)

void modelEdge(bool en, bool pre) {
  if (pre) { regState = 7; regQb0 = true; return; }   // SP: all registers set
  bool q0Before = regState & 1;
  if (en) regState = (regState + 1) & 7;              // enabled binary count
  regQb0 = q0Before;                                  // qb0 pipelines q0
}

void checkRegState(const char *phase) {
  snprintf(vecBuf, sizeof vecBuf, "%s (expect q=%d%d%d qb0=%d)",
           phase, (regState >> 2) & 1, (regState >> 1) & 1, regState & 1, regQb0);
  check(vecBuf, "q0", PIN_P23, regState & 1);
  check(vecBuf, "q1", PIN_P22, (regState >> 1) & 1);
  check(vecBuf, "q2", PIN_P21, (regState >> 2) & 1);
  check(vecBuf, "!qb0 pin", PIN_P20, !regQb0);
  check(vecBuf, "carry", PIN_P14, regState == 7);
}

void runRegistered() {
  Serial.println(F("\nTesting gal22v10_registered -- count, AR, SP, qb0..."));
  configurePins(0);                      // nothing tri-states in this burn
  failures = 0; checks = 0;
  unsigned long t0 = millis();

  // Phase 1: AR asynchronous proof. From the unknown power-up state,
  // raising rst with the clock held LOW must clear everything at once.
  digitalWrite(PIN_IN[0], HIGH);         // rst = 1, NO clock edge
  delayMicroseconds(SETTLE_US);
  regState = 0; regQb0 = false;
  checkRegState("AR asserted, no clock");
  digitalWrite(PIN_IN[0], LOW);          // release; registers must hold
  delayMicroseconds(SETTLE_US);
  checkRegState("AR released, no clock");

  // Phase 2: enabled count, three full wraps. qb0 trails q0 by one edge.
  digitalWrite(PIN_IN[2], HIGH);         // en = 1
  for (uint8_t step = 1; step <= 24; step++) {
    clockPulse();
    modelEdge(true, false);
    snprintf(vecBuf, sizeof vecBuf, "count step %u", step);
    checkRegState(vecBuf);
  }

  // Phase 3: hold. With en=0 the count freezes but qb0 keeps latching q0.
  digitalWrite(PIN_IN[2], LOW);          // en = 0
  for (uint8_t step = 0; step < 3; step++) {
    clockPulse();
    modelEdge(false, false);
    checkRegState("hold (en=0)");
  }

  // Phase 4: SP synchronous proof. Count to a state with mixed bits,
  // assert pre with NO clock (state must not change -- SP is not async),
  // then one edge presets every register to 1.
  digitalWrite(PIN_IN[2], HIGH);         // en = 1
  for (uint8_t step = 0; step < 3; step++) { clockPulse(); modelEdge(true, false); }
  checkRegState("pre-SP count");
  digitalWrite(PIN_IN[1], HIGH);         // pre = 1, NO clock edge yet
  delayMicroseconds(SETTLE_US);
  checkRegState("SP asserted, no clock");   // unchanged: proves SP is sync
  clockPulse();
  modelEdge(true, true);                 // -> q=111, qb0=1, carry=1, pin20=0
  checkRegState("SP clock edge");
  digitalWrite(PIN_IN[1], LOW);          // pre = 0

  // Phase 5: wrap out of the preset state, then AR from mid-count.
  clockPulse();
  modelEdge(true, false);                // 111 -> 000, qb0 <- 1 (pin 20 = 0)
  checkRegState("wrap after SP");
  for (uint8_t step = 0; step < 2; step++) { clockPulse(); modelEdge(true, false); }
  checkRegState("mid-count");
  digitalWrite(PIN_IN[0], HIGH);         // rst = 1, clock held LOW
  delayMicroseconds(SETTLE_US);
  regState = 0; regQb0 = false;          // AR clears everything, async
  checkRegState("AR from mid-count, no clock");
  digitalWrite(PIN_IN[0], LOW);

  summarize("gal22v10_registered", t0);
  idlePins();
}

// ------------------ menu ---------------------------------------------
void printMenu() {
  Serial.println(F("\n=============================================="));
  Serial.println(F("TTLSim GAL22V10 burn tester"));
  Serial.println(F("Socket the chip FIRST, then choose:"));
  Serial.println(F("  1 = gal22v10_combinational  (4096-vector sweep)"));
  Serial.println(F("  2 = gal22v10_registered     (count / AR / SP / qb0)"));
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
  if (ch == '1') runCombinational();
  else if (ch == '2') runRegistered();
  else if (ch > ' ') Serial.println(F("Type 1 or 2."));
  if (ch > ' ') printMenu();
}
