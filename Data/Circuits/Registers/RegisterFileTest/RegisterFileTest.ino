/*
 * RegisterFileTest.ino
 * Arduino Mega 2560 test harness for the Register File Module (74HC670 board).
 *
 * FULL POPULATION: all 16 '670s stuffed (Thumby configuration).
 *   8 registers x 16 bits, two independent read ports.
 *
 * Assumed socket layout (row-major, corroborated during 8x8 bring-up but
 * not formally proven — the suspect-chip report depends on it):
 *
 *            bits 3:0   bits 7:4   bits 11:8  bits 15:12
 *   primary  bank 0   U1         U2         U3         U4
 *            bank 1   U5         U6         U7         U8
 *   mirror   bank 0   U9         U10        U11        U12
 *            bank 1   U13        U14        U15        U16
 *
 *   Bank 0 holds r0..r3, bank 1 holds r4..r7 (address bit 2 selects).
 *   Primary array feeds port A (QA); mirror array feeds port B (QB).
 *   U17/U18 '139 bank decoders, U19 '00 write gating.
 *
 * ------------------- SN74LS670 PINOUT (datasheet-verified) -------------
 *   16 Vcc                        8 GND
 *   15 D1                         1 D2
 *   14 WA  write addr LSB         2 D3
 *   13 WB  write addr MSB         3 D4
 *   12 E_W write enable           4 RB  read addr MSB
 *   11 E_R read enable            5 RA  read addr LSB
 *   10 Q1                         6 Q4
 *    9 Q2                         7 Q3
 *
 * Every multi-bit group on this part runs LSB-on-the-HIGHER pin:
 * WA(14)>WB(13), RA(5)>RB(4), Q1(10)>Q2(9), Q3(7)>Q4(6). D1 is orphaned
 * at pin 15 while D2..D4 sit at 1..3.
 *
 * ------------------------- READING A FAILURE ---------------------------
 * Ports A and B are served by physically different chips. Therefore:
 *
 *   bit fails on BOTH ports  -> NOT a '670 output. The fault is upstream
 *                               of the array split: the D input for that
 *                               bit, or WADDR / WE / CLK. A floating D
 *                               line is latched into the primary and
 *                               mirror copies alike, so both ports report
 *                               the same wrong bit.
 *   bit fails on port A only -> primary array (U1..U8)
 *   bit fails on port B only -> mirror array  (U9..U16)
 *
 * Register numbers then pick the bank: r0..r3 = bank 0, r4..r7 = bank 1.
 * Bit/4 picks the nibble column. The report does this arithmetic below.
 *
 * An INTERMITTENT wrong bit — mostly high, occasionally holding a stale
 * low — indicates an OPEN, not a short: this board has no Q-bus
 * pulldowns, so an undriven line keeps whatever charge it last had.
 * A stuck line reads consistently.
 *
 * ------------------------- FAULT HISTORY -------------------------------
 * All three faults found during bring-up were CONNECTIONS. Not one was a
 * faulty component — and the one chip that was swapped on suspicion was
 * innocent, with the swap itself disturbing the loom and introducing
 * fault (2). Suspect joints and crimps before silicon.
 *
 * (1) Write/read address bit0/bit1 disagreement (r1<->r2, r5<->r6):
 *     the WADDR chunk was transposed inside the connector housing at the
 *     MEGA end. It survived repeated visual inspection because the
 *     green-wire-is-bit-0 convention was doing the verifying — the green
 *     wire really did reach WADDR0 on the board, it just did not start
 *     at pin 4.
 *
 * (2) Bit 8 wrong on both ports, every register: the D8..D15 ribbon was
 *     reversed. Appeared immediately after the board was handled.
 *
 * (3) Bit 0 wrong on port B, bank 1 only: U13 pin 10 (Q1) was never
 *     soldered — a bare pad with the hole still open, in a row of good
 *     fillets.
 *
 * DIAGNOSTIC RULES EARNED HERE
 *   - A bit wrong on BOTH ports is never a single '670 output; the two
 *     ports are different silicon. Look upstream (D, WE, CLK, WADDR) or
 *     at whatever the two Q buses share.
 *   - A bit wrong in both directions — sometimes high when 0 was
 *     expected, sometimes low when 1 was — is an OPEN. This board has no
 *     Q-bus pulldowns, so an undriven node keeps residual charge. A short
 *     to a rail is consistent in one direction only.
 *   - Which registers fail picks the bank (r0..r3 bank 0, r4..r7 bank 1);
 *     bit/4 picks the nibble column; the port picks primary or mirror.
 *     Together they name one chip.
 *   - Ribbon order: compare each chunk's conductor colours against the
 *     others rather than tracing a signal name back from the board.
 *     Tracing a name confirms the naming convention, not the wiring.
 *     Decisive check is the d / w / a / b / s probe on a physical pin.
 *
 * The harness NEVER compensates for board faults. Addresses and data go
 * out exactly as asked for.
 *
 * ------------------------- PROBE MODE ----------------------------------
 * Serial commands (115200, newline-terminated):
 *
 *   w<n>     hold WADDR = n (0..7)   then meter U1 pin 14 (WA) / 13 (WB)
 *   a<n>     hold AADDR = n (0..7)   then meter U1 pin  5 (RA) /  4 (RB)
 *   b<n>     hold BADDR = n (0..7)   then meter U9 pin  5 (RA) /  4 (RB)
 *   d<hex>   hold the D bus at a pattern, e.g. "d100" for bit 8 only
 *   s<r>:<hex>  store a value then park both read addresses on it, so
 *               the '670 Q pins can be metered against what the Mega
 *               reports, e.g. "s5:0000"
 *   q        print QA and QB as currently read
 *   r        run the test suite
 *   ?        help
 *
 * BUILT-IN LED (pin 13, not part of the loom):
 *   blinking = tests running   solid = all pass   dark = failures
 *   Useful during the exhaustive sweep, which runs silently for minutes.
 *   Remaining spare pins: 2, 3, 8, 9, 10, 11.
 *
 * To chase a suspect data bit: send "d100", then meter J1 D8 and the
 * D-pin of the chips carrying bits 11:8 (U3, U7, U11, U15). The level
 * should be HIGH at every one of them.
 */

