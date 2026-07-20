// ============================================================================
//  PortChaser.ino  -  LED chaser on a chosen virtual port (dual platform)
//
//  Teensy 4.1 on the adapter, or Arduino Mega 2560 wired directly into the
//  same header positions - AdapterPorts.h picks the mapping from the board
//  selected in the IDE. This is the sketch that originally verified the
//  Teensy mapping end-to-end; run it once on any new board/wiring before
//  trusting anything else.
//
//  Needs AdapterPorts.h in the same sketch folder.
//
//  Usage: open the Serial Monitor, type A, B, C or D (either case) and press
//  Enter. A walking-bit chaser runs on that port for chasePasses full passes
//  (bit 0 white up to bit 7 red), then blanks the LEDs and returns to the
//  menu. Typing a different letter mid-run switches ports immediately.
//
//  LED note: through the adapter's level shifters, LEDs need buffering or
//  high-value resistors (weak TXS static drive). The Mega's pins drive an
//  LED + resistor directly (20 mA class).
// ============================================================================

#include "AdapterPorts.h"

static const unsigned long stepMillis  = 100;  // chaser speed
static const uint8_t       chasePasses = 5;    // full passes before returning to the menu

static char          activePort      = 0;   // 'A'..'D', 0 = at the menu
static uint8_t       chaserBit       = 0;   // 0..7
static uint8_t       completedPasses = 0;
static unsigned long lastStep        = 0;

void writeActivePort(uint8_t value) {
  switch (activePort) {
    case 'A': writePortA(value); break;
    case 'B': writePortB(value); break;
    case 'C': writePortC(value); break;
    case 'D': writePortD(value); break;
    default:  break;
  }
}

void allPortsOff() {
  writePortA(0);
  writePortB(0);
  writePortC(0);
  writePortD(0);
}

void promptUser() {
  Serial.println(F("LED chaser - which port? Type A, B, C or D:"));
}

void selectPort(char letter) {
  allPortsOff();
  activePort      = letter;
  chaserBit       = 0;
  completedPasses = 0;
  lastStep        = millis();
  Serial.print(F("Chasing on port "));
  Serial.print(activePort);
  Serial.print(F(" ("));
  Serial.print(chasePasses);
  Serial.println(F(" passes)"));
}

void backToMenu() {
  allPortsOff();
  activePort = 0;
  Serial.println(F("Done."));
  promptUser();
}

void setup() {
  Serial.begin(115200);              // real on the Mega; ignored on Teensy

  // LEDs on all four ports -> everything is an output, everything off.
  setPortMode(PORT_A_PINS, OUTPUT);
  setPortMode(PORT_B_PINS, OUTPUT);
  setPortMode(PORT_C_PINS, OUTPUT);
  setPortMode(PORT_D_PINS, OUTPUT);
  allPortsOff();

  // Wait briefly for the monitor so the prompt isn't missed, then ask.
  while (!Serial && millis() < 3000) { }
  Serial.print(F("PortChaser on "));
  Serial.println(F(PLATFORM_NAME));
  promptUser();
}

void loop() {
  // Port selection - accepts a new letter at any time, running or not.
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c >= 'a' && c <= 'd') {
      c = (char)(c - 'a' + 'A');       // to upper case
    }
    if (c >= 'A' && c <= 'D') {
      selectPort(c);
    }
    // CR/LF and anything else: ignored
  }

  // Run the chaser on the chosen port.
  if (activePort != 0 && millis() - lastStep >= stepMillis) {
    lastStep = millis();
    writeActivePort((uint8_t)(1u << chaserBit));
    chaserBit++;
    if (chaserBit > 7) {               // pass complete (bit 7 just lit)
      chaserBit = 0;
      completedPasses++;
      if (completedPasses >= chasePasses) {
        backToMenu();
      }
    }
  }
}
