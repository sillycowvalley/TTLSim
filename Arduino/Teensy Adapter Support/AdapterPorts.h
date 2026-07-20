// ============================================================================
//  AdapterPorts.h  -  virtual 8-bit ports, THREE platforms
//
//  Targets:
//    Mega 2560       (__AVR_ATmega2560__, automatic)  - direct wiring into
//                     the adapter's header positions. Verification platform
//                     of record. See MegaDirect_Reference.md.
//    Teensy 4.1      (__IMXRT1062__, automatic) - which BOARD is selected by
//                     the define below:
//        (default)          the original TXS level-shifter adapter.
//                           Chaser-verified mapping - never alter it.
//        LVC_HARNESS_BOARD  the purpose-built HCT245/LVC245 board: 40 data
//                           lines (ports A-E), per-port DIR control, four
//                           buttons. See LVCBoard_Reference.md.
//
//  Common to all platforms: ports A-D as PIN_PA0..PIN_PD7, port tables,
//  setPortMode, unrolled fast accessors, samplePorts(), SW1/SW2 helpers,
//  SW_PIN_MODE, PLATFORM_NAME. Ribbon colours: bit 0 = white .. bit 7 = red.
//  The LVC board adds Port E (strobes, PE0 = CLK), SW3/SW4, and the
//  portDrive/portRelease direction API.
// ============================================================================

#pragma once
#include <Arduino.h>

// ---- Teensy board select: uncomment when building for the LVC board. ----
// (The Mega ignores this; both Teensy boards compile as __IMXRT1062__ so the
//  compiler cannot tell them apart on its own.)
//#define LVC_HARNESS_BOARD

// Naming: PIN_PA0..PIN_PE7 ("P" for port). PIN_A0..PIN_A9 cannot be used -
// the cores define those as macros for the analog pins, which breaks any
// declaration reusing the names.

#if defined(__IMXRT1062__) && defined(LVC_HARNESS_BOARD)
// ==================================================== Teensy 4.1 + LVC ===
// Purpose-built board: 74HCT245 (drive, 5V) + 74LVC245 (read, 3.3V) per
// data port, /OE-steered; directed, continuously driven. Port E: HCT only.
// Near-contiguous assignment (every through-hole pin 0..41 is plain GPIO);
// pins 13 and 32 are swapped between ports B and E so the onboard LED
// (pin 13) sits on output-only PE0/CLK instead of bidirectional PB5.

#define PLATFORM_NAME "Teensy 4.1 + LVC board"

// ---------------------------------------------------------------- Port A pins
constexpr uint8_t PIN_PA0 = 0;   // white
constexpr uint8_t PIN_PA1 = 1;   // grey
constexpr uint8_t PIN_PA2 = 2;   // violet
constexpr uint8_t PIN_PA3 = 3;   // blue
constexpr uint8_t PIN_PA4 = 4;   // green
constexpr uint8_t PIN_PA5 = 5;   // yellow
constexpr uint8_t PIN_PA6 = 6;   // orange
constexpr uint8_t PIN_PA7 = 7;   // red

// ---------------------------------------------------------------- Port B pins
constexpr uint8_t PIN_PB0 = 8;   // white
constexpr uint8_t PIN_PB1 = 9;   // grey
constexpr uint8_t PIN_PB2 = 10;  // violet
constexpr uint8_t PIN_PB3 = 11;  // blue
constexpr uint8_t PIN_PB4 = 12;  // green
constexpr uint8_t PIN_PB5 = 32;  // yellow
constexpr uint8_t PIN_PB6 = 14;  // orange
constexpr uint8_t PIN_PB7 = 15;  // red

// ---------------------------------------------------------------- Port C pins
constexpr uint8_t PIN_PC0 = 16;  // white
constexpr uint8_t PIN_PC1 = 17;  // grey
constexpr uint8_t PIN_PC2 = 18;  // violet
constexpr uint8_t PIN_PC3 = 19;  // blue
constexpr uint8_t PIN_PC4 = 20;  // green
constexpr uint8_t PIN_PC5 = 21;  // yellow
constexpr uint8_t PIN_PC6 = 22;  // orange
constexpr uint8_t PIN_PC7 = 23;  // red

