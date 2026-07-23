/*
 * RegisterFileTest.ino
 * Arduino Mega 2560 test harness for the Thumby Register File Module.
 *
 * BOARD: 8 registers x 16 bits, two read ports, one write port.
 *   Array A (port QA): U1..U8      Array B (port QB): U9..U16
 *   U17/U18 74HC139 decoders, U19 74HC00 write gating.
 *   Fixed configuration — no jumpers, no pulldowns, 3 address bits.
 *
 *            bits 3:0   bits 7:4   bits 11:8  bits 15:12
 *   A bank 0 (r0-r3)  U1    U2    U3    U4
 *   A bank 1 (r4-r7)  U5    U6    U7    U8
 *   B bank 0 (r0-r3)  U9    U10   U11   U12
 *   B bank 1 (r4-r7)  U13   U14   U15   U16
 *
 * ------------------------- FAST I/O ------------------------------------
 * digitalWrite/digitalRead cost tens of clock cycles each; a full
 * write-and-read cycle needs about forty of them. Direct port access
 * replaces that with a handful of instructions.
 *
 * The D bus maps cleanly: A0..A7 are PF0..PF7 and A8..A15 are PK0..PK7,
 * both in ascending order, so the whole 16-bit write is two stores:
 *
 *     PORTF = (uint8_t)value;   PORTK = (uint8_t)(value >> 8);
 *
 * The Q buses do NOT map cleanly. One bus per header row means each is
 * sprayed across six ports, several of them bit-reversed. Those maps are
 * spelled out one bit per line below rather than compressed, because
 * this is precisely the kind of table that is easy to get subtly wrong
 * and expensive to debug.
 *
 * SELF-VERIFICATION: at boot the fast path is checked against the
 * digitalRead/digitalWrite path over a spread of patterns. If they ever
 * disagree the fast path is disabled for the session and the mismatch is
 * printed. A wrong bit map therefore costs a warning, not a wild goose
 * chase.
 *
 * ------------------------- ELECTRICAL NOTE -----------------------------
 * Fast I/O makes the loom's weaknesses MORE relevant, not less. A single
 * port store switches eight outputs in one clock instead of spreading
 * them over ~30 us of digitalWrite calls, so peak di/dt through the
 * ground return rises sharply. This loom has:
 *
 *   - one ground pin (H8) and one ~35 cm return wire for 19 ICs
 *   - 35 cm ribbons with no interleaved ground conductors
 *   - no pulldowns anywhere on the board
 *
 * That does not threaten test correctness: ground bounce settles in
 * nanoseconds and every test samples microseconds later. It does affect
 * the MARGIN measurements below, which deliberately work in the bounce
 * timescale — so treat those numbers as "this loom, this board", not as
 * the module's intrinsic capability. The remedy for bounce is more
 * ground returns, not slower code.
 *
 * ------------------------- COMMANDS ------------------------------------
 *   r          run the test suite
 *   bench      compare slow vs fast I/O throughput
 *   margin     measure minimum write window and read settle time
 *   w<n>       hold WADDR = n (0..7)   meter U1 pin 14 (WA) / 13 (WB)
 *   a<n>       hold AADDR = n (0..7)   meter U1 pin  5 (RA) /  4 (RB)
 *   b<n>       hold BADDR = n (0..7)   meter U9 pin  5 (RA) /  4 (RB)
 *   d<hex>     hold the D bus, e.g. d100 = bit 8 only
 *   s<r>:<hex> store then park both read addresses, e.g. s5:0000
 *   q          print QA and QB as currently read
 *   f          toggle fast I/O on/off
 *   ?          help
 *
 * LED (pin 13): blinks ~3 Hz while running, solid = all pass,
 * dark = failures. Rate is set by LED_BLINK_MS.
 */

#include <avr/io.h>
#include <util/atomic.h>

/* ------------------------- CONFIGURATION ------------------------------- */

const uint8_t  REG_COUNT   = 8;
const uint8_t  DATA_WIDTH  = 16;
const bool     HAS_PORT_B  = true;
const bool     QB_WIRED    = true;
const bool     BOARD_HAS_PULLDOWNS = false;
const bool     DRIVE_OEA   = true;
const bool     DRIVE_OEB   = true;

const uint8_t  MAX_FAILS_SHOWN = 3;
const uint16_t RANDOM_VALUES   = 512;
const bool     RUN_EXHAUSTIVE  = true;   /* 8 x 65536 cycles */

/* Settling delays. With fast I/O the instruction overhead alone is no
 * longer comfortably above the module's access time, so these are what
 * guarantee the read is valid. 2 us is ~28x the 70 ns bank-crossing
 * worst case. Lower them only with the margin figures in hand. */
const uint8_t  SETTLE_US = 2;

bool fastIO = true;      /* cleared if boot verification fails */

/* ------------------------------- PINS ---------------------------------- */

const uint8_t qaPins[16] = {22,24,26,28,30,32,34,36,38,40,42,44,46,48,50,52};
const uint8_t qbPins[16] = {23,25,27,29,31,33,35,37,39,41,43,45,47,49,51,53};
const uint8_t dataPins[16] = {54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69};
const uint8_t waddrPins[3] = {4, 5, 6};
const uint8_t aaddrPins[3] = {14, 15, 16};
const uint8_t baddrPins[3] = {18, 19, 20};

const uint8_t pinWE  = 7;
const uint8_t pinCLK = 12;
const uint8_t pinOEA = 17;
const uint8_t pinOEB = 21;
const uint8_t pinLed = LED_BUILTIN;

const uint8_t  ADDR_BITS = 3;

/* LED heartbeat. Time-based, not count-based: the check rate varies by
 * more than 10x between slow and fast I/O, so a fixed number of checks
 * per toggle looks like a lazy blink in one mode and a solid lamp in the
 * other. LED_BLINK_MS is the half-period, so 150 ms is about 3.3 Hz.
 * millis() is consulted only every LED_POLL_EVERY checks, because the
 * call itself costs a few microseconds — comparable to a whole check on
 * the fast path. */
const uint16_t LED_BLINK_MS  = 150;
const uint16_t LED_POLL_EVERY = 64;