/* ------------------------- BOARD CONFIGURATION ------------------------- */

const uint8_t  REG_COUNT   = 8;      /* two banks of four                 */
const uint8_t  DATA_WIDTH  = 16;     /* four nibble columns stuffed       */
const bool     HAS_PORT_B  = true;   /* mirror array complete             */
const bool     QB_WIRED    = true;

const bool     BOARD_HAS_PULLDOWNS = false;

const bool     DRIVE_OEA   = true;
const bool     DRIVE_OEB   = true;

const uint8_t  MAX_FAILS_SHOWN = 3;
const uint16_t RANDOM_VALUES   = 512;   /* LFSR values per register */

/* Exhaustive sweep of every value in every register. Tractable now that
 * the counter is 32-bit: 8 x 65536 cycles, roughly two minutes at 16
 * bits. Off by default because the random sweep catches the same faults
 * in a couple of seconds. */
const bool     RUN_EXHAUSTIVE  = true;

/* ------------------------------- PINS ---------------------------------- */

/* Dual-row header, even column: QA bit n on pin 22 + 2n */
const uint8_t qaPins[16] = {22,24,26,28,30,32,34,36,38,40,42,44,46,48,50,52};

/* Dual-row header, odd column: QB bit n on pin 23 + 2n */
const uint8_t qbPins[16] = {23,25,27,29,31,33,35,37,39,41,43,45,47,49,51,53};

/* Analog header as digital outputs: pins 54..69 == A0..A15 */
const uint8_t dataPins[16] = {54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69};

const uint8_t waddrPins[3] = {4, 5, 6};
const uint8_t aaddrPins[3] = {14, 15, 16};
const uint8_t baddrPins[3] = {18, 19, 20};

const uint8_t pinWE  = 7;
const uint8_t pinCLK = 12;
const uint8_t pinOEA = 17;
const uint8_t pinOEB = 21;

/* Progress heartbeat on the Mega's built-in LED (pin 13, otherwise a
 * spare). Toggles every LED_TOGGLE_EVERY checks; at roughly 200 us per
 * check that gives about a 10 Hz blink — steady flicker means running.
 * At the end of a run the LED is left ON for all-pass, OFF for failures,
 * so the result is readable without the serial monitor. */
const uint8_t  pinLed = LED_BUILTIN;
const uint16_t LED_TOGGLE_EVERY = 256;

const uint8_t ADDR_BITS = 3;

/* ----------------------------- DERIVED --------------------------------- */

const uint16_t valueMask = (DATA_WIDTH >= 16) ? 0xFFFF
                          : (uint16_t)((1u << DATA_WIDTH) - 1);
const uint16_t readMask  = BOARD_HAS_PULLDOWNS ? 0xFFFF : valueMask;

/* 32-bit: an exhaustive 16-bit sweep is 524288 checks, which overflows a
 * 16-bit counter and reports nonsense. */
uint32_t failCount = 0;
uint32_t testCount = 0;

/* Fault attribution, tracked separately per port. */
uint16_t regFailA = 0, bitFailA = 0;
uint16_t regFailB = 0, bitFailB = 0;

/* Per-bit register masks: regFailByBitA[n] holds the registers whose
 * port-A readback differed in bit n. Pooling registers across all bits
 * smears a single wide fault over the whole array, which is exactly how
 * a bit-8 fault made an innocent U9 look guilty. */
uint16_t regFailByBitA[16];
uint16_t regFailByBitB[16];

const char* sectionTag = "";
uint32_t sectionChecks = 0;
uint32_t sectionFails  = 0;

/* --------------------------- BUS HELPERS -------------------------------- */

void setBus(const uint8_t pins[], uint8_t count, uint16_t value) {
  for (uint8_t i = 0; i < count; i++) {
    digitalWrite(pins[i], (value >> i) & 1 ? HIGH : LOW);
  }
}

