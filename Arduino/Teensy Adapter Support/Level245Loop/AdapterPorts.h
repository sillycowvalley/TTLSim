// ============================================================================
//  AdapterPorts.h  -  virtual 8-bit ports A..E, dual platform
//
//  Targets (auto-selected by the compiler, no #define needed):
//    Teensy 4.1  (__IMXRT1062__)      - the LVC harness board: 74HCT245
//                                       (drive, 5 V) + 74LVC245 (read,
//                                       3.3 V) per port, /OE-steered.
//                                       See LVCBoard_Reference.md.
//    Mega 2560   (__AVR_ATmega2560__) - wired directly to a DUT, native
//                                       5 V. Independent verification
//                                       platform. See MegaDirect_Reference.md.
//
//  Common API: PIN_PA0..PIN_PE7, port tables, setPortMode, unrolled fast
//  accessors, samplePorts() (A-D), readPortE/writePortE, SW0/SW1 buttons,
//  SW_PIN_MODE, PLATFORM_NAME. Ribbon colours: bit 0 = white .. bit 7 = red.
//
//  Direction control (Teensy/LVC board only):
//    Ports A, B, E - direction set by board jumpers J8 / J12 / J15
//                    (fitted = port drives the DUT, parked = port reads).
//    Ports C, D    - runtime direction on PIN_DIR0 / PIN_DIR1
//                    (high = drive, low = read; 10 k pulldowns = read at
//                    power-up). portDrive('C'|'D') / portRelease('C'|'D').
//    The sketch must match the jumpers - print the assumed configuration
//    in the boot banner. On the Mega there are no shifters: direction is
//    simply pinMode, and portDrive/portRelease are not defined.
//
//  Keep pins as compile-time constants and the accessors unrolled: on the
//  Teensy, digitalReadFast/digitalWriteFast then compile to single GPIO
//  register operations. Never convert the accessors to loops.
// ============================================================================

#pragma once
#include <Arduino.h>

// Naming: PIN_PA0..PIN_PE7 ("P" for port). PIN_A0..PIN_A9 cannot be used -
// the cores define those as macros for the analog pins, which breaks any
// declaration reusing the names.

#if defined(__IMXRT1062__)
// ================================================== Teensy 4.1, LVC board ===

#define PLATFORM_NAME "Teensy 4.1 + LVC harness board"

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
// Strobe/sense port, Teensy 34-41 consecutive. PE0 = CLK by convention.
// Direction is set by jumper J15 for the whole port (fitted = drive).
// PE6/PE7 are shared with the buttons via J6/J7 (see Buttons below).
constexpr uint8_t PIN_PE0 = 13;  // white  (CLK)
constexpr uint8_t PIN_PE1 = 35;  // grey
constexpr uint8_t PIN_PE2 = 36;  // violet
constexpr uint8_t PIN_PE3 = 37;  // blue
constexpr uint8_t PIN_PE4 = 38;  // green
constexpr uint8_t PIN_PE5 = 39;  // yellow
constexpr uint8_t PIN_PE6 = 40;  // orange (only with J6 in port position)
constexpr uint8_t PIN_PE7 = 41;  // red    (only with J7 in port position)

// ------------------------------------------------------- direction control
// DIR0 -> port C, DIR1 -> port D. High = HCT drives the DUT, low = LVC
// reads. 10 k pulldowns (R3/R4 on the OE nets) mean C and D power up
// READING. Ports A/B/E have no runtime direction - jumpers J8/J12/J15.
// The onboard LED is pin 13 = DIR0: lit = port C driving.
constexpr uint8_t PIN_DIR0 = 34;
constexpr uint8_t PIN_DIR1 = 33;

// ------------------------------------------------------------------- Buttons
// SW0/SW1 share Teensy 40/41 with PE6/PE7, selected by jumpers J6/J7
// (position 1 = button through 1 k, position 3 = port bit). Buttons are
// momentary to GND with no board pull-ups - internal pull-ups do the work.
// SW0 = start/advance, SW1 = abort/exit (harness convention). The 1 k legs
// make a jumper/sketch mismatch harmless.
constexpr uint8_t PIN_SW0 = 40;
constexpr uint8_t PIN_SW1 = 41;
constexpr uint8_t SW_PIN_MODE = INPUT_PULLUP;

#elif defined(__AVR_ATmega2560__)
// ============================================================== Mega 2560 ===
// Direct DUT wiring on the Mega's header pins 22..53. 5 V push-pull drive,
// no level shifters. Independent verification platform.

#define PLATFORM_NAME "Arduino Mega 2560 (direct)"

// Compatibility shims: sketches use the Teensy fast API and delayNanoseconds
// directly. On AVR these map to the plain core calls; delayNanoseconds
// rounds UP to whole microseconds.
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

// ---------------------------------------------------------------- Port E pins
// Four strobe lines on PWM-capable pins; PE0 = CLK by convention. The Mega
// has no PE4-PE7 - sketches that need more than four strobes are
// Teensy-only.
constexpr uint8_t PIN_PE0 = 5;   // white  (CLK)
constexpr uint8_t PIN_PE1 = 6;   // grey
constexpr uint8_t PIN_PE2 = 7;   // violet
constexpr uint8_t PIN_PE3 = 8;   // blue
constexpr uint8_t PIN_PE4 = 9;   // (present for compile compatibility;
constexpr uint8_t PIN_PE5 = 10;  //  wire only if the project needs them)
constexpr uint8_t PIN_PE6 = 11;
constexpr uint8_t PIN_PE7 = 12;