/* ---------------------- PORT MAPS FOR FAST I/O -------------------------
 * Every line here was derived from the Mega 2560 pin mapping table and
 * is checked at boot by verifyFastIO(). Do not "tidy" these into loops.
 *
 * D bus  : D0..D7  = PF0..PF7   D8..D15 = PK0..PK7   (clean, ascending)
 * CLK    : pin 12  = PB6        - low I/O, so cbi/sbi: 2 cycles
 * WE     : pin  7  = PH4
 * /OEA   : pin 17  = PH0        /OEB : pin 21 = PD0
 * WADDR  : 4,5,6   = PG5, PE3, PH3
 * AADDR  : 14,15,16= PJ1, PJ0, PH1     (note PJ1/PJ0 reversed)
 * BADDR  : 18,19,20= PD3, PD2, PD1     (note descending within PORTD)
 */

/* The de-interleave accumulates into uint8_t ONE TERM AT A TIME.
 * Writing it as a single OR-chain looks tidier but C integer promotion
 * turns every `(a >> 1) & 0x02` into 16-bit arithmetic, and sixteen
 * promoted terms on an 8-bit core cost ~240 cycles. Assigning each term
 * into a uint8_t accumulator keeps the whole thing in 8-bit registers.
 *
 * QA bit -> port bit:
 *   0..3   PINA 0,2,4,6        4..7   PINC 7,5,3,1
 *   8      PIND 7              9      PING 1
 *   10..13 PINL 7,5,3,1        14,15  PINB 3,1          */
static inline uint16_t readQAFast() {
  uint8_t a = PINA, c = PINC, dd = PIND, g = PING, l = PINL, b = PINB;
  uint8_t lo, hi;

  lo  = (uint8_t)(a & 0x01);
  lo |= (uint8_t)((uint8_t)(a >> 1) & 0x02);
  lo |= (uint8_t)((uint8_t)(a >> 2) & 0x04);
  lo |= (uint8_t)((uint8_t)(a >> 3) & 0x08);
  lo |= (uint8_t)((uint8_t)(c >> 3) & 0x10);
  lo |= (uint8_t)(c & 0x20);
  lo |= (uint8_t)((uint8_t)(c << 3) & 0x40);
  lo |= (uint8_t)((uint8_t)(c << 6) & 0x80);

  hi  = (uint8_t)((uint8_t)(dd >> 7) & 0x01);
  hi |= (uint8_t)(g & 0x02);
  hi |= (uint8_t)((uint8_t)(l >> 5) & 0x04);
  hi |= (uint8_t)((uint8_t)(l >> 2) & 0x08);
  hi |= (uint8_t)((uint8_t)(l << 1) & 0x10);
  hi |= (uint8_t)((uint8_t)(l << 4) & 0x20);
  hi |= (uint8_t)((uint8_t)(b << 3) & 0x40);
  hi |= (uint8_t)((uint8_t)(b << 6) & 0x80);

  return (uint16_t)(((uint16_t)hi << 8) | lo);
}

/* QB bit -> port bit:
 *   0..3   PINA 1,3,5,7        4..7   PINC 6,4,2,0
 *   8      PING 2              9      PING 0
 *   10..13 PINL 6,4,2,0        14,15  PINB 2,0          */
static inline uint16_t readQBFast() {
  uint8_t a = PINA, c = PINC, g = PING, l = PINL, b = PINB;
  uint8_t lo, hi;

  lo  = (uint8_t)((uint8_t)(a >> 1) & 0x01);
  lo |= (uint8_t)((uint8_t)(a >> 2) & 0x02);
  lo |= (uint8_t)((uint8_t)(a >> 3) & 0x04);
  lo |= (uint8_t)((uint8_t)(a >> 4) & 0x08);
  lo |= (uint8_t)((uint8_t)(c >> 2) & 0x10);
  lo |= (uint8_t)((uint8_t)(c << 1) & 0x20);
  lo |= (uint8_t)((uint8_t)(c << 4) & 0x40);
  lo |= (uint8_t)((uint8_t)(c << 7) & 0x80);

  hi  = (uint8_t)((uint8_t)(g >> 2) & 0x01);
  hi |= (uint8_t)((uint8_t)(g << 1) & 0x02);
  hi |= (uint8_t)((uint8_t)(l >> 4) & 0x04);
  hi |= (uint8_t)((uint8_t)(l >> 1) & 0x08);
  hi |= (uint8_t)((uint8_t)(l << 2) & 0x10);
  hi |= (uint8_t)((uint8_t)(l << 5) & 0x20);
  hi |= (uint8_t)((uint8_t)(b << 4) & 0x40);
  hi |= (uint8_t)((uint8_t)(b << 7) & 0x80);

  return (uint16_t)(((uint16_t)hi << 8) | lo);
}

static inline void writeDataFast(uint16_t v) {
  PORTF = (uint8_t)v;
  PORTK = (uint8_t)(v >> 8);
}

/* Address writes use LITERAL bit numbers, not a helper taking the bit as
 * an argument. AVR has no barrel shifter: `1u << bit` with a runtime bit
 * compiles to a shift loop, and passing a volatile reference stops the
 * optimiser folding it. The earlier helper-based version cost ~145
 * cycles for three bit writes. With literals GCC emits sbi/cbi for the
 * low-I/O ports and a short read-modify-write for the extended ones. */

static inline void writeWaddrFast(uint8_t v) {
  if (v & 1) PORTG |=  (uint8_t)(1u << 5);    /* pin 4  = PG5 = bit 0 */
  else       PORTG &= (uint8_t)~(1u << 5);
  if (v & 2) PORTE |=  (uint8_t)(1u << 3);    /* pin 5  = PE3 = bit 1 */
  else       PORTE &= (uint8_t)~(1u << 3);
  if (v & 4) PORTH |=  (uint8_t)(1u << 3);    /* pin 6  = PH3 = bit 2 */
  else       PORTH &= (uint8_t)~(1u << 3);
}

static inline void writeAaddrFast(uint8_t v) {
  if (v & 1) PORTJ |=  (uint8_t)(1u << 1);    /* pin 14 = PJ1 = bit 0 */
  else       PORTJ &= (uint8_t)~(1u << 1);
  if (v & 2) PORTJ |=  (uint8_t)(1u << 0);    /* pin 15 = PJ0 = bit 1 */
  else       PORTJ &= (uint8_t)~(1u << 0);
  if (v & 4) PORTH |=  (uint8_t)(1u << 1);    /* pin 16 = PH1 = bit 2 */
  else       PORTH &= (uint8_t)~(1u << 1);
}

