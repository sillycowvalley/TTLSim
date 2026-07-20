// ============================================================================
//  PortChaser.ino  -  LED chaser on a chosen virtual port (Teensy 4.1 adapter)
//
//  Needs AdapterPorts.h in the same sketch folder.
//
//  Usage: open the Serial Monitor, type A, B, C or D (either case) and press
//  Enter. A walking-bit chaser runs on that port for chasePasses full passes
//  (bit 0 white up to bit 7 red), then blanks the LEDs and returns to the
//  menu. Typing a different letter mid-run switches ports immediately.
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
  Serial.println("LED chaser - which port? Type A, B, C or D:");
}

void selectPort(char letter) {
  allPortsOff();
  activePort      = letter;
  chaserBit       = 0;
  completedPasses = 0;
  lastStep        = millis();
  Serial.print("Chasing on port ");
  Serial.print(activePort);
  Serial.print(" (");
  Serial.print(chasePasses);
  Serial.println(" passes)");
}

void backToMenu() {
  allPortsOff();
  activePort = 0;
  Serial.println("Done.");
  promptUser();
}

void setup() {
  Serial.begin(115200);

  // LEDs on all four ports -> everything is an output, everything off.
  setPortMode(PORT_A_PINS, OUTPUT);
  setPortMode(PORT_B_PINS, OUTPUT);
  setPortMode(PORT_C_PINS, OUTPUT);
  setPortMode(PORT_D_PINS, OUTPUT);
  allPortsOff();

  // Wait briefly for the monitor so the prompt isn't missed, then ask.
  while (!Serial && millis() < 3000) { }
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