// ---------------------------------------------------------------- Port D pins
constexpr uint8_t PIN_PD0 = 24;  // white
constexpr uint8_t PIN_PD1 = 25;  // grey
constexpr uint8_t PIN_PD2 = 26;  // violet
constexpr uint8_t PIN_PD3 = 27;  // blue
constexpr uint8_t PIN_PD4 = 28;  // green
constexpr uint8_t PIN_PD5 = 29;  // yellow
constexpr uint8_t PIN_PD6 = 30;  // orange
constexpr uint8_t PIN_PD7 = 31;  // red

// ---------------------------------------------------------------- Port E pins
// Dedicated strobe port (H5-H12: signal+GND pair headers). PE0 = CLK by
// convention. Direction is fixed: the shifter is strapped to drive the DUT.
constexpr uint8_t PIN_PE0 = 13;  // white  (CLK - onboard LED = clock activity light)
constexpr uint8_t PIN_PE1 = 33;  // grey
constexpr uint8_t PIN_PE2 = 34;  // violet
constexpr uint8_t PIN_PE3 = 35;  // blue
constexpr uint8_t PIN_PE4 = 36;  // green
constexpr uint8_t PIN_PE5 = 37;  // yellow
constexpr uint8_t PIN_PE6 = 38;  // orange
constexpr uint8_t PIN_PE7 = 39;  // red

// ------------------------------------------------------- direction control
// One DIR line per data port (bottom pads), 10 k pulldowns on the board.
// DIR steers the pair's /OE lines (via the shared 'HCT04 inverter):
// HIGH = HCT enabled = Teensy drives the DUT; LOW = LVC enabled = board
// reads. The pulldowns mean every data port powers up READING - the board
// cannot fight a DUT before software runs.
constexpr uint8_t PIN_DIR_A = 42;
constexpr uint8_t PIN_DIR_B = 43;
constexpr uint8_t PIN_DIR_C = 44;
constexpr uint8_t PIN_DIR_D = 45;

// ------------------------------------------------------------------- Buttons
// Four momentary buttons to GND, no board pull-ups - internal pull-ups do
// the work. SW1/SW2 keep the harness-wide roles (start/advance, abort/exit);
// SW3/SW4 are spare for per-project functions.
constexpr uint8_t PIN_SW1 = 40;
constexpr uint8_t PIN_SW2 = 41;
constexpr uint8_t PIN_SW3 = 46;
constexpr uint8_t PIN_SW4 = 47;
constexpr uint8_t SW_PIN_MODE = INPUT_PULLUP;

#elif defined(__IMXRT1062__)
// ============================================================ Teensy 4.1 ===
// Adapter mapping, verified end-to-end by LED chaser. Do not alter.

#define PLATFORM_NAME "Teensy 4.1 + adapter"

// ---------------------------------------------------------------- Port A pins
// Inner column, lower group (ribbon colour in comment)
constexpr uint8_t PIN_PA0 = 15;  // white
constexpr uint8_t PIN_PA1 = 23;  // grey
constexpr uint8_t PIN_PA2 = 10;  // violet
constexpr uint8_t PIN_PA3 = 11;  // blue
constexpr uint8_t PIN_PA4 = 27;  // green
constexpr uint8_t PIN_PA5 =  4;  // yellow
constexpr uint8_t PIN_PA6 = 38;  // orange
constexpr uint8_t PIN_PA7 = 36;  // red

// ---------------------------------------------------------------- Port B pins
// Inner column, upper group
constexpr uint8_t PIN_PB0 = 24;  // white
constexpr uint8_t PIN_PB1 = 16;  // grey
constexpr uint8_t PIN_PB2 =  5;  // violet
constexpr uint8_t PIN_PB3 = 20;  // blue
constexpr uint8_t PIN_PB4 =  8;  // green
constexpr uint8_t PIN_PB5 = 14;  // yellow
constexpr uint8_t PIN_PB6 = 30;  // orange
constexpr uint8_t PIN_PB7 = 39;  // red

// ---------------------------------------------------------------- Port C pins
// Edge column, lower group
constexpr uint8_t PIN_PC0 = 22;  // white
constexpr uint8_t PIN_PC1 =  9;  // grey
constexpr uint8_t PIN_PC2 = 25;  // violet
constexpr uint8_t PIN_PC3 = 12;  // blue
constexpr uint8_t PIN_PC4 = 26;  // green
constexpr uint8_t PIN_PC5 =  3;  // yellow
constexpr uint8_t PIN_PC6 = 37;  // orange
constexpr uint8_t PIN_PC7 = 35;  // red

