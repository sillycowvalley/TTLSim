// ============================================================================
//  AdapterPorts.h  -  virtual 8-bit ports A..E on the Teensy 5V Adapter board
//
//  Target: Teensy 4.1 (__IMXRT1062__) on the 5V Adapter board - 74HCT245
//  (drive, 5 V) + 74LVC245 (read, 3.3 V) per port, /OE-steered.
//
//  This header is the single source of truth for the board's pin mapping and
//  for every port-level access primitive. Sketches include it unchanged and
//  add only project logic. If a sketch finds itself writing GPIO plumbing,
//  the plumbing belongs here instead.
//
//  Provides:
//    - PIN_PA0..PIN_PE7, PORT_x_PINS[8] tables, setPortMode()
//    - unrolled fast accessors readPortA()..readPortE(), writePortA()..E()
//    - samplePorts() - ports A-D as one uint32_t
//    - banked port I/O: PortBanks, buildPortBanks(), bankedWritePort(),
//      bankedReadPort() - simultaneous multi-line slew, see below
//    - runtime direction for ports C/D: initPortDirections(), portDrive(),
//      portRelease()
//    - buttons SW0/SW1, SW_PIN_MODE, sw0Pressed(), sw1Pressed()
//    - printPortPins(), printPortBanks() for boot banners
//    - PLATFORM_NAME
//
//  Ribbon colours: bit 0 = white .. bit 7 = red.
//
//  Direction control:
//    Ports A, B, E - direction set by board jumpers J8 / J12 / J15
//                    (fitted = port drives the DUT, parked = port reads).
//    Ports C, D    - runtime direction on PIN_DIR0 / PIN_DIR1
//                    (high = drive, low = read; 10 k pulldowns = read at
//                    power-up). portDrive('C'|'D') / portRelease('C'|'D').
//    The sketch must match the jumpers - print the assumed configuration
//    in the boot banner.
//
//  Keep pins as compile-time constants and the unrolled accessors unrolled:
//  digitalReadFast/digitalWriteFast then fold to single GPIO register
//  operations. Never convert the accessors to loops - a variable pin
//  argument defeats the fast path entirely.
// ============================================================================

#pragma once
#include <Arduino.h>

#if !defined(__IMXRT1062__)
#error "AdapterPorts.h targets the Teensy 5V Adapter board - build for Teensy 4.1."
#endif

#define PLATFORM_NAME "Teensy 4.1 + 5V Adapter board"

// Naming: PIN_PA0..PIN_PE7 ("P" for port). PIN_A0..PIN_A9 cannot be used -
// the cores define those as macros for the analog pins, which breaks any
// declaration reusing the names.

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
// Strobe/sense port. PE0 = CLK by convention. Direction is set by jumper J15
// for the whole port (fitted = drive). PE6/PE7 are shared with the buttons
// via J6/J7 (see Buttons below).
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
// The onboard LED is pin 13 = PE0.
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
// register test. Do NOT convert these to loops.

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

// ====================================================== banked port I/O =====
// An alternative to the unrolled accessors above. NOT a speed optimisation -
// pick it for what it does to TIMING, not throughput.
//
// The unrolled writer issues eight separate stores, so bit 0 changes roughly
// 70 ns before bit 7, and the unrolled reader samples bit 0 well before
// bit 7. Banked collapses each GPIO bank to a single store on write and a
// single load on read, so every bit within a bank moves - and is sampled -
// on one instant.
//
// Two consequences, both measured on this board (2026-07):
//
//  1. HARSHER AGGRESSOR. A genuinely simultaneous multi-line slew is what a
//     real bus turnaround looks like, and what coupled a data bus into a
//     clock line in the ALU campaign. The unrolled path's stagger softens it.
//
//  2. SHORTER WRITE-TO-SAMPLE INTERVAL, which makes it the instrument for
//     timing measurement. A settle sweep on the unrolled path reports zero
//     errors at every delay from 0 ns up, because the loop's own stagger
//     already exceeds what is being resolved. Timing sweeps must run banked
//     or they measure nothing.
//
//     What that sweep established here: a banked write followed immediately
//     by a banked read FAILS every trial, while the same pair separated by
//     delayNanoseconds(0) - which costs tens of ns in call overhead before
//     it delays anything - is clean. So the '245 pair's settle requirement
//     lies inside that gap: non-zero, but below what software timing can
//     resolve on this part. Consistent with the datasheet sum of ~15 ns
//     (HCT245 ~9-13 ns at 5V, LVC245 ~3.5-5 ns).
//
//     CAUTION when reading such a sweep: if the delay call is skipped at
//     zero rather than made with a zero argument, row 0 is structurally
//     different from every other row and the overhead appearing looks like
//     a propagation cliff. It is not. Make the call at every row.
//
// SPEED: essentially a wash in real use. Hammer runs with the read consumed:
// unrolled 285 ns/vector, banked 290. A microbenchmark that discards the
// read will flatter banked badly (90 vs 151 ns was observed) because its
// bit-extraction arithmetic is elidable while eight volatile loads are not.
// Believe the workload, not the microbenchmark.
//
// Cost: each PortBanks is ~4 KB (a 256-entry set-mask table per bank).
// Declare only the ports a sketch actually banks.
//
// Usage:
//     static PortBanks banksA;
//     buildPortBanks(banksA, PORT_A_PINS);   // in setup(), after setPortMode
//     bankedWritePort(banksA, 0xFF);
//     uint8_t v = bankedReadPort(banksA);
//
// The bank partition is derived from the pin table, so this stays correct if
// the mapping above ever changes. Both the set/clear and input register
// pointers come from the same GPIO base, so one partition serves reads and
// writes.

