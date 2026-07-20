// ============================================================================
//  AdapterPorts.h  -  virtual 8-bit ports A..D on the Teensy 4.1 adapter
//
//  Physical layout (J1, 2x18 header, board photographed orientation):
//     top row      : 2x GND
//     bottom row   : 2x +5V
//     edge column  : Port D (upper 8) over Port C (lower 8)
//     inner column : Port B (upper 8) over Port A (lower 8)
//  Within each port: bit 0 = white ribbon wire (bottom of group),
//                    bit 7 = red ribbon wire   (top of group).
//  All J1 pins are 5 V logic via the on-board level shifters.
//
//  Pin numbers are Teensy 4.1 Arduino pins. Pin-to-port/bit assignment
//  verified end-to-end by LED chaser. The diagram
//  (TeensyAdapter_Ports.svg) matches this file.
//  Keep pins as compile-time constants: digitalReadFast/digitalWriteFast
//  then compile to single GPIO register operations on the 4.1.
// ============================================================================

#pragma once
#include <Arduino.h>

// Naming: PIN_PA0..PIN_PD7 ("P" for port). PIN_A0..PIN_A9 cannot be used -
// the Teensy core's pins_arduino.h defines those as macros for the analog
// pins, which breaks any declaration reusing the names.

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
// SW1 (right) and SW2 (left), active-low, 4.7k pull-ups to 3.3 V on the board.
// Derived from the footprint (Teensy 3.5 A21/A22 positions) - verify once.
constexpr uint8_t PIN_SW1 = 40;
constexpr uint8_t PIN_SW2 = 41;

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
// register test. Do NOT convert these to loops over the pin tables - a
// variable pin defeats digitalReadFast on the Teensy.

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