// ---------------------------------------------------------------- Port D pins
// Edge column, upper group
constexpr uint8_t PIN_PD0 = 17;  // white
constexpr uint8_t PIN_PD1 = 13;  // grey   (Teensy onboard LED - fine as input)
constexpr uint8_t PIN_PD2 = 21;  // violet
constexpr uint8_t PIN_PD3 =  6;  // blue
constexpr uint8_t PIN_PD4 =  7;  // green
constexpr uint8_t PIN_PD5 =  2;  // yellow
constexpr uint8_t PIN_PD6 = 29;  // orange
constexpr uint8_t PIN_PD7 = 28;  // red

// ------------------------------------------------------------------- Buttons
// SW1 (right) and SW2 (left) on the adapter, active-low, 4.7k pull-ups to
// 3.3 V on the board - so plain INPUT.
constexpr uint8_t PIN_SW1 = 40;
constexpr uint8_t PIN_SW2 = 41;
constexpr uint8_t SW_PIN_MODE = INPUT;

#elif defined(__AVR_ATmega2560__)
// ============================================================== Mega 2560 ===
// Direct wiring: the Mega's own header pins 22..53 in the same positions the
// adapter presents. 5 V push-pull drive, no TXS, no series terminations.

#define PLATFORM_NAME "Arduino Mega 2560 (direct)"

// Compatibility shims: the harness uses the Teensy fast API and
// delayNanoseconds directly. On AVR these map to the plain core calls;
// delayNanoseconds rounds UP to whole microseconds (so chase delays on the
// Mega quantize to 1 us steps).
#define digitalWriteFast(pin, val) digitalWrite((pin), (val))
#define digitalReadFast(pin)       digitalRead((pin))
inline void delayNanoseconds(uint32_t ns) {
  if (ns == 0) return;
  delayMicroseconds((unsigned int)((ns + 999UL) / 1000UL));
}

// ---------------------------------------------------------------- Port A pins
// Inner column, lower group = header evens 22..36 (ribbon colour in comment)
constexpr uint8_t PIN_PA0 = 22;  // white
constexpr uint8_t PIN_PA1 = 24;  // grey
constexpr uint8_t PIN_PA2 = 26;  // violet
constexpr uint8_t PIN_PA3 = 28;  // blue
constexpr uint8_t PIN_PA4 = 30;  // green
constexpr uint8_t PIN_PA5 = 32;  // yellow
constexpr uint8_t PIN_PA6 = 34;  // orange
constexpr uint8_t PIN_PA7 = 36;  // red

// ---------------------------------------------------------------- Port B pins
// Inner column, upper group = header evens 38..52
constexpr uint8_t PIN_PB0 = 38;  // white
constexpr uint8_t PIN_PB1 = 40;  // grey
constexpr uint8_t PIN_PB2 = 42;  // violet
constexpr uint8_t PIN_PB3 = 44;  // blue
constexpr uint8_t PIN_PB4 = 46;  // green
constexpr uint8_t PIN_PB5 = 48;  // yellow
constexpr uint8_t PIN_PB6 = 50;  // orange
constexpr uint8_t PIN_PB7 = 52;  // red

// ---------------------------------------------------------------- Port C pins
// Edge column, lower group = header odds 23..37
constexpr uint8_t PIN_PC0 = 23;  // white
constexpr uint8_t PIN_PC1 = 25;  // grey
constexpr uint8_t PIN_PC2 = 27;  // violet
constexpr uint8_t PIN_PC3 = 29;  // blue
constexpr uint8_t PIN_PC4 = 31;  // green
constexpr uint8_t PIN_PC5 = 33;  // yellow
constexpr uint8_t PIN_PC6 = 35;  // orange
constexpr uint8_t PIN_PC7 = 37;  // red

// ---------------------------------------------------------------- Port D pins
// Edge column, upper group = header odds 39..53
constexpr uint8_t PIN_PD0 = 39;  // white
constexpr uint8_t PIN_PD1 = 41;  // grey
constexpr uint8_t PIN_PD2 = 43;  // violet
constexpr uint8_t PIN_PD3 = 45;  // blue
constexpr uint8_t PIN_PD4 = 47;  // green
constexpr uint8_t PIN_PD5 = 49;  // yellow
constexpr uint8_t PIN_PD6 = 51;  // orange
constexpr uint8_t PIN_PD7 = 53;  // red

