# Teensy 4.1 + 5 V Adapter Board — Reference for Writing Test-Harness Sketches

Working notes for building Arduino sketches that use the Teensy 4.1 adapter
board (RetroShield-derived design, TXS0108E level shifters, J1 2x18 header)
as a test harness for 5 V TTL circuits.

**Dual-platform note (2026-07):** the toolchain now also targets the Arduino
Mega 2560 wired directly into the same header positions (RetroShield form
factor — zero rewiring). `AdapterPorts.h` selects the board automatically.
The Mega is the **verification platform of record**; this document covers
the Teensy/adapter path, and `MegaDirect_Reference.md` covers the Mega and
the platform-choice rules. Sketches must build on both: wrap string
literals in `F(...)` and use `SW_PIN_MODE` for the buttons.

---

## 1. The Hardware Stack

| Layer | Part | Speed / limit |
|---|---|---|
| CPU | NXP i.MX RT1062, ARM Cortex-M7 @ 600 MHz | ~1.7 ns per instruction cycle |
| GPIO | RT1062 native ports | Toggle up to ~150 MHz, sub-ns edges |
| Level shift | TXS0108E (auto-direction, 3.3 V ↔ 5 V) | ~110 Mbps push-pull best case; tens of Mbps realistic; **the bottleneck** |
| Cabling | Ribbon to device under test | ~50–70 pF/m; keep short (see §5) |
| Host link | Native USB 2.0 High Speed (480 Mbit/s) | ~20–25 MB/s real throughput |

Speed hierarchy: **CPU ≫ GPIO ≫ TXS0108 ≫ any retro/TTL bus.** A 1–8 MHz
classic bus is orders of magnitude inside the envelope. The level shifters,
not the Teensy, set the practical ceiling.

---

## 2. Software Architecture (established pattern)

Every sketch = shared header + per-project `.ino` in the same sketch folder.

**`AdapterPorts.h` is dual-platform.** The Teensy section carries the
original pin mapping, verified end-to-end with the LED chaser — **never
alter that mapping**. The Mega section (`__AVR_ATmega2560__`) provides the
identical API at the same header positions plus compatibility shims
(`digitalWriteFast`/`digitalReadFast` → plain calls, `delayNanoseconds` →
µs-rounded, `SW_PIN_MODE`, `PLATFORM_NAME`). All sketches share the one
header so both mappings stay trustworthy.

### What the header provides

- Four virtual 8-bit ports **A–D** on the J1 header, all 5 V logic via the
  level shifters (Teensy) or native (Mega).
- Pins are `constexpr uint8_t` named `PIN_PA0`..`PIN_PD7`. The `P` prefix is
  mandatory: the cores define `PIN_A0`-style macros for analog pins, and
  reusing those names breaks compilation.
- Ribbon colour convention: **bit 0 = white, bit 7 = red** within each port.
- Physical layout on J1: edge column = Port D (upper 8) over Port C (lower 8);
  inner column = Port B (upper 8) over Port A (lower 8). Top row 2x GND,
  bottom row 2x +5 V.
- `PORT_A_PINS[8]`..`PORT_D_PINS[8]` tables + `setPortMode(table, mode)` for
  setup-time direction configuration (loops are fine here — speed irrelevant).
- Unrolled fast accessors: `readPortA()`..`readPortD()`,
  `writePortA(v)`..`writePortD(v)`. On the Teensy these use
  `digitalReadFast` / `digitalWriteFast` with compile-time-constant pins so
  each bit folds to a single GPIO register operation.
  **Never convert these to loops — a variable pin argument defeats the fast
  path entirely.** (On the Mega they map to plain calls and run bit-serial,
  ~4–5 µs per line — see `MegaDirect_Reference.md` §3.)
- `samplePorts()` → all 32 lines in one `uint32_t`: A = bits 0–7, B = 8–15,
  C = 16–23, D = 24–31.
- Buttons: `PIN_SW1`, `PIN_SW2`, active-low. On the adapter: Teensy pins
  40/41 with 4.7 kΩ pull-ups **on the board** → `SW_PIN_MODE` is plain
  `INPUT`. On the Mega: pins 2/3, `SW_PIN_MODE` is `INPUT_PULLUP`, buttons
  optional. Always write `pinMode(PIN_SWx, SW_PIN_MODE)`.
  Helpers: `sw1Pressed()`, `sw2Pressed()` return true when pressed.