// ------------------------------------------------------------------- Buttons
// Two spare pins with the internal pull-ups so that unwired = not pressed.
// Wire momentary switches to GND on pins 2 (SW0) and 3 (SW1) if wanted;
// every button function has a serial command.
constexpr uint8_t PIN_SW0 = 2;
constexpr uint8_t PIN_SW1 = 3;
constexpr uint8_t SW_PIN_MODE = INPUT_PULLUP;

#else
#error "AdapterPorts.h: unsupported board - build for Teensy 4.1 or Arduino Mega 2560."
#endif

// ======================================================= shared definitions

// -------------------------------------------------------- pin tables (setup)
constexpr uint8_t PORT_A_PINS[8] = { PIN_PA0, PIN_PA1, PIN_PA2, PIN_PA3, PIN_PA4, PIN_PA5, PIN_PA6, PIN_PA7 };
constexpr uint8_t PORT_B_PINS[8] = { PIN_PB0, PIN_PB1, PIN_PB2, PIN_PB3, PIN_PB4, PIN_PB5, PIN_PB6, PIN_PB7 };
constexpr uint8_t PORT_C_PINS[8] = { PIN_PC0, PIN_PC1, PIN_PC2, PIN_PC3, PIN_PC4, PIN_PC5, PIN_PC6, PIN_PC7 };
constexpr uint8_t PORT_D_PINS[8] = { PIN_PD0, PIN_PD1, PIN_PD2, PIN_PD3, PIN_PD4, PIN_PD5, PIN_PD6, PIN_PD7 };
constexpr uint8_t PORT_E_PINS[8] = { PIN_PE0, PIN_PE1, PIN_PE2, PIN_PE3, PIN_PE4, PIN_PE5, PIN_PE6, PIN_PE7 };

// Set the direction of a whole port: INPUT, INPUT_PULLUP or OUTPUT.
inline void setPortMode(const uint8_t (&pins)[8], uint8_t mode) {
  for (uint8_t i = 0; i < 8; i++) {
    pinMode(pins[i], mode);
  }
}

// --------------------------------------------------------------- fast readers
// Unrolled with named constants so every digitalReadFast folds to one
// register test on the Teensy. Do NOT convert these to loops.

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

// --------------------------------------------------------------- fast writers

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

// -------------------------------------------------------------- convenience
// Data ports in one 32-bit word: A = bits 0-7, B = 8-15, C = 16-23, D = 24-31.
inline uint32_t samplePorts() {
  return  (uint32_t)readPortA()
       | ((uint32_t)readPortB() << 8)
       | ((uint32_t)readPortC() << 16)
       | ((uint32_t)readPortD() << 24);
}

// Buttons are active-low: true = pressed.
inline bool sw0Pressed() { return digitalReadFast(PIN_SW0) == LOW; }
inline bool sw1Pressed() { return digitalReadFast(PIN_SW1) == LOW; }

#if defined(__IMXRT1062__)
// ------------------------------------------- runtime direction (C/D only)
// Call initPortDirections() first thing in setup(): DIR pins low (C and D
// reading, matching the pulldown power-up state), all port pins inputs.
// Then configure per the board's jumpers and say so in the boot banner.
//
// portDrive('C'|'D')   - take the bus: DIR high, then Teensy pins OUTPUT.
// portRelease('C'|'D') - give it back: Teensy pins INPUT, then DIR low.
//
// During portDrive the DUT-side bus is briefly undefined (~us) between the
// DIR flip and the pins driving - the same window any real bus turnaround
// has. Fit the port's SIP pulls (RN3/RN4 via J3/J4) when it talks to a
// tri-state DUT bus so the level is defined during it.

inline void initPortDirections() {
  pinMode(PIN_DIR0, OUTPUT); digitalWriteFast(PIN_DIR0, LOW);
  pinMode(PIN_DIR1, OUTPUT); digitalWriteFast(PIN_DIR1, LOW);
  setPortMode(PORT_A_PINS, INPUT);
  setPortMode(PORT_B_PINS, INPUT);
  setPortMode(PORT_C_PINS, INPUT);
  setPortMode(PORT_D_PINS, INPUT);
  setPortMode(PORT_E_PINS, INPUT);
}

inline void portDrive(char port) {
  if (port == 'C') {
    digitalWriteFast(PIN_DIR0, HIGH);
    setPortMode(PORT_C_PINS, OUTPUT);
  } else if (port == 'D') {
    digitalWriteFast(PIN_DIR1, HIGH);
    setPortMode(PORT_D_PINS, OUTPUT);
  }
}

inline void portRelease(char port) {
  if (port == 'C') {
    setPortMode(PORT_C_PINS, INPUT);
    digitalWriteFast(PIN_DIR0, LOW);
  } else if (port == 'D') {
    setPortMode(PORT_D_PINS, INPUT);
    digitalWriteFast(PIN_DIR1, LOW);
  }
}
#endif  // __IMXRT1062__