static const uint8_t PORT_MAX_BANKS = 4;

struct PortBanks {
  volatile uint32_t *setReg[PORT_MAX_BANKS];
  volatile uint32_t *clrReg[PORT_MAX_BANKS];
  volatile uint32_t *inReg[PORT_MAX_BANKS];
  uint32_t           allMask[PORT_MAX_BANKS];
  uint32_t           setTable[PORT_MAX_BANKS][256];
  uint32_t           bitMask[8];
  uint8_t            bitBank[8];
  uint8_t            bankCount;
};

// Sink for unused bank slots, so the access paths stay branchless.
inline volatile uint32_t *portBankSink() {
  static volatile uint32_t sink = 0;
  return &sink;
}

inline void buildPortBanks(PortBanks &pb, const uint8_t (&pins)[8]) {
  pb.bankCount = 0;

  for (uint8_t i = 0; i < 8; i++) {
    uint8_t pin = pins[i];
    volatile uint32_t *sr = portSetRegister(pin);

    uint8_t b = 0xFF;
    for (uint8_t k = 0; k < pb.bankCount; k++) {
      if (pb.setReg[k] == sr) { b = k; break; }
    }
    if (b == 0xFF) {
      b = pb.bankCount++;
      pb.setReg[b]  = sr;
      pb.clrReg[b]  = portClearRegister(pin);
      pb.inReg[b]   = portInputRegister(pin);
      pb.allMask[b] = 0;
    }
    pb.bitBank[i] = b;
    pb.bitMask[i] = digitalPinToBitMask(pin);
    pb.allMask[b] |= pb.bitMask[i];
  }

  for (uint8_t b = 0; b < pb.bankCount; b++) {
    for (uint16_t v = 0; v < 256; v++) {
      uint32_t s = 0;
      for (uint8_t i = 0; i < 8; i++) {
        if (pb.bitBank[i] == b && (v & (1u << i))) {
          s |= pb.bitMask[i];
        }
      }
      pb.setTable[b][v] = s;
    }
  }

  for (uint8_t b = pb.bankCount; b < PORT_MAX_BANKS; b++) {
    pb.setReg[b]  = portBankSink();
    pb.clrReg[b]  = portBankSink();
    pb.inReg[b]   = portBankSink();
    pb.allMask[b] = 0;
    for (uint16_t v = 0; v < 256; v++) {
      pb.setTable[b][v] = 0;
    }
  }
}

// Every bit within a bank changes on one store.
inline void bankedWritePort(const PortBanks &pb, uint8_t v) {
  uint32_t s0 = pb.setTable[0][v];
  uint32_t s1 = pb.setTable[1][v];
  uint32_t s2 = pb.setTable[2][v];
  uint32_t s3 = pb.setTable[3][v];
  *pb.setReg[0] = s0;  *pb.clrReg[0] = pb.allMask[0] & ~s0;
  *pb.setReg[1] = s1;  *pb.clrReg[1] = pb.allMask[1] & ~s1;
  *pb.setReg[2] = s2;  *pb.clrReg[2] = pb.allMask[2] & ~s2;
  *pb.setReg[3] = s3;  *pb.clrReg[3] = pb.allMask[3] & ~s3;
}

// Each bank sampled once, so the byte is one instant per bank rather than
// eight instants spread over the read.
inline uint8_t bankedReadPort(const PortBanks &pb) {
  uint32_t snap[PORT_MAX_BANKS];
  snap[0] = *pb.inReg[0];
  snap[1] = *pb.inReg[1];
  snap[2] = *pb.inReg[2];
  snap[3] = *pb.inReg[3];

  uint8_t v = 0;
  if (snap[pb.bitBank[0]] & pb.bitMask[0]) v |= 0x01;
  if (snap[pb.bitBank[1]] & pb.bitMask[1]) v |= 0x02;
  if (snap[pb.bitBank[2]] & pb.bitMask[2]) v |= 0x04;
  if (snap[pb.bitBank[3]] & pb.bitMask[3]) v |= 0x08;
  if (snap[pb.bitBank[4]] & pb.bitMask[4]) v |= 0x10;
  if (snap[pb.bitBank[5]] & pb.bitMask[5]) v |= 0x20;
  if (snap[pb.bitBank[6]] & pb.bitMask[6]) v |= 0x40;
  if (snap[pb.bitBank[7]] & pb.bitMask[7]) v |= 0x80;
  return v;
}

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

// ------------------------------------------------------------ boot banner
// Small helpers so every sketch reports its wiring the same way. Checking
// the printed pins against the loom before trusting a run has caught more
// faults than any amount of staring at the schematic.

inline void printPortPins(const char *name, const uint8_t (&pins)[8]) {
  Serial.print(name);
  for (uint8_t i = 0; i < 8; i++) {
    Serial.print(' ');
    Serial.print(pins[i]);
  }
  Serial.println();
}

inline void printPortBanks(const char *name, const PortBanks &pb) {
  Serial.print(name);
  Serial.print(' ');
  Serial.print(pb.bankCount);
  Serial.println(F(" GPIO bank(s)"));
}