static inline void writeBaddrFast(uint8_t v) {
  if (v & 1) PORTD |=  (uint8_t)(1u << 3);    /* pin 18 = PD3 = bit 0 */
  else       PORTD &= (uint8_t)~(1u << 3);
  if (v & 2) PORTD |=  (uint8_t)(1u << 2);    /* pin 19 = PD2 = bit 1 */
  else       PORTD &= (uint8_t)~(1u << 2);
  if (v & 4) PORTD |=  (uint8_t)(1u << 1);    /* pin 20 = PD1 = bit 2 */
  else       PORTD &= (uint8_t)~(1u << 1);
}

#define WE_HIGH()   (PORTH |=  (1u << 4))
#define WE_LOW()    (PORTH &= ~(1u << 4))
#define CLK_HIGH()  (PORTB |=  (1u << 6))   /* sbi, 2 cycles */
#define CLK_LOW()   (PORTB &= ~(1u << 6))   /* cbi, 2 cycles */

/* Exactly n NOPs, one cycle each (62.5 ns at 16 MHz). Fall-through is
 * deliberate — it gives a single indirect jump rather than a loop, so
 * the timing is exact rather than quantised by loop overhead. */
#define NOP1 __asm__ __volatile__("nop")
static inline void nopN(uint8_t n) {
  switch (n) {
    case 16: NOP1;  /* falls through */
    case 15: NOP1;  /* falls through */
    case 14: NOP1;  /* falls through */
    case 13: NOP1;  /* falls through */
    case 12: NOP1;  /* falls through */
    case 11: NOP1;  /* falls through */
    case 10: NOP1;  /* falls through */
    case  9: NOP1;  /* falls through */
    case  8: NOP1;  /* falls through */
    case  7: NOP1;  /* falls through */
    case  6: NOP1;  /* falls through */
    case  5: NOP1;  /* falls through */
    case  4: NOP1;  /* falls through */
    case  3: NOP1;  /* falls through */
    case  2: NOP1;  /* falls through */
    case  1: NOP1;  /* falls through */
    default: break;
  }
}

/* ----------------------------- DERIVED --------------------------------- */

const uint16_t valueMask = (DATA_WIDTH >= 16) ? 0xFFFF
                          : (uint16_t)((1u << DATA_WIDTH) - 1);
const uint16_t readMask  = BOARD_HAS_PULLDOWNS ? 0xFFFF : valueMask;

uint32_t failCount = 0;
uint32_t testCount = 0;
uint16_t regFailA = 0, bitFailA = 0;
uint16_t regFailB = 0, bitFailB = 0;
uint16_t regFailByBitA[16];
uint16_t regFailByBitB[16];

const char* sectionTag = "";
uint32_t sectionChecks = 0;
uint32_t sectionFails  = 0;

/* --------------------------- SLOW PATH ---------------------------------- */

void setBusSlow(const uint8_t pins[], uint8_t count, uint16_t value) {
  for (uint8_t i = 0; i < count; i++) {
    digitalWrite(pins[i], (value >> i) & 1 ? HIGH : LOW);
  }
}

uint16_t readBusSlow(const uint8_t pins[], uint8_t count) {
  uint16_t value = 0;
  for (uint8_t i = 0; i < count; i++) {
    if (digitalRead(pins[i]) == HIGH) value |= (uint16_t)1 << i;
  }
  return value;
}

/* --------------------------- DISPATCH ----------------------------------- */

static inline void putData(uint16_t v) {
  if (fastIO) writeDataFast(v); else setBusSlow(dataPins, 16, v);
}
static inline void putWaddr(uint8_t v) {
  if (fastIO) writeWaddrFast(v); else setBusSlow(waddrPins, ADDR_BITS, v);
}
static inline void putAaddr(uint8_t v) {
  if (fastIO) writeAaddrFast(v); else setBusSlow(aaddrPins, ADDR_BITS, v);
}
static inline void putBaddr(uint8_t v) {
  if (fastIO) writeBaddrFast(v); else setBusSlow(baddrPins, ADDR_BITS, v);
}
static inline uint16_t getQA() {
  return (fastIO ? readQAFast() : readBusSlow(qaPins, 16)) & readMask;
}
static inline uint16_t getQB() {
  return (fastIO ? readQBFast() : readBusSlow(qbPins, 16)) & readMask;
}
static inline void putWE(bool high) {
  if (fastIO) { if (high) WE_HIGH(); else WE_LOW(); }
  else digitalWrite(pinWE, high ? HIGH : LOW);
}
static inline void putCLK(bool high) {
  if (fastIO) { if (high) CLK_HIGH(); else CLK_LOW(); }
  else digitalWrite(pinCLK, high ? HIGH : LOW);
}

/* ------------------------ MODULE OPERATIONS ----------------------------- */

void writeRegister(uint8_t reg, uint16_t value) {
  putData(value);
  putWaddr(reg);
  delayMicroseconds(SETTLE_US);
  putWE(true);
  putCLK(false);                    /* write window opens */
  delayMicroseconds(SETTLE_US);
  putCLK(true);                     /* committed */
  putWE(false);
}

void clockWithoutWE(uint8_t reg, uint16_t value) {
  putData(value);
  putWaddr(reg);
  delayMicroseconds(SETTLE_US);
  putCLK(false);
  delayMicroseconds(SETTLE_US);
  putCLK(true);
}

uint16_t readPortA(uint8_t reg) {
  putAaddr(reg);
  delayMicroseconds(SETTLE_US);
  return getQA();
}

uint16_t readPortB(uint8_t reg) {
  putBaddr(reg);
  delayMicroseconds(SETTLE_US);
  return getQB();
}

/* -------------------- FAST PATH SELF-VERIFICATION ----------------------- */

/* Compare fast and slow I/O against each other on live hardware. Writes
 * with one path and reads with both, in both directions. Any disagreement
 * means a bit map above is wrong; the fast path is then disabled rather
 * than left to produce mystery failures. */