uint16_t readBus(const uint8_t pins[], uint8_t count) {
  uint16_t value = 0;
  for (uint8_t i = 0; i < count; i++) {
    if (digitalRead(pins[i]) == HIGH) value |= (uint16_t)1 << i;
  }
  return value;
}

/* Exchange bits 0 and 1 of a register number, leaving bit 2 alone.
 * Used ONLY to classify an observed fault — never to alter output. */
uint8_t swapLowBits(uint8_t reg) {
  return (uint8_t)((reg & 0b100) | ((reg & 1) << 1) | ((reg >> 1) & 1));
}

/* Maximal-length 16-bit Fibonacci LFSR, taps 16/14/13/11. */
uint16_t lfsrNext(uint16_t v) {
  uint16_t bit = (uint16_t)((v ^ (v >> 2) ^ (v >> 3) ^ (v >> 5)) & 1u);
  return (uint16_t)((v >> 1) | (bit << 15));
}

/* A distinct, wide, bit-spreading value for each register. */
uint16_t regPattern(uint8_t reg, uint16_t salt) {
  return (uint16_t)(((uint16_t)(reg + 1) * 0x1111u) ^ salt) & valueMask;
}

/* ------------------------ MODULE OPERATIONS ----------------------------- */

void writeRegister(uint8_t reg, uint16_t value) {
  setBus(dataPins, 16, value);
  setBus(waddrPins, ADDR_BITS, reg);
  delayMicroseconds(2);
  digitalWrite(pinWE, HIGH);
  digitalWrite(pinCLK, LOW);         /* write window opens */
  delayMicroseconds(2);
  digitalWrite(pinCLK, HIGH);        /* write committed */
  digitalWrite(pinWE, LOW);
}

void clockWithoutWE(uint8_t reg, uint16_t value) {
  setBus(dataPins, 16, value);
  setBus(waddrPins, ADDR_BITS, reg);
  delayMicroseconds(2);
  digitalWrite(pinCLK, LOW);
  delayMicroseconds(2);
  digitalWrite(pinCLK, HIGH);
}

uint16_t readPortA(uint8_t reg) {
  setBus(aaddrPins, ADDR_BITS, reg);
  delayMicroseconds(2);              /* covers bank-handover worst case */
  return readBus(qaPins, 16) & readMask;
}

uint16_t readPortB(uint8_t reg) {
  setBus(baddrPins, ADDR_BITS, reg);
  delayMicroseconds(2);
  return readBus(qbPins, 16) & readMask;
}

/* ---------------------------- REPORTING --------------------------------- */

void beginSection(const char* tag) {
  sectionTag = tag;
  sectionChecks = 0;
  sectionFails = 0;
}

void endSection() {
  Serial.print(F("  "));
  Serial.print(sectionTag);
  Serial.print(F("\t"));
  if (sectionFails == 0) {
    Serial.print(F("OK   "));
    Serial.print(sectionChecks);
    Serial.println(F(" checks"));
  } else {
    Serial.print(F("FAIL "));
    Serial.print(sectionFails);
    Serial.print('/');
    Serial.println(sectionChecks);
  }
}

/* Heartbeat. Called once per check — the one funnel every test uses —
 * so the blink rate is a direct indication that work is happening. */
void ledTick() {
  static uint16_t counter = 0;
  static bool state = false;
  if (++counter >= LED_TOGGLE_EVERY) {
    counter = 0;
    state = !state;
    digitalWrite(pinLed, state ? HIGH : LOW);
  }
}

void checkPort(bool ok, char port, uint8_t reg,
               uint16_t expected, uint16_t got) {
  testCount++;
  sectionChecks++;
  ledTick();
  if (ok) return;

  failCount++;
  sectionFails++;
  uint16_t diff = (uint16_t)((expected ^ got) & readMask);
  if (port == 'B') {
    regFailB |= (uint16_t)1 << reg;
    bitFailB |= diff;
    for (uint8_t bit = 0; bit < 16; bit++) {
      if (diff & ((uint16_t)1 << bit)) regFailByBitB[bit] |= (uint16_t)1 << reg;
    }
  } else {
    regFailA |= (uint16_t)1 << reg;
    bitFailA |= diff;
    for (uint8_t bit = 0; bit < 16; bit++) {
      if (diff & ((uint16_t)1 << bit)) regFailByBitA[bit] |= (uint16_t)1 << reg;
    }
  }

  if (sectionFails <= MAX_FAILS_SHOWN) {
    Serial.print(F("    ["));
    Serial.print(sectionTag);
    Serial.print(F("] "));
    Serial.print(port);
    Serial.print(F(" r"));
    Serial.print(reg);
    Serial.print(F(" exp=0x"));
    Serial.print(expected, HEX);
    Serial.print(F(" got=0x"));
    Serial.println(got, HEX);
  }
}

void check(bool ok, uint8_t reg, uint16_t expected, uint16_t got) {
  checkPort(ok, 'A', reg, expected, got);
}

