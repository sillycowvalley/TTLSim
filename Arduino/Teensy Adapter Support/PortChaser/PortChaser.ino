// ============================================================================
//  PortChaser.ino  -  walking-bit LED chaser on any port, A..E
//
//  Teensy 4.1 on the LVC harness board, or Arduino Mega 2560 wired direct.
//  AdapterPorts.h picks the mapping from the board selected in the IDE.
//
//  This is the pin-map verifier: run it on any new board or wiring before
//  trusting anything else. Point an 8-bit LED bar at the port's connector
//  (or at the raw Teensy pins for bare-silicon Stage 0 checks) and watch a
//  single lit bit walk from bit 0 (white) to bit 7 (red).
//
//  MENU (115200; the baud is decorative on the Teensy):
//    A B C D E : chase that port
//    X         : exit - blank every port, back to the menu
//    SPACE / S : pause / resume
//    N         : bounce mode on/off (sweep back down instead of wrapping)
//    +  /  -   : faster / slower (25 ms .. 800 ms per step)
//    0  /  F   : all bits off / all bits on (lamp test; pauses the chase)
//    ?         : status
//  SW0 pauses/resumes, SW1 toggles bounce.
//
//  BOARD SETUP (LVC harness board):
//    - Ports A, B, E drive only with their jumper FITTED: J8 (A),
//      J12 (B), J15 (E). Parked = the port reads and its header stays dark.
//    - Ports C and D are runtime-directional: this sketch calls
//      portDrive() when you select them and portRelease() when you leave,
//      so no jumper is involved.
//    - PE0 is the clock line: chasing E blinks the onboard LED on bit 0.
//    - PE6/PE7 only reach H11/H12 with J6/J7 in the port position.
//    - LED bar: power from the board's 5 V pair (H13/H14), grounds
//      commoned (H15/H16). Bare-Teensy instead: wire to the port's GPIOs
//      and power the bar from the Teensy's 3.3 V rail.
// ============================================================================

#include "AdapterPorts.h"

static const unsigned long stepMinMillis  = 25;
static const unsigned long stepMaxMillis  = 800;
static const unsigned long debounceMillis = 30;

static unsigned long stepMillis = 100;   // current chaser speed
static unsigned long lastStep   = 0;
static char          activePort = 0;     // 'A'..'E', 0 = at the menu
static uint8_t       chaserBit  = 0;     // 0..7
static bool          paused     = false;
static bool          bounce     = false;
static int8_t        direction  = 1;     // +1 up, -1 down (bounce mode)

static bool          lastSw0    = false;
static bool          lastSw1    = false;
static unsigned long lastSwEdge = 0;