bool verifyFastIO() {
  const uint16_t patterns[] = {
    0x0000, 0xFFFF, 0xAAAA, 0x5555, 0x0001, 0x8000, 0x00FF, 0xFF00,
    0x0F0F, 0xF0F0, 0x1234, 0x8765, 0xDEAD, 0xBEEF, 0x0100, 0xFEFF
  };
  const uint8_t n = sizeof(patterns) / sizeof(patterns[0]);
  bool ok = true;

  for (uint8_t reg = 0; reg < REG_COUNT && ok; reg++) {
    for (uint8_t i = 0; i < n && ok; i++) {
      uint16_t want = patterns[i] & valueMask;

      /* write SLOW, read both ways */
      fastIO = false;
      writeRegister(reg, want);
      setBusSlow(aaddrPins, ADDR_BITS, reg);
      setBusSlow(baddrPins, ADDR_BITS, reg);
      delayMicroseconds(SETTLE_US);
      uint16_t slowA = readBusSlow(qaPins, 16) & readMask;
      uint16_t slowB = readBusSlow(qbPins, 16) & readMask;
      uint16_t fastA = readQAFast() & readMask;
      uint16_t fastB = readQBFast() & readMask;

      if (slowA != fastA || slowB != fastB) {
        Serial.print(F("  FAST I/O READ MISMATCH r"));
        Serial.print(reg);
        Serial.print(F(" wrote=0x")); Serial.print(want, HEX);
        Serial.print(F("  slowQA=0x")); Serial.print(slowA, HEX);
        Serial.print(F(" fastQA=0x")); Serial.print(fastA, HEX);
        Serial.print(F("  slowQB=0x")); Serial.print(slowB, HEX);
        Serial.print(F(" fastQB=0x")); Serial.println(fastB, HEX);
        ok = false;
        break;
      }

      /* write FAST, read slow — catches a bad write map */
      fastIO = true;
      writeRegister(reg, (uint16_t)(~want) & valueMask);
      fastIO = false;
      setBusSlow(aaddrPins, ADDR_BITS, reg);
      delayMicroseconds(SETTLE_US);
      uint16_t back = readBusSlow(qaPins, 16) & readMask;
      if (back != ((uint16_t)(~want) & valueMask)) {
        Serial.print(F("  FAST I/O WRITE MISMATCH r"));
        Serial.print(reg);
        Serial.print(F(" wrote=0x"));
        Serial.print((uint16_t)(~want) & valueMask, HEX);
        Serial.print(F(" read=0x")); Serial.println(back, HEX);
        ok = false;
      }
    }
  }

  fastIO = ok;
  Serial.print(F("Fast I/O self-check: "));
  Serial.println(ok ? F("PASS — enabled") : F("FAILED — using digitalWrite"));
  return ok;
}

/* ---------------------------- REPORTING --------------------------------- */

void ledTick() {
  static uint16_t counter = 0;
  static uint32_t lastToggle = 0;
  static bool state = false;
  if (++counter < LED_POLL_EVERY) return;
  counter = 0;
  uint32_t now = millis();
  if (now - lastToggle >= LED_BLINK_MS) {
    lastToggle = now;
    state = !state;
    digitalWrite(pinLed, state ? HIGH : LOW);
  }
}

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
    for (uint8_t bit = 0; bit < 16; bit++)
      if (diff & ((uint16_t)1 << bit)) regFailByBitB[bit] |= (uint16_t)1 << reg;
  } else {
    regFailA |= (uint16_t)1 << reg;
    bitFailA |= diff;
    for (uint8_t bit = 0; bit < 16; bit++)
      if (diff & ((uint16_t)1 << bit)) regFailByBitA[bit] |= (uint16_t)1 << reg;
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

void reportArray(const char* label, uint16_t bits,
                 const uint16_t regByBit[], uint8_t base) {
  if (bits == 0) return;
  uint16_t chips = 0;
  for (uint8_t bit = 0; bit < 16; bit++) {
    if ((bits & ((uint16_t)1 << bit)) == 0) continue;
    uint8_t column = bit / 4;
    uint16_t regs = regByBit[bit];
    if (regs & 0x000F) chips |= (uint16_t)1 << column;
    if (regs & 0x00F0) chips |= (uint16_t)1 << (column + 4);
  }
  Serial.print(F("  "));
  Serial.print(label);
  Serial.print(F("  bits: 0x"));
  Serial.print(bits, HEX);
  Serial.print(F("  suspect: "));
  for (uint8_t i = 0; i < 8; i++) {
    if (chips & ((uint16_t)1 << i)) {
      Serial.print('U'); Serial.print(base + i); Serial.print(' ');
    }
  }
  Serial.println();
}

void reportDiagnosis() {
  uint16_t shared = bitFailA & bitFailB;
  uint16_t onlyA  = bitFailA & ~shared;
  uint16_t onlyB  = bitFailB & ~shared;

  if (shared) {
    Serial.println(F("  SHARED across both ports — not a single '670 output."));
    Serial.print(F("    bits: "));
    for (uint8_t bit = 0; bit < 16; bit++)
      if (shared & ((uint16_t)1 << bit)) { Serial.print(bit); Serial.print(' '); }
    Serial.println();
    Serial.println(F("    Upstream: that D line, WE, CLK, WADDR."));
    Serial.println(F("    Downstream: QA/QB of the same bit index."));
  }
  reportArray("PORT A only — array A.", onlyA, regFailByBitA, 1);
  reportArray("PORT B only — array B.", onlyB, regFailByBitB, 9);
}

/* ------------------------------ TESTS ----------------------------------- */

uint16_t lfsrNext(uint16_t v) {
  uint16_t bit = (uint16_t)((v ^ (v >> 2) ^ (v >> 3) ^ (v >> 5)) & 1u);
  return (uint16_t)((v >> 1) | (bit << 15));
}

uint16_t regPattern(uint8_t reg, uint16_t salt) {
  return (uint16_t)(((uint16_t)(reg + 1) * 0x1111u) ^ salt) & valueMask;
}

uint8_t swapLowBits(uint8_t reg) {
  return (uint8_t)((reg & 0b100) | ((reg & 1) << 1) | ((reg >> 1) & 1));
}

void testAddressOrientation() {
  beginSection("orient");
  uint16_t gotA[16], gotB[16];
  for (uint8_t reg = 0; reg < REG_COUNT; reg++)
    writeRegister(reg, regPattern(reg, 0));
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
      uint16_t diff = 0;
      for (uint8_t reg = 0; reg < REG_COUNT; reg++)
        diff |= (uint16_t)(got[reg] ^ regPattern(reg, 0)) & readMask;
      Serial.print(F("addressing OK, data bits differ: 0x"));
      Serial.println(diff, HEX);
      for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
        if (got[reg] == regPattern(reg, 0)) continue;
        Serial.print(F("    r")); Serial.print(reg);
        Serial.print(F(" wrote=0x")); Serial.print(regPattern(reg, 0), HEX);
        Serial.print(F(" reads=0x")); Serial.println(got[reg], HEX);
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
      uint16_t a1 = readPortA(reg);
      check(a1 == one, reg, one, a1);
      if (HAS_PORT_B) { uint16_t b1 = readPortB(reg);
                        checkPort(b1 == one, 'B', reg, one, b1); }
      writeRegister(reg, zero);
      uint16_t a0 = readPortA(reg);
      check(a0 == zero, reg, zero, a0);
      if (HAS_PORT_B) { uint16_t b0 = readPortB(reg);
                        checkPort(b0 == zero, 'B', reg, zero, b0); }
    }
  }
  endSection();
}