// ------------------------------------------------------------------- Buttons
// No adapter, no buttons: two spare pins with the internal pull-ups so that
// unwired = idle-high = not pressed. Wire momentary switches to GND on pins
// 2 (SW1) and 3 (SW2) if wanted; every button function has a serial command.
constexpr uint8_t PIN_SW1 = 2;
constexpr uint8_t PIN_SW2 = 3;
constexpr uint8_t SW_PIN_MODE = INPUT_PULLUP;

#else
#error "AdapterPorts.h: unsupported board - build for Teensy 4.1 or Arduino Mega 2560."
#endif

// ======================================================= shared definitions
// Everything below is platform-independent: on the Teensy the fast calls are
// the real digitalReadFast/digitalWriteFast; on the Mega they are the shims.

// -------------------------------------------------------- pin tables (setup)
// Bit order: index 0 = bit 0. Use these for pinMode loops and diagnostics.
constexpr uint8_t PORT_A_PINS[8] = { PIN_PA0, PIN_PA1, PIN_PA2, PIN_PA3, PIN_PA4, PIN_PA5, PIN_PA6, PIN_PA7 };
constexpr uint8_t PORT_B_PINS[8] = { PIN_PB0, PIN_PB1, PIN_PB2, PIN_PB3, PIN_PB4, PIN_PB5, PIN_PB6, PIN_PB7 };
constexpr uint8_t PORT_C_PINS[8] = { PIN_PC0, PIN_PC1, PIN_PC2, PIN_PC3, PIN_PC4, PIN_PC5, PIN_PC6, PIN_PC7 };
constexpr uint8_t PORT_D_PINS[8] = { PIN_PD0, PIN_PD1, PIN_PD2, PIN_PD3, PIN_PD4, PIN_PD5, PIN_PD6, PIN_PD7 };

// Set the direction of a whole port: INPUT, INPUT_PULLUP or OUTPUT.
inline void setPortMode(const uint8_t (&pins)[8], uint8_t mode) {
  for (uint8_t i = 0; i < 8; i++) {
    pinMode(pins[i], mode);
  }
}

// --------------------------------------------------------------- fast readers
// Unrolled with named constants so every digitalReadFast folds to one
// register test on the Teensy. Do NOT convert these to loops over the pin
// tables - a variable pin defeats digitalReadFast.

inline uint8_t readPortA() {
  uint8_t v = 0;
  if (digitalReadFast(PIN_PA0)) v |= 0x01;
  if (digitalReadFast(PIN_PA1)) v |= 0x02;
  if (digitalReadFast(PIN_PA2)) v |= 0x04;
  if (digitalReadFast(PIN_PA3)) v |= 0x08;
  if (digitalReadFast(PIN_PA4)) v |= 0x10;
  if (digitalReadFast(PIN_PA5)) v |= 0x20;
  if (digitalReadFast(PIN_PA6)) v |= 0x40;
  if (digitalReadFast(PIN_PA7)) v |= 0x80;
  return v;
}

inline uint8_t readPortB() {
  uint8_t v = 0;
  if (digitalReadFast(PIN_PB0)) v |= 0x01;
  if (digitalReadFast(PIN_PB1)) v |= 0x02;
  if (digitalReadFast(PIN_PB2)) v |= 0x04;
  if (digitalReadFast(PIN_PB3)) v |= 0x08;
  if (digitalReadFast(PIN_PB4)) v |= 0x10;
  if (digitalReadFast(PIN_PB5)) v |= 0x20;
  if (digitalReadFast(PIN_PB6)) v |= 0x40;
  if (digitalReadFast(PIN_PB7)) v |= 0x80;
  return v;
}

inline uint8_t readPortC() {
  uint8_t v = 0;
  if (digitalReadFast(PIN_PC0)) v |= 0x01;
  if (digitalReadFast(PIN_PC1)) v |= 0x02;
  if (digitalReadFast(PIN_PC2)) v |= 0x04;
  if (digitalReadFast(PIN_PC3)) v |= 0x08;
  if (digitalReadFast(PIN_PC4)) v |= 0x10;
  if (digitalReadFast(PIN_PC5)) v |= 0x20;
  if (digitalReadFast(PIN_PC6)) v |= 0x40;
  if (digitalReadFast(PIN_PC7)) v |= 0x80;
  return v;
}