/* Name the chips serving the failing (bit, bank) combinations of one
 * array. base 1 = primary (U1..U8), base 9 = mirror (U9..U16). Uses the
 * per-bit register masks, so a bit that fails only in bank 1 names only
 * the bank 1 chip. */
void reportArray(const char* label, uint16_t bits,
                 const uint16_t regByBit[], uint8_t base) {
  if (bits == 0) return;
  uint16_t chips = 0;              /* bit n set => U(base + n) */
  for (uint8_t bit = 0; bit < 16; bit++) {
    if ((bits & ((uint16_t)1 << bit)) == 0) continue;
    uint8_t column = bit / 4;
    uint16_t regs = regByBit[bit];
    if (regs & 0x000F) chips |= (uint16_t)1 << column;         /* bank 0 */
    if (regs & 0x00F0) chips |= (uint16_t)1 << (column + 4);   /* bank 1 */
  }
  Serial.print(F("  "));
  Serial.print(label);
  Serial.print(F("  bits: 0x"));
  Serial.print(bits, HEX);
  Serial.print(F("  suspect: "));
  for (uint8_t i = 0; i < 8; i++) {
    if (chips & ((uint16_t)1 << i)) {
      Serial.print('U');
      Serial.print(base + i);
      Serial.print(' ');
    }
  }
  Serial.println();
}

/* Cross-port correlation. A bit failing on BOTH ports cannot be a '670
 * output, because the two ports are different silicon — it has to be
 * upstream of the split, OR common to both Q buses downstream (the same
 * bit index on each row of the header, for instance). */
void reportDiagnosis() {
  uint16_t shared = bitFailA & bitFailB;
  uint16_t onlyA  = bitFailA & ~shared;
  uint16_t onlyB  = bitFailB & ~shared;

  if (shared) {
    Serial.println(F("  SHARED across both ports — not a single '670 output."));
    Serial.print(F("    bits: "));
    for (uint8_t bit = 0; bit < 16; bit++) {
      if (shared & ((uint16_t)1 << bit)) {
        Serial.print(bit); Serial.print(' ');
      }
    }
    Serial.println();
    Serial.println(F("    Upstream candidates: that D line, WE, CLK, WADDR."));
    Serial.println(F("    Downstream candidates: QA and QB of the same bit"));
    Serial.println(F("    index, which are a facing pin pair on the header"));
    Serial.println(F("    (QA n = 22+2n, QB n = 23+2n) and, at bit 8, the"));
    Serial.println(F("    first conductor of the second chunk on both rows."));
  }

  reportArray("PORT A only — primary array.", onlyA, regFailByBitA, 1);
  reportArray("PORT B only — mirror array. ", onlyB, regFailByBitB, 9);
}

/* ------------------------------ PROBE MODE ------------------------------ */

void reportHeld(const char* what, const uint8_t pins[], uint8_t value) {
  Serial.print(F("HOLD "));
  Serial.print(what);
  Serial.print(F(" = "));
  Serial.print(value);
  Serial.print(F("  ->  "));
  for (uint8_t i = 0; i < ADDR_BITS; i++) {
    Serial.print(F("pin"));
    Serial.print(pins[i]);
    Serial.print('=');
    Serial.print(digitalRead(pins[i]) == HIGH ? F("HI") : F("LO"));
    Serial.print(F("  "));
  }
  Serial.println();
}

void probeExpect(const char* chip, uint8_t value,
                 const char* lsbName, const char* msbName) {
  Serial.print(F("  expect at "));
  Serial.print(chip);
  Serial.print(F(":  "));
  Serial.print(lsbName);
  Serial.print('=');
  Serial.print((value & 1) ? F("HI") : F("LO"));
  Serial.print(F("   "));
  Serial.print(msbName);
  Serial.print('=');
  Serial.println((value & 2) ? F("HI") : F("LO"));
  Serial.println(F("  (bit2 goes to the '139 decoder, not to the '670)"));
}

void probeWrite(uint8_t value) {
  value &= 0x07;
  setBus(waddrPins, ADDR_BITS, value);
  reportHeld("WADDR", waddrPins, value);
  probeExpect("U1", value, "pin14 WA", "pin13 WB");
}

void probeReadA(uint8_t value) {
  value &= 0x07;
  setBus(aaddrPins, ADDR_BITS, value);
  reportHeld("AADDR", aaddrPins, value);
  probeExpect("U1", value, "pin5 RA", "pin4 RB");
}

void probeReadB(uint8_t value) {
  value &= 0x07;
  setBus(baddrPins, ADDR_BITS, value);
  reportHeld("BADDR", baddrPins, value);
  probeExpect("U9", value, "pin5 RA", "pin4 RB");
}

/* Hold a pattern on the D bus so individual data lines can be metered
 * at J1 and at each '670's D pins. */