void testNibbleColumns() {
  beginSection("nibble");
  const uint16_t patterns[] = {
    0x000F, 0x00F0, 0x0F00, 0xF000, 0xFFF0, 0xFF0F,
    0xF0FF, 0x0FFF, 0x0000, 0xFFFF, 0xAAAA, 0x5555
  };
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    for (uint8_t i = 0; i < 12; i++) {
      uint16_t want = patterns[i] & valueMask;
      writeRegister(reg, want);
      uint16_t a = readPortA(reg);
      check(a == want, reg, want, a);
      if (HAS_PORT_B) { uint16_t b = readPortB(reg);
                        checkPort(b == want, 'B', reg, want, b); }
    }
  }
  endSection();
}

/* Pseudorandom sweep, both ports. */
void testPseudoRandom() {
  beginSection("random");
  uint16_t seed = 0xACE1;
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t v = seed;
    for (uint16_t i = 0; i < RANDOM_VALUES; i++) {
      v = lfsrNext(v);
      uint16_t want = v & valueMask;
      writeRegister(reg, want);
      uint16_t a = readPortA(reg);
      check(a == want, reg, want, a);
      if (HAS_PORT_B) { uint16_t b = readPortB(reg);
                        checkPort(b == want, 'B', reg, want, b); }
    }
    seed = lfsrNext(seed);
  }
  endSection();
}

/* Exhaustive sweep, both ports. 32-bit counter — (uint16_t)1 << 16 is 0. */
void testExhaustiveValues() {
  if (!RUN_EXHAUSTIVE) return;
  Serial.print(F("  sweep\trunning "));
  Serial.print((uint32_t)REG_COUNT * (1UL << DATA_WIDTH));
  Serial.println(F(" values — LED blinks while busy"));
  beginSection("sweep");
  uint32_t top = 1UL << DATA_WIDTH;
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    for (uint32_t v = 0; v < top; v++) {
      uint16_t want = (uint16_t)v & valueMask;
      writeRegister(reg, want);
      uint16_t a = readPortA(reg);
      check(a == want, reg, want, a);
      if (HAS_PORT_B) { uint16_t b = readPortB(reg);
                        checkPort(b == want, 'B', reg, want, b); }
    }
  }
  endSection();
}

void testAddressUniqueness() {
  beginSection("uniq");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++)
    writeRegister(reg, regPattern(reg, 0x0F0F));
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t want = regPattern(reg, 0x0F0F);
    uint16_t a = readPortA(reg);
    check(a == want, reg, want, a);
    if (HAS_PORT_B) { uint16_t b = readPortB(reg);
                      checkPort(b == want, 'B', reg, want, b); }
  }
  endSection();
}

void testBankCrossing() {
  beginSection("bank");
  const uint16_t lowBank  = (uint16_t)0x5AA5 & valueMask;
  const uint16_t highBank = (uint16_t)0xA55A & valueMask;
  writeRegister(3, lowBank);
  writeRegister(4, highBank);
  for (uint8_t pass = 0; pass < 8; pass++) {
    uint16_t l = readPortA(3); check(l == lowBank, 3, lowBank, l);
    uint16_t h = readPortA(4); check(h == highBank, 4, highBank, h);
    if (HAS_PORT_B) {
      uint16_t bl = readPortB(3); checkPort(bl == lowBank, 'B', 3, lowBank, bl);
      uint16_t bh = readPortB(4); checkPort(bh == highBank, 'B', 4, highBank, bh);
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

void testPortBMirror() {
  if (!HAS_PORT_B) return;
  beginSection("mirror");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++)
    writeRegister(reg, regPattern(reg, 0xC33C));
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t want = regPattern(reg, 0xC33C);
    uint16_t a = readPortA(reg), b = readPortB(reg);
    check(a == want, reg, want, a);
    checkPort(b == want, 'B', reg, want, b);
  }
  endSection();
}

void testDualPortIndependence() {
  if (!HAS_PORT_B) return;
  beginSection("dualport");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++)
    writeRegister(reg, regPattern(reg, 0x1234));
  for (uint8_t a = 0; a < REG_COUNT; a++) {
    uint8_t b = (uint8_t)(REG_COUNT - 1 - a);
    putAaddr(a);
    putBaddr(b);
    delayMicroseconds(SETTLE_US);
    uint16_t ga = getQA(), gb = getQB();
    check(ga == regPattern(a, 0x1234), a, regPattern(a, 0x1234), ga);
    checkPort(gb == regPattern(b, 0x1234), 'B', b, regPattern(b, 0x1234), gb);
  }
  endSection();
}

void testConcurrentAccess() {
  if (!HAS_PORT_B) return;
  beginSection("concur");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++)
    writeRegister(reg, regPattern(reg, 0x7788));
  for (uint8_t target = 0; target < REG_COUNT; target++) {
    uint8_t watchA = (uint8_t)((target + 1) % REG_COUNT);
    uint8_t watchB = (uint8_t)((target + 2) % REG_COUNT);
    uint16_t fresh = (uint16_t)(~regPattern(target, 0x7788)) & valueMask;

    putAaddr(watchA);
    putBaddr(watchB);
    putData(fresh);
    putWaddr(target);
    delayMicroseconds(SETTLE_US);
    putWE(true);
    putCLK(false);
    delayMicroseconds(SETTLE_US);
    uint16_t ga = getQA(), gb = getQB();
    putCLK(true);
    putWE(false);

    check(ga == regPattern(watchA, 0x7788), watchA,
          regPattern(watchA, 0x7788), ga);
    checkPort(gb == regPattern(watchB, 0x7788), 'B', watchB,
              regPattern(watchB, 0x7788), gb);
    writeRegister(target, regPattern(target, 0x7788));
  }
  endSection();
}