inline uint8_t readPortD() {
  uint8_t v = 0;
  if (digitalReadFast(PIN_PD0)) v |= 0x01;
  if (digitalReadFast(PIN_PD1)) v |= 0x02;
  if (digitalReadFast(PIN_PD2)) v |= 0x04;
  if (digitalReadFast(PIN_PD3)) v |= 0x08;
  if (digitalReadFast(PIN_PD4)) v |= 0x10;
  if (digitalReadFast(PIN_PD5)) v |= 0x20;
  if (digitalReadFast(PIN_PD6)) v |= 0x40;
  if (digitalReadFast(PIN_PD7)) v |= 0x80;
  return v;
}

// --------------------------------------------------------------- fast writers
// Same rule: keep the pins constant, keep them unrolled.

inline void writePortA(uint8_t v) {
  digitalWriteFast(PIN_PA0, v & 0x01);
  digitalWriteFast(PIN_PA1, v & 0x02);
  digitalWriteFast(PIN_PA2, v & 0x04);
  digitalWriteFast(PIN_PA3, v & 0x08);
  digitalWriteFast(PIN_PA4, v & 0x10);
  digitalWriteFast(PIN_PA5, v & 0x20);
  digitalWriteFast(PIN_PA6, v & 0x40);
  digitalWriteFast(PIN_PA7, v & 0x80);
}

inline void writePortB(uint8_t v) {
  digitalWriteFast(PIN_PB0, v & 0x01);
  digitalWriteFast(PIN_PB1, v & 0x02);
  digitalWriteFast(PIN_PB2, v & 0x04);
  digitalWriteFast(PIN_PB3, v & 0x08);
  digitalWriteFast(PIN_PB4, v & 0x10);
  digitalWriteFast(PIN_PB5, v & 0x20);
  digitalWriteFast(PIN_PB6, v & 0x40);
  digitalWriteFast(PIN_PB7, v & 0x80);
}

inline void writePortC(uint8_t v) {
  digitalWriteFast(PIN_PC0, v & 0x01);
  digitalWriteFast(PIN_PC1, v & 0x02);
  digitalWriteFast(PIN_PC2, v & 0x04);
  digitalWriteFast(PIN_PC3, v & 0x08);
  digitalWriteFast(PIN_PC4, v & 0x10);
  digitalWriteFast(PIN_PC5, v & 0x20);
  digitalWriteFast(PIN_PC6, v & 0x40);
  digitalWriteFast(PIN_PC7, v & 0x80);
}

inline void writePortD(uint8_t v) {
  digitalWriteFast(PIN_PD0, v & 0x01);
  digitalWriteFast(PIN_PD1, v & 0x02);
  digitalWriteFast(PIN_PD2, v & 0x04);
  digitalWriteFast(PIN_PD3, v & 0x08);
  digitalWriteFast(PIN_PD4, v & 0x10);
  digitalWriteFast(PIN_PD5, v & 0x20);
  digitalWriteFast(PIN_PD6, v & 0x40);
  digitalWriteFast(PIN_PD7, v & 0x80);
}

// -------------------------------------------------------------- convenience
// All four ports in one 32-bit word: A = bits 0-7, B = 8-15, C = 16-23, D = 24-31.
inline uint32_t samplePorts() {
  return  (uint32_t)readPortA()
       | ((uint32_t)readPortB() << 8)
       | ((uint32_t)readPortC() << 16)
       | ((uint32_t)readPortD() << 24);
}

// Buttons are active-low: true = pressed.
inline bool sw1Pressed() { return digitalReadFast(PIN_SW1) == LOW; }
inline bool sw2Pressed() { return digitalReadFast(PIN_SW2) == LOW; }

#if defined(__IMXRT1062__) && defined(LVC_HARNESS_BOARD)
// ================================================ LVC-board additions ===
// Port E, the extra buttons, and the per-port direction API. Only compiled
// for the LVC board, so accidental use on the other platforms is a
// compile-time error rather than a silent misbehaviour.

