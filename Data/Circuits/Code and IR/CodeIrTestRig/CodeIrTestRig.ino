// ============================================================
//  Code & IR Module — Arduino Mega standalone test rig
//  Fitment under test: 74HC377 in U3/U4 (synchronous load enable,
//  outputs always drive — all IR pins on the Mega stay INPUTs).
//
//  Wiring (Mega end connector, four 8-way ribbon cables — the end
//  header is 2x18, even digital pins in one row, odd in the other,
//  so each ribbon lands on 8 physically consecutive pins of a row):
//    Ribbon 1 (even row) D22,24,26,28,30,32,34,36 -> H1.1..H1.8  A0..A7
//    Ribbon 2 (odd row)  D23,25,27,29,31,33,35,37 -> H2.1..H2.8  A8..A15  ** REMOVE H6 SHUNT **
//    Ribbon 3 (even row) D38,40,42,44,46,48,50,52 <- H3.1..H3.8  IR0..IR7
//    Ribbon 4 (odd row)  D39,41,43,45,47,49,51,53 <- H4.1..H4.8  IR8..IR15
//    D2       -> H5.2         CP  (register clock)
//    D3       -> H5.3         /EN (synchronous load enable, active low)
//    GND      -> H5.1,  5V    -> H5.4
//
//  EEPROMs must be burned with U1_high.hex (U1) and U2_low.hex (U2),
//  produced by MakeTestHex.cs. Pattern:
//    addr  0..15 : walking one   (1 << a)
//    addr 16..31 : walking zero  (~(1 << (a-16)))
//    addr 32..35 : 0x0000, 0xFFFF, 0xAA55, 0x55AA
//    otherwise   : (addr * 0x9E37) ^ 0x55AA   (16-bit)
//
//  Open Serial Monitor at 115200. Tests run automatically;
//  send 'r' to run them again.
// ============================================================

const uint8_t AddrLowPins[8]  = {22, 24, 26, 28, 30, 32, 34, 36}; // A0..A7   (even row)
const uint8_t AddrHighPins[8] = {23, 25, 27, 29, 31, 33, 35, 37}; // A8..A15  (odd row)
const uint8_t IrLowPins[8]    = {38, 40, 42, 44, 46, 48, 50, 52}; // IR0..IR7 (even row)
const uint8_t IrHighPins[8]   = {39, 41, 43, 45, 47, 49, 51, 53}; // IR8..IR15 (odd row)

const uint8_t PinCP = 2;   // H5.2 — register clock, rising edge loads
const uint8_t PinEN = 3;   // H5.3 — /EN, sampled synchronously at the edge

uint32_t failCount = 0;
uint32_t testCount = 0;

// ---------- expected content (must match MakeTestHex.cs) ----------
uint16_t ExpectedWord(uint16_t addr)
{
  if (addr < 16)  return (uint16_t)(1u << addr);
  if (addr < 32)  return (uint16_t)~(1u << (addr - 16));
  if (addr == 32) return 0x0000;
  if (addr == 33) return 0xFFFF;
  if (addr == 34) return 0xAA55;
  if (addr == 35) return 0x55AA;
  return (uint16_t)((addr * 0x9E37u) ^ 0x55AAu);
}

// ---------- low-level pin helpers ----------
void SetAddress(uint16_t addr)
{
  for (uint8_t i = 0; i < 8; i++)
  {
    digitalWrite(AddrLowPins[i],  (addr >> i)       & 1);
    digitalWrite(AddrHighPins[i], (addr >> (8 + i)) & 1);
  }
  // EEPROM access 150 ns + register setup — one microsecond is
  // orders of magnitude more than the ~165 ns worst case.
  delayMicroseconds(2);
}

uint16_t ReadIR()
{
  uint16_t value = 0;
  for (uint8_t i = 0; i < 8; i++)
  {
    if (digitalRead(IrLowPins[i]))  value |= (uint16_t)1 << i;
    if (digitalRead(IrHighPins[i])) value |= (uint16_t)1 << (8 + i);
  }
  return value;
}

// Rising CP edge with /EN low: the '377 loads.
void ClockLoad()
{
  digitalWrite(PinEN, LOW);
  delayMicroseconds(1);          // /EN setup before the edge
  digitalWrite(PinCP, HIGH);
  delayMicroseconds(1);
  digitalWrite(PinCP, LOW);
  digitalWrite(PinEN, HIGH);     // park disabled between tests
  delayMicroseconds(1);          // register clock-to-Q ~35 ns; generous
}

// Rising CP edge with /EN high: the '377 must HOLD.
void ClockWithoutEnable()
{
  digitalWrite(PinEN, HIGH);
  delayMicroseconds(1);
  digitalWrite(PinCP, HIGH);
  delayMicroseconds(1);
  digitalWrite(PinCP, LOW);
  delayMicroseconds(1);
}

// ---------- reporting ----------
void Check(uint16_t addr, uint16_t expected, uint16_t actual, const char* label)
{
  testCount++;
  if (expected != actual)
  {
    failCount++;
    if (failCount <= 32)   // don't flood the port on a dead board
    {
      Serial.print(F("FAIL ["));
      Serial.print(label);
      Serial.print(F("] addr=0x"));
      Serial.print(addr, HEX);
      Serial.print(F(" expected=0x"));
      Serial.print(expected, HEX);
      Serial.print(F(" got=0x"));
      Serial.print(actual, HEX);
      Serial.print(F(" diff=0x"));
      Serial.println((uint16_t)(expected ^ actual), HEX);
    }
    else if (failCount == 33)
    {
      Serial.println(F("... further failures suppressed"));
    }
  }
}