void testWriteThrough() {
  beginSection("wthru");
  for (uint8_t reg = 0; reg < REG_COUNT; reg++) {
    uint16_t oldValue = regPattern(reg, 0x0F0F);
    uint16_t newValue = (uint16_t)(~oldValue) & valueMask;
    writeRegister(reg, oldValue);
    putAaddr(reg);
    if (HAS_PORT_B) putBaddr(reg);
    putData(newValue);
    putWaddr(reg);
    delayMicroseconds(SETTLE_US);
    putWE(true);
    putCLK(false);
    delayMicroseconds(SETTLE_US);
    uint16_t ga = getQA();
    uint16_t gb = HAS_PORT_B ? getQB() : 0;
    putCLK(true);
    putWE(false);
    check(ga == newValue, reg, newValue, ga);
    if (HAS_PORT_B) checkPort(gb == newValue, 'B', reg, newValue, gb);
  }
  endSection();
}

/* ------------------------------ BENCHMARK ------------------------------- */

/* Time each primitive separately. The whole-cycle figure says 48 us but
 * the parts should sum to far less, so something dominates that is not
 * obvious from reading the code. Measure rather than guess. The volatile
 * sink stops the optimiser deleting calls whose results are unused, and
 * the empty-loop baseline is subtracted from every row. */
void benchPrimitives() {
  const uint16_t N = 5000;
  volatile uint16_t sink = 0;
  uint32_t t0, dt, base;

  Serial.println(F("\nPer-primitive cost:"));

  t0 = micros();
  for (uint16_t i = 0; i < N; i++) { sink += i; }
  base = micros() - t0;

#define BENCH_ONE(name, expr)                                   \
  t0 = micros();                                                \
  for (uint16_t i = 0; i < N; i++) { expr; }                    \
  dt = micros() - t0;                                           \
  dt = (dt > base) ? (dt - base) : 0;                           \
  Serial.print(F("  "));                                        \
  Serial.print(F(name));                                        \
  Serial.print(F("  "));                                        \
  Serial.print((uint32_t)dt * 1000UL / N);                      \
  Serial.println(F(" ns"))

  BENCH_ONE("putData   ", putData(i));
  BENCH_ONE("putWaddr  ", putWaddr(i & 7));
  BENCH_ONE("putAaddr  ", putAaddr(i & 7));
  BENCH_ONE("putBaddr  ", putBaddr(i & 7));
  BENCH_ONE("getQA     ", sink = getQA());
  BENCH_ONE("getQB     ", sink = getQB());
  BENCH_ONE("putWE pair", { putWE(true); putWE(false); });
  BENCH_ONE("putCLK pr ", { putCLK(false); putCLK(true); });
  BENCH_ONE("delay(SET)", delayMicroseconds(SETTLE_US));

#undef BENCH_ONE

  Serial.print(F("  (empty loop baseline "));
  Serial.print((uint32_t)base * 1000UL / N);
  Serial.println(F(" ns/iteration, already subtracted)"));
  Serial.println(F("  writeRegister = putData + putWaddr + 2 delays + WE/CLK"));
  Serial.println(F("  readPortA     = putAaddr + 1 delay + getQA"));
}

void runBenchmark() {
  const uint16_t iterations = 2000;
  bool saved = fastIO;

  Serial.println(F("\nThroughput (write + read-back cycles):"));

  for (uint8_t mode = 0; mode < 2; mode++) {
    fastIO = (mode == 1);
    if (fastIO && !saved) continue;      /* fast path disabled */
    uint32_t t0 = micros();
    uint16_t v = 0xACE1;
    for (uint16_t i = 0; i < iterations; i++) {
      v = lfsrNext(v);
      writeRegister(i & 0x07, v);
      (void)readPortA(i & 0x07);
    }
    uint32_t dt = micros() - t0;
    Serial.print(F("  "));
    Serial.print(fastIO ? F("fast ") : F("slow "));
    Serial.print(dt / iterations);
    Serial.print(F(" us/cycle    "));
    Serial.print((uint32_t)iterations * 1000000UL / dt);
    Serial.println(F(" cycles/s"));
  }
  fastIO = saved;

  Serial.print(F("  nominal settle delays: 3 x "));
  Serial.print(SETTLE_US);
  Serial.println(F(" us — but see the measured delay() row below."));

  benchPrimitives();
}

/* ------------------------------- MARGIN --------------------------------- */

/* CLK pulse with a floor of ONE cpu cycle. cbi/sbi are 2 cycles each, so
 * the earlier version could not go below 125 ns and hit its floor on the
 * first step, learning nothing. `out` is 1 cycle, so precomputing both
 * port values into registers and issuing back-to-back `out` gives a
 * 62.5 ns low period — the hard limit for GPIO on a 16 MHz AVR.
 *
 * Each case is a separate asm block so the nop count is fixed at compile
 * time; a runtime loop or switch inside the block would add its own
 * cycles between the two stores and destroy the timing. */
#define CLK_PULSE(NOPS)                          \
  __asm__ __volatile__(                          \
    "out %[p], %[lo]\n\t"                        \
    NOPS                                         \
    "out %[p], %[hi]\n\t"                        \
    :: [p]  "I" (_SFR_IO_ADDR(PORTB)),           \
       [lo] "r" (lo), [hi] "r" (hi))

static inline void clkLowPulse(uint8_t nops) {
  uint8_t hi = (uint8_t)(PORTB |  (1u << 6));
  uint8_t lo = (uint8_t)(PORTB & ~(1u << 6));
  switch (nops) {
    case  0: CLK_PULSE("");                                          break;
    case  1: CLK_PULSE("nop\n\t");                                   break;
    case  2: CLK_PULSE("nop\n\tnop\n\t");                            break;
    case  3: CLK_PULSE("nop\n\tnop\n\tnop\n\t");                     break;
    case  4: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\t");              break;
    case  5: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\t");       break;
    case  6: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"); break;
    case  7: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"
                       "nop\n\t");                                   break;
    case  8: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"
                       "nop\n\tnop\n\t");                            break;
    case  9: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"
                       "nop\n\tnop\n\tnop\n\t");                     break;
    case 10: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"
                       "nop\n\tnop\n\tnop\n\tnop\n\t");              break;
    case 11: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"
                       "nop\n\tnop\n\tnop\n\tnop\n\tnop\n\t");       break;
    default: CLK_PULSE("nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"
                       "nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t"); break;
  }
}

/* ns for a pulse of (1 + nops) cycles at 16 MHz */
static uint16_t pulseNs(uint8_t nops) {
  return (uint16_t)(((uint32_t)(1 + nops) * 125UL) / 2UL);
}

/* Preload the register with the complement, then attempt the real write
 * with a pulse of the requested width. If the short pulse fails to
 * commit, the readback still shows the complement. */
