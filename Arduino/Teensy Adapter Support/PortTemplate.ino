// ============================================================================
//  PortTemplate.ino  -  starting template for adapter/Mega harness sketches
//
//  Dual platform: Teensy 4.1 on the level-shifter adapter, or Arduino Mega
//  2560 wired directly into the same header positions. AdapterPorts.h picks
//  the mapping from the board selected in the IDE - no #define needed.
//
//  Uses AdapterPorts.h (keep it in the same sketch folder). Demonstrates:
//    - configuring each virtual port's direction in setup()
//    - reading all 32 I/O as one word with samplePorts()
//    - writing an 8-bit value to a port
//    - the two user buttons
//
//  As shipped it configures all four ports as inputs, prints the 32-bit
//  state as 8 hex chars whenever it changes, and prints button events.
//  Adapt setup() and loop() per project; leave the Teensy mapping in
//  AdapterPorts.h untouched so every sketch shares the same verified pins.
//
//  Dual-target sketch rules (see the reference docs):
//    - wrap string literals in F(...) - the Mega has 8 KB SRAM
//    - pinMode(PIN_SWx, SW_PIN_MODE), never a hard-coded mode
//    - print PLATFORM_NAME at boot so logs identify the board
//    - on the Mega, Serial is a real 115200 UART: printing costs time,
//      so keep the change-detection pattern
// ============================================================================

#include "AdapterPorts.h"

static const long serialBaud = 115200L;  // real on the Mega; ignored on Teensy

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

  // Buttons: adapter has 4.7k pull-ups on the board (plain INPUT);
  // the Mega uses its internal pull-ups. SW_PIN_MODE covers both.
  pinMode(PIN_SW1, SW_PIN_MODE);
  pinMode(PIN_SW2, SW_PIN_MODE);

  // Example: to drive port D instead, replace its line above with
  //   setPortMode(PORT_D_PINS, OUTPUT);
  // and write it with
  //   writePortD(0xA5);
  // (On the Teensy, D1 is the onboard LED - a free visual bit-1 indicator.)

  lastSample = samplePorts();
  lastSw1 = sw1Pressed();
  lastSw2 = sw2Pressed();

  while (!Serial && millis() < 3000) { }
  Serial.print(F("PortTemplate ready on "));
  Serial.println(F(PLATFORM_NAME));
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
    Serial.println(sw1 ? F("SW1 down") : F("SW1 up"));
    lastSw1 = sw1;
  }
  bool sw2 = sw2Pressed();
  if (sw2 != lastSw2) {
    Serial.println(sw2 ? F("SW2 down") : F("SW2 up"));
    lastSw2 = sw2;
  }
}