// ---------- tests ----------
// 1. Full sweep, A15 = 0: burn-image compare of all 32K words.
//    Walking one/zero at 0..31 catch stuck or shorted IR lines;
//    the hash makes any address-line fault produce a mismatch.
void TestFullSweep()
{
  Serial.println(F("Test 1: full 32K sweep (A15=0)..."));
  for (uint32_t a = 0; a < 32768UL; a++)
  {
    SetAddress((uint16_t)a);
    ClockLoad();
    Check((uint16_t)a, ExpectedWord((uint16_t)a), ReadIR(), "sweep");
  }
}

// 2. Load-enable test: with /EN high, a clock edge must NOT load.
//    This is the property that makes the '377 usable as an IR.
void TestLoadEnable()
{
  Serial.println(F("Test 2: /EN gating (clock with /EN high must hold)..."));
  SetAddress(36); ClockLoad();
  uint16_t held = ReadIR();
  Check(36, ExpectedWord(36), held, "en-load");

  SetAddress(1234);            // new address on the EEPROMs...
  ClockWithoutEnable();        // ...clocked with /EN high
  Check(36, held, ReadIR(), "en-hold");
}

// 3. Hold test: after a load, changing the address with no clock
//    must not change IR. A '373 accidentally fitted (transparent)
//    fails here; a '377 passes.
void TestRegisteredHold()
{
  Serial.println(F("Test 3: registered hold (address change, no clock)..."));
  SetAddress(35); ClockLoad();          // 0x55AA
  uint16_t held = ReadIR();
  Check(35, 0x55AA, held, "hold-load");

  SetAddress(34);                       // 0xAA55 now at the ROM outputs
  delayMicroseconds(5);                 // no clock edge
  Check(35, held, ReadIR(), "hold");
}

// 4. A15 mirror test: A15 is not decoded, so 0x8000+a must read the
//    word at a. Requires the H6 shunt REMOVED. A stuck-low A15 wire
//    passes this trivially, so it complements — not replaces — test 1.
void TestA15Mirror()
{
  Serial.println(F("Test 4: A15 mirror (0x8000+a == a)..."));
  const uint16_t samples[] = {0, 1, 15, 16, 31, 33, 36, 100, 1234, 4096, 20000, 32767};
  for (uint8_t i = 0; i < sizeof(samples) / sizeof(samples[0]); i++)
  {
    uint16_t a = samples[i];
    SetAddress(a); ClockLoad();
    uint16_t low = ReadIR();
    SetAddress(a | 0x8000); ClockLoad();
    Check(a, low, ReadIR(), "a15-mirror");
  }
}

// 5. Per-address-line uniqueness: for each address bit, the word at
//    (1 << bit) must differ from the word at 0 — a quick targeted
//    check that every address wire H1/H2 actually reaches the ROMs.
void TestAddressLines()
{
  Serial.println(F("Test 5: address line walk..."));
  SetAddress(0); ClockLoad();
  uint16_t atZero = ReadIR();
  Check(0, ExpectedWord(0), atZero, "aline-base");
  for (uint8_t bit = 0; bit < 15; bit++)
  {
    uint16_t a = (uint16_t)1 << bit;
    SetAddress(a); ClockLoad();
    uint16_t w = ReadIR();
    Check(a, ExpectedWord(a), w, "aline");
    testCount++;
    if (w == atZero)
    {
      failCount++;
      Serial.print(F("FAIL [aline-stuck] A"));
      Serial.print(bit);
      Serial.println(F(" appears stuck (word matches address 0)"));
    }
  }
}

void RunAllTests()
{
  failCount = 0;
  testCount = 0;
  Serial.println(F("=== Code & IR module test ('377 fitment) ==="));
  Serial.println(F("Reminder: H6 shunt must be REMOVED (Mega drives A15)."));

  TestAddressLines();
  TestLoadEnable();
  TestRegisteredHold();
  TestA15Mirror();
  TestFullSweep();

  Serial.print(F("=== Done: "));
  Serial.print(testCount);
  Serial.print(F(" checks, "));
  Serial.print(failCount);
  Serial.println(failCount == 0 ? F(" failures — PASS ===") : F(" failures — FAIL ==="));
  Serial.println(F("Send 'r' to run again."));
}

void setup()
{
  Serial.begin(115200);

  // IR pins first, as plain inputs — the '377s are already driving them.
  for (uint8_t i = 0; i < 8; i++)
  {
    pinMode(IrLowPins[i],  INPUT);
    pinMode(IrHighPins[i], INPUT);
  }

  // Controls parked safe before anything toggles: /EN high, CP low.
  digitalWrite(PinEN, HIGH); pinMode(PinEN, OUTPUT);
  digitalWrite(PinCP, LOW);  pinMode(PinCP, OUTPUT);

  // Address bus.
  for (uint8_t i = 0; i < 8; i++)
  {
    pinMode(AddrLowPins[i],  OUTPUT);
    pinMode(AddrHighPins[i], OUTPUT);
  }
  SetAddress(0);

  delay(500);
  RunAllTests();
}

void loop()
{
  if (Serial.available() && Serial.read() == 'r')
  {
    RunAllTests();
  }
}