bool tryWriteWindow(uint8_t reg, uint16_t value, uint8_t nops) {
  writeDataFast((uint16_t)(~value) & valueMask);
  writeWaddrFast(reg);
  delayMicroseconds(4);
  WE_HIGH();
  clkLowPulse(12);                  /* known-good preload window */
  WE_LOW();
  delayMicroseconds(4);

  writeDataFast(value & valueMask);
  writeWaddrFast(reg);
  delayMicroseconds(4);
  WE_HIGH();
  clkLowPulse(nops);                /* the window under test */
  WE_LOW();
  delayMicroseconds(4);

  writeAaddrFast(reg);
  delayMicroseconds(4);
  return (readQAFast() & readMask) == (value & valueMask);
}

void marginWrite() {
  Serial.println(F("\nWrite window (CLK-low pulse width):"));
  Serial.println(F("  cycles    ns    result"));
  const uint16_t probes[] = {0x0000, 0xFFFF, 0xA5A5, 0x5A5A};
  uint8_t firstGood = 255;

  for (uint8_t nops = 0; nops <= 12; nops++) {
    bool allOk = true;
    for (uint8_t rep = 0; rep < 16 && allOk; rep++)
      for (uint8_t p = 0; p < 4 && allOk; p++)
        for (uint8_t reg = 0; reg < REG_COUNT && allOk; reg++)
          if (!tryWriteWindow(reg, probes[p] & valueMask, nops)) allOk = false;

    Serial.print(F("     "));
    Serial.print(1 + nops);
    Serial.print(F("     "));
    Serial.print(pulseNs(nops));
    Serial.print(F("    "));
    Serial.println(allOk ? F("commits") : F("FAILS"));
    if (allOk && firstGood == 255) firstGood = nops;
    ledTick();
  }

  Serial.print(F("  minimum reliable window: "));
  if (firstGood == 255)      Serial.println(F("none in range — check the loom"));
  else if (firstGood == 0)   Serial.println(F("<= 62 ns (one CPU cycle — the floor)"));
  else { Serial.print(pulseNs(firstGood)); Serial.println(F(" ns")); }
}

/* Address-to-valid-data on port A. Set the address, wait n cycles, then
 * sample. NOTE the resolution limit: sampling all six port registers
 * takes several cycles itself, so bits read later get more settling than
 * bits read first. Treat this as approximate — it is a sanity check on
 * SETTLE_US, not a datasheet-grade access time. */
bool tryReadSettle(uint8_t regA, uint8_t regB, uint16_t expect, uint8_t nops) {
  writeAaddrFast(regB);             /* park somewhere else first */
  delayMicroseconds(4);
  writeAaddrFast(regA);             /* the transition under test */
  nopN(nops);
  return (readQAFast() & readMask) == (expect & valueMask);
}

void marginRead() {
  Serial.println(F("\nRead settle (address change -> valid QA):"));
  Serial.println(F("  cycles    ns    result"));

  for (uint8_t reg = 0; reg < REG_COUNT; reg++)
    writeRegister(reg, regPattern(reg, 0x5A5A));

  uint8_t firstGood = 255;
  for (uint8_t nops = 0; nops <= 16; nops++) {
    bool allOk = true;
    for (uint8_t rep = 0; rep < 16 && allOk; rep++) {
      /* r3 -> r4 crosses the bank boundary, the slowest transition */
      if (!tryReadSettle(4, 3, regPattern(4, 0x5A5A), nops)) allOk = false;
      if (!tryReadSettle(3, 4, regPattern(3, 0x5A5A), nops)) allOk = false;
      if (!tryReadSettle(1, 0, regPattern(1, 0x5A5A), nops)) allOk = false;
    }
    Serial.print(F("     "));
    Serial.print(nops);
    Serial.print(F("     "));
    Serial.print((uint16_t)(((uint32_t)nops * 125UL) / 2UL));
    Serial.print(F("    "));
    Serial.println(allOk ? F("valid") : F("FAILS"));
    if (allOk && firstGood == 255) firstGood = nops;
    ledTick();
  }

  Serial.print(F("  settles within: "));
  if (firstGood == 255)    Serial.println(F("not within range"));
  else if (firstGood == 0) Serial.println(F("the sampling overhead itself"));
  else { Serial.print((uint16_t)(((uint32_t)firstGood * 125UL) / 2UL));
         Serial.println(F(" ns of added delay")); }
  Serial.print(F("  SETTLE_US is currently "));
  Serial.print(SETTLE_US);
  Serial.println(F(" us — margin is the ratio of those two."));
}

void runMargin() {
  if (!fastIO) {
    Serial.println(F("margin needs fast I/O — self-check failed, aborting."));
    return;
  }
  marginWrite();
  marginRead();
  Serial.println(F("  Both figures include this loom: 35 cm ribbons, one"));
  Serial.println(F("  ground return, no pulldowns. They are a floor for the"));
  Serial.println(F("  system as wired, not the module's intrinsic limit."));
}

/* ------------------------------ SUITE ----------------------------------- */

void runSuite() {
  failCount = testCount = 0;
  regFailA = bitFailA = regFailB = bitFailB = 0;
  for (uint8_t bit = 0; bit < 16; bit++) {
    regFailByBitA[bit] = 0;
    regFailByBitB[bit] = 0;
  }

  Serial.println(F("\n=============================================="));
  Serial.print(F("RegFile "));
  Serial.print(REG_COUNT);
  Serial.print('x');
  Serial.print(DATA_WIDTH);
  Serial.print(F("  portB="));
  Serial.print(HAS_PORT_B ? F("yes") : F("no"));
  Serial.print(F("  io="));
  Serial.println(fastIO ? F("fast") : F("slow"));
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
    digitalWrite(pinLed, HIGH);
  } else {
    reportDiagnosis();
    Serial.println(F("*** FAILURES ***"));
    digitalWrite(pinLed, LOW);
  }
  Serial.println(F("Send ? for commands."));
}

/* ------------------------------ PROBES ---------------------------------- */

void reportHeld(const char* what, const uint8_t pins[], uint8_t value) {
  Serial.print(F("HOLD ")); Serial.print(what);
  Serial.print(F(" = "));   Serial.print(value);
  Serial.print(F("  ->  "));
  for (uint8_t i = 0; i < ADDR_BITS; i++) {
    Serial.print(F("pin")); Serial.print(pins[i]); Serial.print('=');
    Serial.print(digitalRead(pins[i]) == HIGH ? F("HI") : F("LO"));
    Serial.print(F("  "));
  }
  Serial.println();
}

