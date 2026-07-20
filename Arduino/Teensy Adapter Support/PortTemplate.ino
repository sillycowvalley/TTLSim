// ============================================================================
//  PortTemplate.ino  -  starting template for harness sketches
//
//  Dual platform: Teensy 4.1 on the LVC harness board, or Arduino Mega
//  2560 wired directly to the DUT. AdapterPorts.h picks the mapping from
//  the board selected in the IDE - no #define needed.
//
//  Uses AdapterPorts.h (keep it in the same sketch folder). Demonstrates:
//    - configuring port directions in setup()
//    - reading the data ports as one word with samplePorts()
//    - the two user buttons (SW0/SW1)
//
//  As shipped it configures all ports as inputs, prints the 32-bit data
//  port state as 8 hex chars whenever it changes, and prints button
//  events. Adapt setup() and loop() per project; leave AdapterPorts.h
//  untouched so every sketch shares the same verified pins.
//
//  LVC-board direction rules (see LVCBoard_Reference.md):
//    - Ports A/B/E: direction is jumper J8/J12/J15 - declare the assumed
//      configuration in the boot banner; the sketch cannot change it.
//    - Ports C/D: runtime direction via portDrive('C'|'D') /
//      portRelease('C'|'D'). initPortDirections() first in setup().
//
//  Dual-target sketch rules:
//    - wrap string literals in F(...) - the Mega has 8 KB SRAM
//    - pinMode(PIN_SWx, SW_PIN_MODE), never a hard-coded mode
//    - print PLATFORM_NAME at boot so logs identify the board
//    - on the Mega, Serial is a real 115200 UART: printing costs time,
//      so keep the change-detection pattern
// ============================================================================

#include "AdapterPorts.h"

static const long serialBaud = 115200L;  // real on the Mega; ignored on Teensy

static uint32_t lastSample = 0xFFFFFFFFUL;
static bool     lastSw0    = false;
static bool     lastSw1    = false;

void printHex32(uint32_t word) {
  static const char hexDigits[] = "0123456789ABCDEF";
  char buf[10];
  for (int8_t nib = 7; nib >= 0; nib--) {
    buf[7 - nib] = hexDigits[(word >> (nib * 4)) & 0x0F];
  }
  buf[8] = '\n';
  buf[9] = '\0';
  Serial.write(buf, 9);
}

void setup() {
  Serial.begin(serialBaud);

#if defined(__IMXRT1062__)
  // LVC board: C/D reading, everything an input, jumpers rule A/B/E.
  initPortDirections();
#else
  // Mega: direction is just pinMode.
  setPortMode(PORT_A_PINS, INPUT);
  setPortMode(PORT_B_PINS, INPUT);
  setPortMode(PORT_C_PINS, INPUT);
  setPortMode(PORT_D_PINS, INPUT);
  setPortMode(PORT_E_PINS, INPUT);
#endif

  pinMode(PIN_SW0, SW_PIN_MODE);
  pinMode(PIN_SW1, SW_PIN_MODE);

  // Example: to drive port C on the LVC board, call portDrive('C') and
  // write it with writePortC(0xA5); portRelease('C') hands the bus back.
  // Ports A/B/E drive only if their jumper is fitted - then
  // setPortMode(PORT_x_PINS, OUTPUT) and write as usual.

  lastSample = samplePorts();
  lastSw0 = sw0Pressed();
  lastSw1 = sw1Pressed();

  while (!Serial && millis() < 3000) { }
  Serial.print(F("PortTemplate ready on "));
  Serial.println(F(PLATFORM_NAME));
  Serial.println(F("Assumed jumpers: J8/J12/J15 parked (A/B/E read)"));
}

void loop() {
  // Report any change on the 32 data-port pins as one hex word:
  // A = bits 0-7, B = 8-15, C = 16-23, D = 24-31.
  uint32_t sample = samplePorts();
  if (sample != lastSample) {
    printHex32(sample);
    lastSample = sample;
  }

  // Button edges
  bool sw0 = sw0Pressed();
  if (sw0 != lastSw0) {
    Serial.println(sw0 ? F("SW0 down") : F("SW0 up"));
    lastSw0 = sw0;
  }
  bool sw1 = sw1Pressed();
  if (sw1 != lastSw1) {
    Serial.println(sw1 ? F("SW1 down") : F("SW1 up"));
    lastSw1 = sw1;
  }
}