void writeActivePort(uint8_t value) {
  switch (activePort) {
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
}

void printMenu() {
  Serial.println(F("Chaser menu: A B C D E = chase port, X = exit"));
  Serial.println(F("  SPACE/S pause, N bounce, +/- speed, 0 off, F on, ? status"));
}

void reportStatus() {
  if (activePort == 0) {
    Serial.println(F("At the menu."));
    return;
  }
  Serial.print(F("Port "));
  Serial.print(activePort);
  Serial.print(F(": "));
  Serial.print(paused ? F("paused") : F("running"));
  Serial.print(F(", bit "));
  Serial.print(chaserBit);
  Serial.print(F(", "));
  Serial.print(stepMillis);
  Serial.print(F(" ms/step, "));
  Serial.println(bounce ? F("bounce") : F("wrap"));
}

// PE6/PE7 are the same GPIOs as SW0/SW1 (40/41). While port E is driving,
// those pins are outputs and reading them just returns what the chaser is
// driving - which would look like button presses. Buttons are therefore
// ignored while E is active. On the board the same overlap exists whenever
// J6/J7 sit in the port position.
bool buttonsUsable() {
#if defined(__IMXRT1062__)
  return activePort != 'E';
#else
  return true;   // Mega: SW0/SW1 are separate pins (2/3)
#endif
}

// Put pins 40/41 back to button duty and re-arm the edge detector so the
// first poll after leaving E doesn't register a phantom press.
void rearmButtons() {
  pinMode(PIN_SW0, SW_PIN_MODE);
  pinMode(PIN_SW1, SW_PIN_MODE);
  lastSw0 = sw0Pressed();
  lastSw1 = sw1Pressed();
  lastSwEdge = millis();
}

// Ports A/B/E: the Teensy pins drive whenever this sketch runs; the board
// jumper decides whether that reaches the header. Ports C/D: take the bus
// through the direction API on the Teensy, plain pinMode on the Mega.
void claimPort(char port) {
  switch (port) {
    case 'A': setPortMode(PORT_A_PINS, OUTPUT); break;
    case 'B': setPortMode(PORT_B_PINS, OUTPUT); break;
    case 'E': setPortMode(PORT_E_PINS, OUTPUT); break;
    case 'C':
    case 'D':
#if defined(__IMXRT1062__)
      portDrive(port);
#else
      setPortMode(port == 'C' ? PORT_C_PINS : PORT_D_PINS, OUTPUT);
#endif
      break;
    default: break;
  }
}

void releasePort(char port) {
  switch (port) {
    case 'A': setPortMode(PORT_A_PINS, INPUT); break;
    case 'B': setPortMode(PORT_B_PINS, INPUT); break;
    case 'E':
      setPortMode(PORT_E_PINS, INPUT);
      rearmButtons();
      break;
    case 'C':
    case 'D':
#if defined(__IMXRT1062__)
      portRelease(port);
#else
      setPortMode(port == 'C' ? PORT_C_PINS : PORT_D_PINS, INPUT);
#endif
      break;
    default: break;
  }
}

void showBit(uint8_t bit) {
  writeActivePort((uint8_t)(1u << bit));
}

void advance() {
  if (bounce) {
    if (chaserBit == 7 && direction > 0) {
      direction = -1;
    } else if (chaserBit == 0 && direction < 0) {
      direction = 1;
    }
    chaserBit = (uint8_t)(chaserBit + direction);
  } else {
    chaserBit = (uint8_t)((chaserBit + 1) & 0x07);
  }
  showBit(chaserBit);
}

void selectPort(char port) {
  if (activePort != 0 && activePort != port) {
    writeActivePort(0);
    releasePort(activePort);
  }
  activePort = port;
  chaserBit  = 0;
  direction  = 1;
  paused     = false;
  claimPort(activePort);
  showBit(chaserBit);
  lastStep = millis();
  reportStatus();
}

void exitToMenu() {
  if (activePort != 0) {
    writeActivePort(0);
    releasePort(activePort);
  }
  allPortsOff();
  activePort = 0;
  Serial.println(F("Exited - all ports blank."));
  printMenu();
}

void handleCommand(char c) {
  if (c >= 'A' && c <= 'E') {
    selectPort(c);
    return;
  }
  switch (c) {
    case 'X':
      exitToMenu();
      break;
    case ' ':
    case 'S':
      if (activePort == 0) break;
      paused = !paused;
      if (!paused) lastStep = millis();
      reportStatus();
      break;
    case 'N':
      bounce = !bounce;
      direction = 1;
      reportStatus();
      break;
    case '+':
    case '=':
      if (stepMillis > stepMinMillis) {
        stepMillis /= 2;
        if (stepMillis < stepMinMillis) stepMillis = stepMinMillis;
      }
      reportStatus();
      break;
    case '-':
    case '_':
      if (stepMillis < stepMaxMillis) {
        stepMillis *= 2;
        if (stepMillis > stepMaxMillis) stepMillis = stepMaxMillis;
      }
      reportStatus();
      break;
    case '0':
      if (activePort == 0) break;
      paused = true;
      writeActivePort(0x00);
      Serial.println(F("Port = 0x00"));
      break;
    case 'F':
      if (activePort == 0) break;
      paused = true;
      writeActivePort(0xFF);
      Serial.println(F("Port = 0xFF (lamp test)"));
      break;
    case '?':
      reportStatus();
      break;
    default:
      break;  // CR/LF and anything else ignored
  }
}

void setup() {
  Serial.begin(115200);

#if defined(__IMXRT1062__)
  initPortDirections();   // C/D reading, DIR lines low, all ports inputs
#else
  setPortMode(PORT_A_PINS, INPUT);
  setPortMode(PORT_B_PINS, INPUT);
  setPortMode(PORT_C_PINS, INPUT);
  setPortMode(PORT_D_PINS, INPUT);
  setPortMode(PORT_E_PINS, INPUT);
#endif

  pinMode(PIN_SW0, SW_PIN_MODE);
  pinMode(PIN_SW1, SW_PIN_MODE);
  lastSw0 = sw0Pressed();
  lastSw1 = sw1Pressed();

  while (!Serial && millis() < 3000) { }
  Serial.print(F("PortChaser on "));
  Serial.println(F(PLATFORM_NAME));
  Serial.println(F("Jumpers: A=J8, B=J12, E=J15 must be FITTED to reach the headers"));
  printMenu();
}

void loop() {
  // Menu / commands - accepted at any time, running or not.
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c >= 'a' && c <= 'z') {
      c = (char)(c - 'a' + 'A');
    }
    handleCommand(c);
  }

  // Buttons: SW0 = pause/resume, SW1 = bounce. Skipped while port E owns
  // pins 40/41 (see buttonsUsable) - use the serial commands there.
  if (buttonsUsable() && (millis() - lastSwEdge >= debounceMillis)) {
    bool sw0 = sw0Pressed();
    if (sw0 != lastSw0) {
      lastSw0 = sw0;
      lastSwEdge = millis();
      if (sw0) handleCommand('S');
    }
    bool sw1 = sw1Pressed();
    if (sw1 != lastSw1) {
      lastSw1 = sw1;
      lastSwEdge = millis();
      if (sw1) handleCommand('N');
    }
  }

  // The chase itself.
  if (activePort != 0 && !paused && (millis() - lastStep >= stepMillis)) {
    lastStep = millis();
    advance();
  }
}