void probeExpect(const char* chip, uint8_t value,
                 const char* lsb, const char* msb) {
  Serial.print(F("  expect at ")); Serial.print(chip); Serial.print(F(":  "));
  Serial.print(lsb); Serial.print('=');
  Serial.print((value & 1) ? F("HI") : F("LO"));
  Serial.print(F("   ")); Serial.print(msb); Serial.print('=');
  Serial.println((value & 2) ? F("HI") : F("LO"));
  Serial.println(F("  (bit2 goes to the '139 decoder, not the '670)"));
}

void probeWrite(uint8_t v) { v &= 7; putWaddr(v);
  reportHeld("WADDR", waddrPins, v); probeExpect("U1", v, "pin14 WA", "pin13 WB"); }
void probeReadA(uint8_t v) { v &= 7; putAaddr(v);
  reportHeld("AADDR", aaddrPins, v); probeExpect("U1", v, "pin5 RA", "pin4 RB"); }
void probeReadB(uint8_t v) { v &= 7; putBaddr(v);
  reportHeld("BADDR", baddrPins, v); probeExpect("U9", v, "pin5 RA", "pin4 RB"); }

void probeData(uint16_t value) {
  putData(value);
  Serial.print(F("HOLD D = 0x")); Serial.println(value, HEX);
  Serial.print(F("  HIGH: "));
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
  Serial.println(F("  bits 3:0 U1/U5/U9/U13    bits 7:4  U2/U6/U10/U14"));
  Serial.println(F("  bits 11:8 U3/U7/U11/U15  bits 15:12 U4/U8/U12/U16"));
}

void probeQ() {
  Serial.print(F("QA = 0x")); Serial.print(getQA(), HEX);
  Serial.print(F("   QB = 0x")); Serial.println(getQB(), HEX);
}

void probeStore(const char* arg) {
  const char* sep = strchr(arg, ':');
  if (!sep) { Serial.println(F("usage: s<reg>:<hex>")); return; }
  uint8_t reg = (uint8_t)(atoi(arg) & 7);
  uint16_t value = (uint16_t)strtoul(sep + 1, NULL, 16);
  writeRegister(reg, value);
  putAaddr(reg);
  putBaddr(reg);
  delayMicroseconds(SETTLE_US);
  uint16_t qa = getQA(), qb = getQB();
  Serial.print(F("STORE r")); Serial.print(reg);
  Serial.print(F(" = 0x")); Serial.print(value, HEX);
  Serial.print(F("   QA=0x")); Serial.print(qa, HEX);
  Serial.print(F("   QB=0x")); Serial.println(qb, HEX);
  uint16_t da = (qa ^ value) & readMask, db = (qb ^ value) & readMask;
  if (da || db) {
    Serial.print(F("  wrong bits  QA:0x")); Serial.print(da, HEX);
    Serial.print(F("  QB:0x")); Serial.println(db, HEX);
    Serial.println(F("  addresses parked — meter the '670 Q pins now"));
    Serial.println(F("    bit%4: 0=pin10 Q1  1=pin9 Q2  2=pin7 Q3  3=pin6 Q4"));
  } else {
    Serial.println(F("  matches."));
  }
}

void printHelp() {
  Serial.println(F("r run suite   bench throughput   margin write window"));
  Serial.println(F("w<n>/a<n>/b<n> hold address   d<hex> hold D bus"));
  Serial.println(F("s<r>:<hex> store+park   q read QA/QB   f toggle fast I/O"));
}

/* --------------------------- COMMAND INPUT ------------------------------ */

char cmdBuf[20];
uint8_t cmdLen = 0;

void handleCommand(const char* cmd) {
  if (strncmp(cmd, "bench", 5) == 0)  { runBenchmark(); return; }
  if (strncmp(cmd, "margin", 6) == 0) { runMargin();    return; }
  switch (cmd[0]) {
    case 'r': case 'R': runSuite(); break;
    case 'q': case 'Q': probeQ();   break;
    case 'f': case 'F':
      fastIO = !fastIO;
      Serial.print(F("fast I/O "));
      Serial.println(fastIO ? F("ON") : F("OFF"));
      break;
    case 'w': case 'W': probeWrite((uint8_t)atoi(cmd + 1)); break;
    case 'a': case 'A': probeReadA((uint8_t)atoi(cmd + 1)); break;
    case 'b': case 'B': probeReadB((uint8_t)atoi(cmd + 1)); break;
    case 'd': case 'D': probeData((uint16_t)strtoul(cmd + 1, NULL, 16)); break;
    case 's': case 'S': probeStore(cmd + 1); break;
    default: printHelp(); break;
  }
}

/* ------------------------------ SETUP ----------------------------------- */

/* Reset cause, captured before anything else can disturb MCUSR. A
 * brownout during a long run and a serial-monitor DTR reset look
 * identical in the output otherwise. Some bootloaders clear MCUSR before
 * handing over, in which case this reads 0 and tells you nothing — but
 * when BORF does appear it is definitive. */
uint8_t resetCause __attribute__((section(".noinit")));

void captureResetCause(void) __attribute__((naked, used, section(".init3")));
void captureResetCause(void) {
  resetCause = MCUSR;
  MCUSR = 0;
}

void reportResetCause() {
  Serial.print(F("Reset cause: "));
  if (resetCause == 0) {
    Serial.println(F("(cleared by bootloader — unavailable)"));
    return;
  }
  if (resetCause & (1 << PORF))  Serial.print(F("power-on "));
  if (resetCause & (1 << EXTRF)) Serial.print(F("external/DTR "));
  if (resetCause & (1 << BORF))  Serial.print(F("BROWNOUT "));
  if (resetCause & (1 << WDRF))  Serial.print(F("watchdog "));
  Serial.print(F(" (0x"));
  Serial.print(resetCause, HEX);
  Serial.println(')');
  if (resetCause & (1 << BORF)) {
    Serial.println(F("  ** Brownout — the 5 V rail sagged. Suspect the"));
    Serial.println(F("  ** single ground return and USB feed under load."));
  }
}

void setup() {
  Serial.begin(115200);

  for (uint8_t i = 0; i < 16; i++) {
    pinMode(dataPins[i], OUTPUT); digitalWrite(dataPins[i], LOW);
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
  Serial.println(F("\nRegister File harness"));
  reportResetCause();
  verifyFastIO();
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
