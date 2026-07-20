// ============================================================================
//  PortChaser.ino  -  LED chaser / pin-map verifier (dual platform)
//
//  Teensy 4.1 (LVC harness board pin map) or Arduino Mega 2560 -
//  AdapterPorts.h picks the mapping from the board selected in the IDE.
//  Run this on any new board or wiring before trusting anything else:
//  it is the pin-map verifier of record.
//
//  Needs AdapterPorts.h in the same sketch folder.
//
//  Usage: open the Serial Monitor and type a command:
//    A, B, C, D, E : walking-bit chaser on that port (bit 0 white up to
//                    bit 7 red), chasePasses passes, then back to the menu
//    R             : (Teensy) walk DIR0/DIR1 alternately for chasePasses
//                    passes - watch the LED (DIR0) and pin 33
//    S             : report button states until any port command arrives
//  Typing a different command mid-run switches immediately.
//
//  Bare-Teensy pin verification (Stage 0, see LVCBoard_Reference.md):
//  probe with 8-bit LED Thing bars - power them from the Teensy's 3.3 V
//  rail so their thresholds match, ground unused sense inputs. All 42
//  pins are covered by A-E + R + S (buttons: short pin 40/41 to GND).
//
//  On the assembled board: fit J8/J12/J15 so A/B/E drive, and note that
//  C/D chase through portDrive. PE6/PE7 only reach the headers with J6/J7
//  in the port position.
// ============================================================================

#include "AdapterPorts.h"

static const unsigned long stepMillis  = 100;  // chaser speed
static const uint8_t       chasePasses = 5;    // passes before the menu

static char          activeMode      = 0;   // 'A'..'E', 'R', 'S', 0 = menu
static uint8_t       chaserBit       = 0;
static uint8_t       completedPasses = 0;
static unsigned long lastStep        = 0;
static bool          lastSw0         = false;
static bool          lastSw1         = false;

void writeActivePort(uint8_t value) {
  switch (activeMode) {
    case 'A': writePortA(value); break;
    case 'B': writePortB(value); break;
    case 'C': writePortC(value); break;
    case 'D': writePortD(value); break;
    case 'E': writePortE(value); break;
    default:  break;
  }
}

void allPortsOff() {
  writePortA(0);
  writePortB(0);
  writePortC(0);
  writePortD(0);
  writePortE(0);
#if defined(__IMXRT1062__)
  digitalWriteFast(PIN_DIR0, LOW);
  digitalWriteFast(PIN_DIR1, LOW);
#endif
}

void promptUser() {
  Serial.println(F("Chaser - A,B,C,D,E = port walk; R = DIR walk; S = buttons:"));
}

void selectMode(char letter) {
  allPortsOff();
  activeMode      = letter;
  chaserBit       = 0;
  completedPasses = 0;
  lastStep        = millis();
  Serial.print(F("Mode "));
  Serial.println(activeMode);
#if defined(__IMXRT1062__)
  // C/D chase through the runtime direction path so the DUT side lights.
  if (letter == 'C' || letter == 'D') portDrive(letter);
#endif
}

void backToMenu() {
#if defined(__IMXRT1062__)
  if (activeMode == 'C' || activeMode == 'D') portRelease(activeMode);
#endif
  allPortsOff();
  activeMode = 0;
  Serial.println(F("Done."));
  promptUser();
}

void setup() {
  Serial.begin(115200);              // real on the Mega; ignored on Teensy

#if defined(__IMXRT1062__)
  initPortDirections();
  // Chaser drives every port from the Teensy side regardless of jumpers.
  setPortMode(PORT_A_PINS, OUTPUT);
  setPortMode(PORT_B_PINS, OUTPUT);
  setPortMode(PORT_E_PINS, OUTPUT);
  // C/D stay inputs until their chase runs (portDrive handles them).
#else
  setPortMode(PORT_A_PINS, OUTPUT);
  setPortMode(PORT_B_PINS, OUTPUT);
  setPortMode(PORT_C_PINS, OUTPUT);
  setPortMode(PORT_D_PINS, OUTPUT);
  setPortMode(PORT_E_PINS, OUTPUT);
#endif
  allPortsOff();

  pinMode(PIN_SW0, SW_PIN_MODE);
  pinMode(PIN_SW1, SW_PIN_MODE);

  while (!Serial && millis() < 3000) { }
  Serial.print(F("PortChaser on "));
  Serial.println(F(PLATFORM_NAME));
  promptUser();
}

void loop() {
  // Command input - accepted at any time, running or not.
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c >= 'a' && c <= 'z') {
      c = (char)(c - 'a' + 'A');
    }
    if ((c >= 'A' && c <= 'E') || c == 'S') {
      selectMode(c);
    }
#if defined(__IMXRT1062__)
    if (c == 'R') {
      selectMode('R');
    }
#endif
    // CR/LF and anything else: ignored
  }

  if (activeMode == 0) return;

  // Button reporting mode
  if (activeMode == 'S') {
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
    return;
  }

#if defined(__IMXRT1062__)
  // DIR walk: DIR0 and DIR1 alternate; onboard LED shows DIR0.
  if (activeMode == 'R') {
    if (millis() - lastStep >= stepMillis * 3) {
      lastStep = millis();
      chaserBit = (uint8_t)((chaserBit + 1) & 0x03);
      digitalWriteFast(PIN_DIR0, (chaserBit & 0x01) ? HIGH : LOW);
      digitalWriteFast(PIN_DIR1, (chaserBit & 0x02) ? HIGH : LOW);
      if (chaserBit == 0) {
        completedPasses++;
        if (completedPasses >= chasePasses) backToMenu();
      }
    }
    return;
  }
#endif

  // Port walk
  if (millis() - lastStep >= stepMillis) {
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