void probeData(uint16_t value) {
  setBus(dataPins, 16, value);
  Serial.print(F("HOLD D = 0x"));
  Serial.println(value, HEX);
  Serial.print(F("  HIGH lines: "));
  bool any = false;
  for (uint8_t bit = 0; bit < 16; bit++) {
    if (value & ((uint16_t)1 << bit)) {
      Serial.print('D'); Serial.print(bit);
      Serial.print(F("(pin")); Serial.print(dataPins[bit]); Serial.print(F(") "));
      any = true;
    }
  }
  if (!any) Serial.print(F("none"));
  Serial.println();
  Serial.println(F("  Meter J1, then the D pins of the chips for that column:"));
  Serial.println(F("    bits 3:0 U1/U5/U9/U13   bits 7:4  U2/U6/U10/U14"));
  Serial.println(F("    bits 11:8 U3/U7/U11/U15 bits 15:12 U4/U8/U12/U16"));
}

void probeQ() {
  uint16_t qa = readBus(qaPins, 16);
  uint16_t qb = readBus(qbPins, 16);
  Serial.print(F("QA = 0x"));
  Serial.print(qa, HEX);
  Serial.print(F("   QB = 0x"));
  Serial.println(qb, HEX);
}

/* Write a value to a register, then hold BOTH read addresses on it and
 * report what comes back. Leaves the address lines parked so the '670 Q
 * pins can be metered against the value the Mega reports. */
void probeStore(const char* arg) {
  const char* sep = strchr(arg, ':');
  if (sep == NULL) {
    Serial.println(F("usage: s<reg>:<hex>   e.g. s0:0000"));
    return;
  }
  uint8_t reg = (uint8_t)(atoi(arg) & 0x07);
  uint16_t value = (uint16_t)strtoul(sep + 1, NULL, 16);

  writeRegister(reg, value);
  setBus(aaddrPins, ADDR_BITS, reg);
  setBus(baddrPins, ADDR_BITS, reg);
  delayMicroseconds(2);
  uint16_t qa = readBus(qaPins, 16);
  uint16_t qb = readBus(qbPins, 16);

  Serial.print(F("STORE r"));
  Serial.print(reg);
  Serial.print(F(" = 0x"));
  Serial.print(value, HEX);
  Serial.print(F("   QA=0x"));
  Serial.print(qa, HEX);
  Serial.print(F("   QB=0x"));
  Serial.println(qb, HEX);

  uint16_t diffA = (uint16_t)(qa ^ value) & readMask;
  uint16_t diffB = (uint16_t)(qb ^ value) & readMask;
  if (diffA || diffB) {
    Serial.print(F("  wrong bits  QA: 0x"));
    Serial.print(diffA, HEX);
    Serial.print(F("   QB: 0x"));
    Serial.println(diffB, HEX);
    Serial.println(F("  Addresses are parked — meter the '670 Q pins now."));
    Serial.println(F("    bit%4: 0->pin10 Q1  1->pin9 Q2  2->pin7 Q3  3->pin6 Q4"));
  } else {
    Serial.println(F("  matches."));
  }
}

void printHelp() {
  Serial.println(F("w<n> WADDR   a<n> AADDR   b<n> BADDR   (0..7)"));
  Serial.println(F("d<hex> hold D bus, e.g. d100 = bit 8 only"));
  Serial.println(F("s<reg>:<hex> store then park addresses, e.g. s0:0000"));
  Serial.println(F("q read QA/QB    r run suite    ? help"));
}

/* ------------------------------ TESTS ----------------------------------- */

void testAddressOrientation() {
  beginSection("orient");
  uint16_t gotA[16], gotB[16];
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    writeRegister(reg, regPattern(reg, 0));
  }
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    gotA[reg] = readPortA(reg);
    if (HAS_PORT_B) gotB[reg] = readPortB(reg);
  }
  for (uint8_t pass = 0; pass < (HAS_PORT_B ? 2 : 1); pass++) {
    const uint16_t* got = (pass == 0) ? gotA : gotB;
    bool straight = true, swapped = true;
    for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
      if (got[reg] != regPattern(reg, 0)) straight = false;
      if (got[reg] != regPattern(swapLowBits(reg), 0)) swapped = false;
    }
    Serial.print(F("  orient\tport "));
    Serial.print(pass == 0 ? 'A' : 'B');
    Serial.print(F("  "));
    if (straight) {
      Serial.println(F("MATCH"));
    } else if (swapped) {
      Serial.println(F("SWAP bit0/bit1 (r1<->r2, r5<->r6)"));
    } else {
      /* Not an address fault — most likely stuck or open data bits.
       * Show the OR of all differences, which names them directly. */
      uint16_t diff = 0;
      for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
        diff |= (uint16_t)(got[reg] ^ regPattern(reg, 0)) & readMask;
      }
      Serial.print(F("addressing OK, data bits differ: 0x"));
      Serial.println(diff, HEX);
      for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
        if (got[reg] == regPattern(reg, 0)) continue;
        Serial.print(F("    r"));
        Serial.print(reg);
        Serial.print(F(" wrote=0x"));
        Serial.print(regPattern(reg, 0), HEX);
        Serial.print(F(" reads=0x"));
        Serial.println(got[reg], HEX);
      }
    }
  }
}

