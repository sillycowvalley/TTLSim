// ============================================================================
//  Blinky-M Virtual Front Panel  -  Arduino firmware
//  On each CLKG edge, sample 32 pins and print them as one 32-bit hex word.
//
//  Target : Arduino Mega 2560 (or compatible), header pins 22-53, all INPUT.
//  Output : one line per capture - 8 uppercase hex chars + newline.
//           A capture fires on the active CLKG edge, OR on either edge of
//           /RESET. /RESET stops the clock, so its edges give you the machine
//           entering reset and the pristine post-reset state before CLKG
//           resumes - which a CLKG-only trigger would miss. bit 25 tells you
//           whether reset is asserted, so the lines need no marker.
//           bit 0 = A0. Levels are raw (1 = high); active-low inversion is a
//           host-side display concern. (CLKG is the trigger, so it samples high
//           every capture - it is bit 24 but not a toggling indicator.)
//
//  Bit order (LSB first):
//     bits  0-15 : A0..A15   (even pins 22..52)
//     bits 16-23 : D0..D7    (odd  pins 23..37)
//     bits 24-31 : CLKG /RESET /FETCH T0 T1 T2 HALT N  (odd pins 39..53)
// ============================================================================

struct PanelPin {
  uint8_t megaPin;
  bool    pullup;    // true -> INPUT_PULLUP (defined idle on a tri-state line)
};

// Only the D-bus lines get a pull-up (shared tri-state rail floats when idle);
// the A bus and control lines are always driven, so plain INPUT.
static const PanelPin panelPins[32] = {
  // bits 0-15 : A0..A15
  {22, false}, {24, false}, {26, false}, {28, false},
  {30, false}, {32, false}, {34, false}, {36, false},
  {38, false}, {40, false}, {42, false}, {44, false},
  {46, false}, {48, false}, {50, false}, {52, false},
  // bits 16-23 : D0..D7
  {23, true},  {25, true},  {27, true},  {29, true},
  {31, true},  {33, true},  {35, true},  {37, true},
  // bits 24-31 : CLKG /RESET /FETCH T0 T1 T2 HALT N
  {39, false}, {41, false}, {43, false}, {45, false},
  {47, false}, {49, false}, {51, false}, {53, false},
};

static const uint8_t clkgPin           = 39;      // capture strobe (bit 24)
static const uint8_t resetPin          = 41;      // /RESET (bit 25) - also triggers a capture on both edges
static const bool    captureEdgeRising = true;    // false -> capture on falling CLKG
static const long    serialBaud        = 115200L; // native-USB boards ignore this

static int prevClkg  = LOW;
static int prevReset = LOW;

uint32_t samplePins() {
  uint32_t word = 0;
  for (uint8_t i = 0; i < 32; i++) {
    if (digitalRead(panelPins[i].megaPin) == HIGH) {
      word |= ((uint32_t)1 << i);
    }
  }
  return word;
}

void printFrame(uint32_t word) {
  static const char hexDigits[] = "0123456789ABCDEF";
  char buf[9];
  for (int8_t nib = 7; nib >= 0; nib--) {
    buf[7 - nib] = hexDigits[(word >> (nib * 4)) & 0x0F];
  }
  buf[8] = '\0';
  Serial.println(buf);
}

void setup() {
  Serial.begin(serialBaud);
  for (uint8_t i = 0; i < 32; i++) {
    pinMode(panelPins[i].megaPin, panelPins[i].pullup ? INPUT_PULLUP : INPUT);
  }
  prevClkg  = digitalRead(clkgPin);
  prevReset = digitalRead(resetPin);
}

void loop() {
  int clkg  = digitalRead(clkgPin);
  int reset = digitalRead(resetPin);

  bool clkgEdge  = (clkg != prevClkg) && (clkg == (captureEdgeRising ? HIGH : LOW));
  bool resetEdge = (reset != prevReset);              // both edges of /RESET

  if (clkgEdge || resetEdge) {
    printFrame(samplePins());
  }

  prevClkg  = clkg;
  prevReset = reset;
}