- Note: `PIN_PD1` on the Teensy is pin 13 = onboard LED. Harmless as input;
  doubles as a free visual bit-1 indicator when Port D is an output.

### Sketch conventions

- `setup()`: set each port's direction per project
  (`INPUT` when external hardware drives the line, `INPUT_PULLUP` for lines
  that may float or shared tri-state rails, `OUTPUT` when the sketch drives).
- `loop()`: non-blocking. `millis()`-based timing, no `delay()`. Small static
  state variables. Read Serial without blocking; accept commands at any time.
- Serial startup: `while (!Serial && millis() < 3000) { }` — bounded wait so
  sketches still run headless (harmless on the Mega, where it is always true).
- Boot banner: print `PLATFORM_NAME` and any settings whose defaults matter
  (e.g. clock polarity) — **all runtime settings reset on re-flash**, and a
  silently wrong default has burned this project once.
- Change-detection reporting: sample, compare with last value, print only on
  change (see `printHex32()` pattern — 8 hex chars per 32-bit sample).
- String literals in `F(...)` so the sketch also fits the Mega's 8 KB SRAM.

---

## 3. USB Serial Facts (Teensy)

- `Serial` on the Teensy 4.1 is **native USB CDC**, not a UART. The baud rate
  passed to `Serial.begin()` is ignored — pure API decoration. (`Serial.baud()`
  reports what the PC selected, if ever needed.)
- Throughput: USB 2.0 High Speed, ~20–25 MB/s sustained in practice —
  roughly 2,000x a real 115200 UART. Serial output will essentially never be
  the bottleneck in a test harness *on the Teensy*. (On the Mega it is a real
  115200 UART and printing **can** dominate — see `MegaDirect_Reference.md`.)
- Latency, not bandwidth, is the subtlety: prints are buffered and flushed in
  USB packets. `Serial.send_now()` forces an immediate flush when
  minimum-latency output matters (e.g. timestamped event capture).
- `Serial` only evaluates true once the PC opens the port — hence the bounded
  startup wait.

---

## 4. TXS0108E Level Shifters — Behaviour and Limits

Auto-direction, no DIR pin. Mechanism: weak internal pull-ups (~4 kΩ
effective) hold the line statically; **one-shot edge accelerators** briefly
enable a strong driver on each detected edge, then release back to the weak
pull-up. Everything below follows from that.

**Ratings:** up to 110 Mbps push-pull, ~1.2 Mbps open-drain. Propagation
delay a few ns. Keep capacitive load per channel under **~70 pF total**
(cable + connectors + traces + far-end load).

**Consequences:**

1. **Weak static drive.** Between edges the line is held only by the weak
   pull-up. Anything drawing real current — LEDs especially — fights it.
   LEDs on the 5 V side need buffering or high-value resistors.
2. **One-shot re-triggering.** The accelerators are edge detectors. Ringing
   or coupled crosstalk can re-fire them → glitches/oscillation. This pairs
   badly with the Teensy's sub-ns GPIO edges, which is why the board's 33 Ω
   series terminations (at the driving end) matter and must stay.
3. **No damage from contention**, but simultaneous opposing drive gives an
   indeterminate "mush" level, not a clean win.
4. **The weak keeper forms dividers with external resistors** (validated
   2026-07, the hard way): a 10 k pull-up on a TXS-held-low line parks the
   node at ~1.6 V — mid-threshold for HC logic. Any RC / Schmitt / pull-up
   network attached to a TXS-driven line must budget for the keeper: use
   ≥100 k for bias resistors, and arrange for the line to idle in its
   solidly driven state.

**Realistic planning number:** ~20–50 Mbps per line with short, terminated,
push-pull wiring. Retro buses (1–8 MHz) don't come close to stressing it.

---

## 5. Cable Length Budget

Ribbon ≈ 50–70 pF/m per conductor. Against the ~70 pF/channel budget:

| Length | Verdict |
|---|---|
| ≤ 15–30 cm | Comfortable; datasheet behaviour. |
| ~35 cm (current rig) | Top of the comfortable zone: ~25–30 pF total. Fine for chaser, buttons, and retro-speed buses. Watch for crosstalk when many lines switch at once. |
| ~50 cm | Marginal; slowed edges, exposure to one-shot re-triggering. |
| ≥ 1 m | Expect trouble: budget consumed by cable alone, rise times stretch to hundreds of ns, ringing/phantom edges. |

**Symptom signatures** (both campaign-validated):