void testWalkingBits() {
  beginSection("walk");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    for (uint8_t bit = 0; bit < DATA_WIDTH; bit++) {
      uint16_t one  = (uint16_t)1 << bit;
      uint16_t zero = (~one) & valueMask;

      writeRegister(reg, one);
      uint16_t gotA1 = readPortA(reg);
      check(gotA1 == one, reg, one, gotA1);
      if (HAS_PORT_B) {
        uint16_t gotB1 = readPortB(reg);
        checkPort(gotB1 == one, 'B', reg, one, gotB1);
      }

      writeRegister(reg, zero);
      uint16_t gotA0 = readPortA(reg);
      check(gotA0 == zero, reg, zero, gotA0);
      if (HAS_PORT_B) {
        uint16_t gotB0 = readPortB(reg);
        checkPort(gotB0 == zero, 'B', reg, zero, gotB0);
      }
    }
  }
  endSection();
}

void testNibbleColumns() {
  beginSection("nibble");
  const uint16_t patterns[] = {
    0x000F, 0x00F0, 0x0F00, 0xF000,
    0xFFF0, 0xFF0F, 0xF0FF, 0x0FFF,
    0x0000, 0xFFFF, 0xAAAA, 0x5555
  };
  const uint8_t count = sizeof(patterns) / sizeof(patterns[0]);
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    for (uint8_t i = 0; i < count; i++) {
      uint16_t want = patterns[i] & valueMask;
      writeRegister(reg, want);
      uint16_t gotA = readPortA(reg);
      check(gotA == want, reg, want, gotA);
      if (HAS_PORT_B) {
        uint16_t gotB = readPortB(reg);
        checkPort(gotB == want, 'B', reg, want, gotB);
      }
    }
  }
  endSection();
}

void testPseudoRandom() {
  beginSection("random");
  uint16_t seed = 0xACE1;
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t v = seed;
    for (uint16_t i = 0; i < RANDOM_VALUES; i++) {
      v = lfsrNext(v);
      uint16_t want = v & valueMask;
      writeRegister(reg, want);
      uint16_t gotA = readPortA(reg);
      check(gotA == want, reg, want, gotA);
    }
    seed = lfsrNext(seed);
  }
  endSection();
}

/* Every value in every register. 32-bit counter: (uint16_t)1 << 16 is
 * zero, which silently ran no iterations at all in the previous version. */
void testExhaustiveValues() {
  if (!RUN_EXHAUSTIVE) return;
  Serial.print(F("  sweep\trunning "));
  Serial.print((uint32_t)REG_COUNT * (1UL << DATA_WIDTH));
  Serial.println(F(" cycles — LED blinks while busy"));
  beginSection("sweep");
  uint32_t top = 1UL << DATA_WIDTH;
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    for (uint32_t v = 0; v < top; v++) {
      uint16_t want = (uint16_t)v & valueMask;
      writeRegister(reg, want);
      uint16_t got = readPortA(reg);
      check(got == want, reg, want, got);
    }
  }
  endSection();
}

void testAddressUniqueness() {
  beginSection("uniq");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    writeRegister(reg, regPattern(reg, 0x0F0F));
  }
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t want = regPattern(reg, 0x0F0F);
    uint16_t gotA = readPortA(reg);
    check(gotA == want, reg, want, gotA);
    if (HAS_PORT_B) {
      uint16_t gotB = readPortB(reg);
      checkPort(gotB == want, 'B', reg, want, gotB);
    }
  }
  endSection();
}

void testBankCrossing() {
  if (REG_COUNT <= 4) return;
  beginSection("bank");
  const uint16_t lowBank  = (uint16_t)0x5AA5 & valueMask;
  const uint16_t highBank = (uint16_t)0xA55A & valueMask;
  writeRegister(3, lowBank);
  writeRegister(4, highBank);
  for (uint8_t pass = 0; pass < 8; pass++) {
    uint16_t gotLow = readPortA(3);
    check(gotLow == lowBank, 3, lowBank, gotLow);
    uint16_t gotHigh = readPortA(4);
    check(gotHigh == highBank, 4, highBank, gotHigh);
    if (HAS_PORT_B) {
      uint16_t bLow = readPortB(3);
      checkPort(bLow == lowBank, 'B', 3, lowBank, bLow);
      uint16_t bHigh = readPortB(4);
      checkPort(bHigh == highBank, 'B', 4, highBank, bHigh);
    }
  }
  endSection();
}

void testRetention() {
  beginSection("retain");
  for (uint8_t victim = 0; victim < REG_COUNT; victim++) {
    uint16_t keep = regPattern(victim, 0x3C5A);
    writeRegister(victim, keep);
    for (uint8_t other = 0; other < REG_COUNT; other++) {
      if (other == victim) continue;
      writeRegister(other, (uint16_t)(~keep) & valueMask);
      writeRegister(other, regPattern(other, 0x55AA));
    }
    uint16_t got = readPortA(victim);
    check(got == keep, victim, keep, got);
  }
  endSection();
}