constexpr uint8_t PORT_E_PINS[8] = { PIN_PE0, PIN_PE1, PIN_PE2, PIN_PE3, PIN_PE4, PIN_PE5, PIN_PE6, PIN_PE7 };

inline uint8_t readPortE() {
  uint8_t v = 0;
  if (digitalReadFast(PIN_PE0)) v |= 0x01;
  if (digitalReadFast(PIN_PE1)) v |= 0x02;
  if (digitalReadFast(PIN_PE2)) v |= 0x04;
  if (digitalReadFast(PIN_PE3)) v |= 0x08;
  if (digitalReadFast(PIN_PE4)) v |= 0x10;
  if (digitalReadFast(PIN_PE5)) v |= 0x20;
  if (digitalReadFast(PIN_PE6)) v |= 0x40;
  if (digitalReadFast(PIN_PE7)) v |= 0x80;
  return v;
}

inline void writePortE(uint8_t v) {
  digitalWriteFast(PIN_PE0, v & 0x01);
  digitalWriteFast(PIN_PE1, v & 0x02);
  digitalWriteFast(PIN_PE2, v & 0x04);
  digitalWriteFast(PIN_PE3, v & 0x08);
  digitalWriteFast(PIN_PE4, v & 0x10);
  digitalWriteFast(PIN_PE5, v & 0x20);
  digitalWriteFast(PIN_PE6, v & 0x40);
  digitalWriteFast(PIN_PE7, v & 0x80);
}

inline bool sw3Pressed() { return digitalReadFast(PIN_SW3) == LOW; }
inline bool sw4Pressed() { return digitalReadFast(PIN_SW4) == LOW; }

// ------------------------------------------------------- direction control
// Call initPortDirections() first thing in setup(): claims the DIR pins
// (all low = every data port reading, matching the board's pulldown
// power-up state) and sets Port E driving (its shifter is strapped that
// way; the Teensy pins just need to be outputs).
//
// portDrive('A'..'D')   - take the bus: DIR high, then Teensy pins OUTPUT.
// portRelease('A'..'D') - give it back: Teensy pins INPUT, then DIR low.
//
// During portDrive the DUT-side bus is briefly undefined (~us) between the
// DIR flip and the pins driving - the same window any real bus turnaround
// has. Fit the B-side SIP pulls on ports that talk to tri-state DUT buses
// so the level is defined during it.

inline uint8_t dirPinFor(char port) {
  switch (port) {
    case 'A': return PIN_DIR_A;
    case 'B': return PIN_DIR_B;
    case 'C': return PIN_DIR_C;
    default:  return PIN_DIR_D;
  }
}

inline void initPortDirections() {
  pinMode(PIN_DIR_A, OUTPUT); digitalWriteFast(PIN_DIR_A, LOW);
  pinMode(PIN_DIR_B, OUTPUT); digitalWriteFast(PIN_DIR_B, LOW);
  pinMode(PIN_DIR_C, OUTPUT); digitalWriteFast(PIN_DIR_C, LOW);
  pinMode(PIN_DIR_D, OUTPUT); digitalWriteFast(PIN_DIR_D, LOW);
  setPortMode(PORT_A_PINS, INPUT);
  setPortMode(PORT_B_PINS, INPUT);
  setPortMode(PORT_C_PINS, INPUT);
  setPortMode(PORT_D_PINS, INPUT);
  setPortMode(PORT_E_PINS, OUTPUT);   // strobes: always harness-driven
  writePortE(0x00);
}

inline void portDrive(char port) {
  digitalWriteFast(dirPinFor(port), HIGH);
  switch (port) {
    case 'A': setPortMode(PORT_A_PINS, OUTPUT); break;
    case 'B': setPortMode(PORT_B_PINS, OUTPUT); break;
    case 'C': setPortMode(PORT_C_PINS, OUTPUT); break;
    default:  setPortMode(PORT_D_PINS, OUTPUT); break;
  }
}

inline void portRelease(char port) {
  switch (port) {
    case 'A': setPortMode(PORT_A_PINS, INPUT); break;
    case 'B': setPortMode(PORT_B_PINS, INPUT); break;
    case 'C': setPortMode(PORT_C_PINS, INPUT); break;
    default:  setPortMode(PORT_D_PINS, INPUT); break;
  }
  digitalWriteFast(dirPinFor(port), LOW);
}
#endif  // LVC_HARNESS_BOARD