- Phantom transitions in change reports (bits flickering that shouldn't) =
  crosstalk re-firing the one-shots, **not** a pin-mapping error.
- Wrong **latched** values appearing at clock edges — especially additive
  phantom bits or single-bit drops that worsen when many lines slew at
  once — = coupling into a clock or strobe line producing phantom capture
  events. Simultaneous 8-line slews through the 35 cm loom re-clocked '574
  registers up to ~90% per event; single-line slews were far weaker. The
  chase test (slew operand lines N ns after a clock; exact re-captures =
  proof) is the diagnostic.

Diagnose wiring before doubting `AdapterPorts.h`. The mapping is
chaser-verified.

**Mitigations, in order of effort:** shorten the run; put grounds in the
ribbon alongside signals (ideally every second conductor); keep the 33 Ω
series terminations at the Teensy end; route clock/strobe lines apart from
operand groups, with their own ground return; Schmitt-condition clocks at
the DUT end ('HC14 + ~1 k / 100 pF RC, RC ground at the chip, bias per §4.4)
— this took the worst stress case from 95% to ~1%. Worst case for coupling:
eight lines of a data bus all slewing simultaneously (bus turnaround).

---

## 6. Writing a TTL Test Harness — Checklist

1. Copy `PortTemplate.ino` as the starting point; include `"AdapterPorts.h"`.
2. Decide direction per port for the device under test:
   - DUT outputs → harness port as `INPUT`.
   - Shared / tri-state bus lines → `INPUT_PULLUP` so they never float.
   - Harness-driven stimulus → `OUTPUT`.
3. Timing headroom: at 600 MHz there is effectively unlimited CPU per bus
   cycle of a TTL-era device. Generate clocks/strobes with `digitalWriteFast`
   on constant pins; even bit-banged, edges are far faster than the TXS or
   the DUT need.
4. If precise pulse widths matter, remember the TXS adds a few ns of
   propagation delay each way and edge rates on the 5 V side depend on load;
   allow tens of ns of slack rather than assuming Teensy-side edge speed.
5. Logging: print freely on the Teensy — USB won't be the bottleneck. Use
   change-detection (`samplePorts()` vs last value) rather than continuous
   dumping to keep output readable, and `F(...)` on literals for the Mega.
   `Serial.send_now()` if event timing on the host matters.
6. Use SW1/SW2 for manual single-step / trigger controls — already debounced
   in hardware terms by the RC of pull-up + finger; add `millis()` debounce
   if edges are counted. `pinMode(PIN_SWx, SW_PIN_MODE)`.
7. Keep everything non-blocking so command input (port select, mode changes)
   works mid-run — the PortChaser command pattern (read Serial every loop,
   act immediately) is the model.
8. Don't drive LEDs or heavy loads directly through the shifter channels;
   buffer them. (The Mega can drive LEDs directly — 20 mA class pins.)
9. **Verify on the Mega** before declaring a DUT correct: a Teensy-path
   failure may be drive-path signal integrity, not the DUT (see
   `MegaDirect_Reference.md` §4 — the platform discrepancy is itself a
   diagnostic).

---

## 7. Things That Have Bitten / Will Bite

- Naming any constant `PIN_A0`..`PIN_A9` → collides with core macros.
- Converting the unrolled port readers/writers to loops → silently loses the
  single-instruction fast path on the Teensy.
- Using `INPUT_PULLUP` on the adapter's SW1/SW2 → harmless but redundant
  (board has 4.7 kΩ already); `SW_PIN_MODE` handles both platforms.
- Trusting `Serial.begin()`'s baud number to mean anything on the Teensy →
  it doesn't. On the Mega it does.
- Blaming the pin mapping for flaky bits → check cable/crosstalk first; the
  mapping is chaser-verified.
- Forgetting the weak-pull-up phase of the TXS when eyeballing scope traces:
  a slow tail after a fast initial edge is normal behaviour, not a fault.
- Attaching an RC / pull-up / Schmitt network to a TXS-driven line without
  accounting for the ~4 kΩ static keeper → mid-threshold parked nodes and
  chattering gates (§4.4). Bias with ≥100 k; idle the line driven.
- Forgetting that **all runtime settings reset on re-flash** — print the
  ones that matter (clock polarity!) in the boot banner and read it.
- A DMM reading "5.00 V" says nothing about nanosecond transients or
  coupling. Wrong latched values at clock edges are a signal-integrity
  symptom even when every rail meters perfectly.
- Bare string literals in a report-heavy sketch → SRAM exhaustion on the
  Mega. `F(...)` everywhere, from the start.