void testWriteEnableGating() {
  beginSection("wegate");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t keep = regPattern(reg, 0x69C3);
    writeRegister(reg, keep);
    clockWithoutWE(reg, (uint16_t)(~keep) & valueMask);
    uint16_t got = readPortA(reg);
    check(got == keep, reg, keep, got);
  }
  endSection();
}

void testUnpopulatedBitsReadZero() {
  if (!BOARD_HAS_PULLDOWNS || DATA_WIDTH >= 16) return;
  beginSection("zeroext");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    writeRegister(reg, 0xFFFF);
    uint16_t got = readBus(qaPins, 16);
    check(got == valueMask, reg, valueMask, got);
  }
  endSection();
}

void testPortBAbsent() {
  if (HAS_PORT_B || !QB_WIRED || !BOARD_HAS_PULLDOWNS) return;
  beginSection("bzero");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    writeRegister(reg, valueMask);
    uint16_t got = readPortB(reg);
    checkPort(got == 0, 'B', reg, 0, got);
  }
  endSection();
}

void testPortBMirror() {
  if (!HAS_PORT_B) return;
  beginSection("mirror");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    writeRegister(reg, regPattern(reg, 0xC33C));
  }
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t want = regPattern(reg, 0xC33C);
    uint16_t gotA = readPortA(reg);
    uint16_t gotB = readPortB(reg);
    check(gotA == want, reg, want, gotA);
    checkPort(gotB == want, 'B', reg, want, gotB);
  }
  endSection();
}

void testDualPortIndependence() {
  if (!HAS_PORT_B) return;
  beginSection("dualport");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    writeRegister(reg, regPattern(reg, 0x1234));
  }
  for (uint8_t a = 0; a < REG_COUNT; a++) {
    uint8_t b = (uint8_t)(REG_COUNT - 1 - a);
    setBus(aaddrPins, ADDR_BITS, a);
    setBus(baddrPins, ADDR_BITS, b);
    delayMicroseconds(2);
    uint16_t gotA = readBus(qaPins, 16) & readMask;
    uint16_t gotB = readBus(qbPins, 16) & readMask;
    check(gotA == regPattern(a, 0x1234), a, regPattern(a, 0x1234), gotA);
    checkPort(gotB == regPattern(b, 0x1234), 'B', b,
              regPattern(b, 0x1234), gotB);
  }
  endSection();
}

void testConcurrentAccess() {
  if (!HAS_PORT_B) return;
  beginSection("concur");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    writeRegister(reg, regPattern(reg, 0x7788));
  }
  for (uint8_t target = 0; target < REG_COUNT; target++) {
    uint8_t watchA = (uint8_t)((target + 1) % REG_COUNT);
    uint8_t watchB = (uint8_t)((target + 2) % REG_COUNT);
    uint16_t fresh = (uint16_t)(~regPattern(target, 0x7788)) & valueMask;

    setBus(aaddrPins, ADDR_BITS, watchA);
    setBus(baddrPins, ADDR_BITS, watchB);
    setBus(dataPins, 16, fresh);
    setBus(waddrPins, ADDR_BITS, target);
    delayMicroseconds(2);
    digitalWrite(pinWE, HIGH);
    digitalWrite(pinCLK, LOW);          /* window open */
    delayMicroseconds(2);
    uint16_t gotA = readBus(qaPins, 16) & readMask;
    uint16_t gotB = readBus(qbPins, 16) & readMask;
    digitalWrite(pinCLK, HIGH);
    digitalWrite(pinWE, LOW);

    check(gotA == regPattern(watchA, 0x7788), watchA,
          regPattern(watchA, 0x7788), gotA);
    checkPort(gotB == regPattern(watchB, 0x7788), 'B', watchB,
              regPattern(watchB, 0x7788), gotB);

    writeRegister(target, regPattern(target, 0x7788));   /* restore */
  }
  endSection();
}

void testWriteThrough() {
  beginSection("wthru");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t oldValue = regPattern(reg, 0x0F0F);
    uint16_t newValue = (uint16_t)(~oldValue) & valueMask;
    writeRegister(reg, oldValue);

    setBus(aaddrPins, ADDR_BITS, reg);
    if (HAS_PORT_B) setBus(baddrPins, ADDR_BITS, reg);
    setBus(dataPins, 16, newValue);
    setBus(waddrPins, ADDR_BITS, reg);
    delayMicroseconds(2);
    digitalWrite(pinWE, HIGH);
    digitalWrite(pinCLK, LOW);          /* window open */
    delayMicroseconds(2);
    uint16_t gotA = readBus(qaPins, 16) & readMask;
    uint16_t gotB = HAS_PORT_B ? (readBus(qbPins, 16) & readMask) : 0;
    digitalWrite(pinCLK, HIGH);
    digitalWrite(pinWE, LOW);

    check(gotA == newValue, reg, newValue, gotA);
    if (HAS_PORT_B) checkPort(gotB == newValue, 'B', reg, newValue, gotB);
  }
  endSection();
}

/* ------------------------------ SUITE ----------------------------------- */

