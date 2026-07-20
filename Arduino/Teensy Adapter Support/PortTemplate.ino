// ============================================================================
//  PortTemplate.ino  -  starting template for Teensy 4.1 adapter sketches
//
//  Uses AdapterPorts.h (keep it in the same sketch folder). Demonstrates:
//    - configuring each virtual port's direction in setup()
//    - reading all 32 I/O as one word with samplePorts()
//    - writing an 8-bit value to a port
//    - the two user buttons
//
//  As shipped it configures all four ports as inputs, prints the 32-bit
//  state as 8 hex chars whenever it changes, and prints button events.
//  Adapt setup() and loop() per project; leave AdapterPorts.h untouched so
//  every sketch shares the same verified mapping.
// ============================================================================

#include "AdapterPorts.h"

static const long serialBaud = 115200L;  // native USB - value is ignored

static uint32_t lastSample = 0xFFFFFFFFUL;
static bool     lastSw1    = false;
static bool     lastSw2    = false;

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

  // Direction per port - change per project:
  //   INPUT        : line is driven by external hardware
  //   INPUT_PULLUP : line may float / is a shared tri-state rail
  //   OUTPUT       : this sketch drives the line
  setPortMode(PORT_A_PINS, INPUT);
  setPortMode(PORT_B_PINS, INPUT);
  setPortMode(PORT_C_PINS, INPUT);
  setPortMode(PORT_D_PINS, INPUT);

  // Buttons have external 4.7k pull-ups on the board - plain INPUT.
  pinMode(PIN_SW1, INPUT);
  pinMode(PIN_SW2, INPUT);

  // Example: to drive port D instead, replace its line above with
  //   setPortMode(PORT_D_PINS, OUTPUT);
  // and write it with
  //   writePortD(0xA5);
  // (Note D1 is the onboard LED, so it doubles as a visual bit-1 indicator.)

  lastSample = samplePorts();
  lastSw1 = sw1Pressed();
  lastSw2 = sw2Pressed();
}

void loop() {
  // Report any change on the 32 I/O pins as one hex word:
  // A = bits 0-7, B = 8-15, C = 16-23, D = 24-31.
  uint32_t sample = samplePorts();
  if (sample != lastSample) {
    printHex32(sample);
    lastSample = sample;
  }

  // Button edges
  bool sw1 = sw1Pressed();
  if (sw1 != lastSw1) {
    Serial.println(sw1 ? "SW1 down" : "SW1 up");
    lastSw1 = sw1;
  }
  bool sw2 = sw2Pressed();
  if (sw2 != lastSw2) {
    Serial.println(sw2 ? "SW2 down" : "SW2 up");
    lastSw2 = sw2;
  }
}
