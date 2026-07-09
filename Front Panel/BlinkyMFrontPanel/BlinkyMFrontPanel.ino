// ============================================================================
//  Blinky-M Virtual Front Panel  -  probe firmware (dual target)
//  On each CLKG edge (or either /RESET edge), sample 32 pins and print them
//  as one 32-bit hex word.
//
//  TARGET SELECT - exactly one of:
//     #define BOARD_MEGA      Arduino Mega 2560: header pins 22-53 wired per
//                             the hookup diagram.
//     #define BOARD_TEENSY41  Teensy 4.1 on the 8bitforce RetroShield adapter
//                             (Mega form factor): same header positions, but
//                             each position routes to a different Teensy pin
//                             (adapter mapping doc, 2020-08-06).
//
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
//     bits  0-15 : A0..A15   (header evens 22..52)
//     bits 16-23 : D0..D7    (header odds  23..37)
//     bits 24-31 : CLKG /RESET /FETCH T0 T1 T2 HALT N  (header odds 39..53)
// ============================================================================

#define BOARD_TEENSY41
//#define BOARD_MEGA

#if defined(BOARD_MEGA) && defined(BOARD_TEENSY41)
#error "Define only one of BOARD_MEGA / BOARD_TEENSY41."
#endif
#if !defined(BOARD_MEGA) && !defined(BOARD_TEENSY41)
#error "Define one of BOARD_MEGA / BOARD_TEENSY41."
#endif

// ---------------------------------------------------------------- pin names
// One named constant per signal; the value is the board's pin for the header
// position named in the diagram. Change targets by flipping the #define only.

#if defined(BOARD_MEGA)

// Address bus (header evens 22..52)
static const uint8_t TAP_A0  = 22,  TAP_A1  = 24,  TAP_A2  = 26,  TAP_A3  = 28;
static const uint8_t TAP_A4  = 30,  TAP_A5  = 32,  TAP_A6  = 34,  TAP_A7  = 36;
static const uint8_t TAP_A8  = 38,  TAP_A9  = 40,  TAP_A10 = 42,  TAP_A11 = 44;
static const uint8_t TAP_A12 = 46,  TAP_A13 = 48,  TAP_A14 = 50,  TAP_A15 = 52;
// Data bus (header odds 23..37)
static const uint8_t TAP_D0  = 23,  TAP_D1  = 25,  TAP_D2  = 27,  TAP_D3  = 29;
static const uint8_t TAP_D4  = 31,  TAP_D5  = 33,  TAP_D6  = 35,  TAP_D7  = 37;
// Control (header odds 39..53)
static const uint8_t TAP_CLKG   = 39,  TAP_NRESET = 41,  TAP_NFETCH = 43;
static const uint8_t TAP_T0     = 45,  TAP_T1     = 47,  TAP_T2     = 49;
static const uint8_t TAP_HALT   = 51,  TAP_N      = 53;

#elif defined(BOARD_TEENSY41)

// Address bus (header evens 22..52 -> Teensy pins via RetroShield adapter)
static const uint8_t TAP_A0  = 15,  TAP_A1  = 23,  TAP_A2  = 10,  TAP_A3  = 11;
static const uint8_t TAP_A4  = 27,  TAP_A5  =  4,  TAP_A6  = 38,  TAP_A7  = 36;
static const uint8_t TAP_A8  = 24,  TAP_A9  = 16,  TAP_A10 =  5,  TAP_A11 = 20;
static const uint8_t TAP_A12 =  8,  TAP_A13 = 14,  TAP_A14 = 30,  TAP_A15 = 39;
// Data bus (header odds 23..37)
static const uint8_t TAP_D0  = 22,  TAP_D1  =  9,  TAP_D2  = 25,  TAP_D3  = 12;
static const uint8_t TAP_D4  = 26,  TAP_D5  =  3,  TAP_D6  = 37,  TAP_D7  = 35;
// Control (header odds 39..53); Teensy 13 = onboard LED, harmless as input
static const uint8_t TAP_CLKG   = 17,  TAP_NRESET = 13,  TAP_NFETCH = 21;
static const uint8_t TAP_T0     =  6,  TAP_T1     =  7,  TAP_T2     =  2;
static const uint8_t TAP_HALT   = 29,  TAP_N      = 28;

#endif

// ------------------------------------------------------------------ sampling
struct PanelPin {
  uint8_t pin;
  bool    pullup;    // true -> INPUT_PULLUP (defined idle on a tri-state line)
};

// 32 taps in bit order. Only the D-bus lines get a pull-up (shared tri-state
// rail floats when idle); the A bus and control lines are always driven.
static const PanelPin panelPins[32] = {
  // bits 0-15 : A0..A15
  {TAP_A0,  false}, {TAP_A1,  false}, {TAP_A2,  false}, {TAP_A3,  false},
  {TAP_A4,  false}, {TAP_A5,  false}, {TAP_A6,  false}, {TAP_A7,  false},
  {TAP_A8,  false}, {TAP_A9,  false}, {TAP_A10, false}, {TAP_A11, false},
  {TAP_A12, false}, {TAP_A13, false}, {TAP_A14, false}, {TAP_A15, false},
  // bits 16-23 : D0..D7
  {TAP_D0, true},  {TAP_D1, true},  {TAP_D2, true},  {TAP_D3, true},
  {TAP_D4, true},  {TAP_D5, true},  {TAP_D6, true},  {TAP_D7, true},
  // bits 24-31 : CLKG /RESET /FETCH T0 T1 T2 HALT N
  {TAP_CLKG,   false}, {TAP_NRESET, false}, {TAP_NFETCH, false}, {TAP_T0, false},
  {TAP_T1,     false}, {TAP_T2,     false}, {TAP_HALT,   false}, {TAP_N,  false},
};

static const bool captureEdgeRising = true;    // false -> capture on falling CLKG
static const long serialBaud        = 115200L; // native-USB boards ignore this

static int prevClkg  = LOW;
static int prevReset = LOW;

uint32_t samplePins() {
  uint32_t word = 0;
  for (uint8_t i = 0; i < 32; i++) {
    if (digitalRead(panelPins[i].pin) == HIGH) {
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
    pinMode(panelPins[i].pin, panelPins[i].pullup ? INPUT_PULLUP : INPUT);
  }
  prevClkg  = digitalRead(TAP_CLKG);
  prevReset = digitalRead(TAP_NRESET);
}

void loop() {
  int clkg  = digitalRead(TAP_CLKG);
  int reset = digitalRead(TAP_NRESET);

  bool clkgEdge  = (clkg != prevClkg) && (clkg == (captureEdgeRising ? HIGH : LOW));
  bool resetEdge = (reset != prevReset);              // both edges of /RESET

  if (clkgEdge || resetEdge) {
    printFrame(samplePins());
  }

  prevClkg  = clkg;
  prevReset = reset;
}