void printMask(const char* label, uint16_t regMask, uint16_t bitMask) {
  Serial.print(F("  "));
  Serial.print(label);
  Serial.print(F(" regs: "));
  if (regMask == 0) {
    Serial.print(F("none"));
  } else {
    for (uint8_t reg = 0; reg < 16; reg++) {
      if (regMask & ((uint16_t)1 << reg)) { Serial.print(reg); Serial.print(' '); }
    }
  }
  Serial.print(F("  bits: 0x"));
  Serial.println(bitMask, HEX);
}

void runSuite() {
  failCount = 0;
  testCount = 0;
  regFailA = bitFailA = 0;
  regFailB = bitFailB = 0;
  for (uint8_t bit = 0; bit < 16; bit++) {
    regFailByBitA[bit] = 0;
    regFailByBitB[bit] = 0;
  }

  Serial.println(F("\n=============================================="));
  Serial.print(F("RegFile "));
  Serial.print(REG_COUNT);
  Serial.print(F("x"));
  Serial.print(DATA_WIDTH);
  Serial.print(F("  portB="));
  Serial.print(HAS_PORT_B ? F("yes") : F("no"));
  Serial.print(F("  mask=0x"));
  Serial.println(readMask, HEX);
  Serial.println(F("=============================================="));

  testAddressOrientation();
  testWalkingBits();
  testNibbleColumns();
  testPseudoRandom();
  testExhaustiveValues();
  testAddressUniqueness();
  testBankCrossing();
  testRetention();
  testWriteEnableGating();
  testUnpopulatedBitsReadZero();
  testPortBAbsent();
  testPortBMirror();
  testDualPortIndependence();
  testConcurrentAccess();
  testWriteThrough();

  Serial.println(F("----------------------------------------------"));
  Serial.print(F("TOTAL "));
  Serial.print(failCount);
  Serial.print('/');
  Serial.print(testCount);
  Serial.println(F(" failed"));

  if (failCount == 0) {
    Serial.println(F("*** ALL PASS ***"));
    digitalWrite(pinLed, HIGH);       /* solid on = clean run */
  } else {
    printMask("portA", regFailA, bitFailA);
    printMask("portB", regFailB, bitFailB);
    reportDiagnosis();
    Serial.println(F("*** FAILURES ***"));
    digitalWrite(pinLed, LOW);        /* dark = something failed */
  }
  Serial.println(F("Send ? for probe commands, r to re-run."));
}

/* --------------------------- COMMAND INPUT ------------------------------ */

char cmdBuf[16];
uint8_t cmdLen = 0;

void handleCommand(const char* cmd) {
  switch (cmd[0]) {
    case 'r': case 'R': runSuite(); break;
    case 'q': case 'Q': probeQ();   break;
    case 'w': case 'W': probeWrite((uint8_t)atoi(cmd + 1)); break;
    case 'a': case 'A': probeReadA((uint8_t)atoi(cmd + 1)); break;
    case 'b': case 'B': probeReadB((uint8_t)atoi(cmd + 1)); break;
    case 'd': case 'D':
      probeData((uint16_t)strtoul(cmd + 1, NULL, 16));
      break;
    case 's': case 'S': probeStore(cmd + 1); break;
    default: printHelp(); break;
  }
}

/* ------------------------------ SETUP ----------------------------------- */

void setup() {
  Serial.begin(115200);

  for (uint8_t i = 0; i < 16; i++) {
    pinMode(dataPins[i], OUTPUT);  digitalWrite(dataPins[i], LOW);
    pinMode(qaPins[i], INPUT);
    pinMode(qbPins[i], INPUT);
  }
  for (uint8_t i = 0; i < ADDR_BITS; i++) {
    pinMode(waddrPins[i], OUTPUT); digitalWrite(waddrPins[i], LOW);
    pinMode(aaddrPins[i], OUTPUT); digitalWrite(aaddrPins[i], LOW);
    pinMode(baddrPins[i], OUTPUT); digitalWrite(baddrPins[i], LOW);
  }
  pinMode(pinWE, OUTPUT);  digitalWrite(pinWE, LOW);
  pinMode(pinCLK, OUTPUT); digitalWrite(pinCLK, HIGH);   /* CLK idles high */
  pinMode(pinLed, OUTPUT); digitalWrite(pinLed, LOW);

  if (DRIVE_OEA) { pinMode(pinOEA, OUTPUT); digitalWrite(pinOEA, LOW); }
  else           { pinMode(pinOEA, INPUT); }
  if (DRIVE_OEB) { pinMode(pinOEB, OUTPUT); digitalWrite(pinOEB, LOW); }
  else           { pinMode(pinOEB, INPUT); }

  delay(50);
  runSuite();
}

void loop() {
  while (Serial.available()) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      cmdBuf[cmdLen] = '\0';
      if (cmdLen > 0) handleCommand(cmdBuf);
      cmdLen = 0;
    } else if (cmdLen < sizeof(cmdBuf) - 1) {
      cmdBuf[cmdLen++] = c;
    }
  }
}
