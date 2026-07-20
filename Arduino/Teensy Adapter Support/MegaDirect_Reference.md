# Arduino Mega 2560 (Direct) — Independent Verification Platform

Companion to `LVCBoard_Reference.md`. The Mega 2560, wired directly to the
device under test at native 5 V, is the project's **independent
verification platform**: a second, dissimilar rig for confirming results.
Its credentials are earned — the hand-built 8-bit ALU passed
262,144/262,144 vectors on it, twice, with and without clock conditioning.
When a result must be trusted, or when the Teensy harness and the DUT
disagree, run it on the Mega: a discrepancy between two healthy platforms
on the same DUT points at a rig, not the DUT.

---

## 1. Why It's Trustworthy

- **Native 5 V push-pull drive**, ~20 mA class, held continuously — the
  DUT sees strong, simple logic levels with no translation stage of any
  kind in the path.
- **Inherently bit-serial port writes** (~4–5 µs per line): lines never
  slew simultaneously, so the multi-line coupling stress that can
  phantom-clock registers through long looms simply cannot occur. Gentle
  by construction.
- Edge rates of a few nanoseconds: fast enough for any TTL DUT, slow
  enough to be benign on unterminated hookup wire.

The trade is speed: an exhaustive 262,144-vector run takes ≈56 s
(~210 µs/vector) against the Teensy harness's ≈4 s. For verification runs
that is a fine price.

One limitation follows from the same property: the Mega **cannot
generate** simultaneous multi-line slew stress, so coupling
characterisation (chase tests, boundary hammers at full aggression) is
Teensy-harness work; on the Mega those tests are gentle by construction.

---

## 2. Wiring

Ports live on the Mega's 36-pin double-row header, split by row:

| Virtual port | Mega pins (bit 0 → bit 7) |
|---|---|
| Port A | 22, 24, 26, 28, 30, 32, 34, 36 (evens) |
| Port B | 38, 40, 42, 44, 46, 48, 50, 52 (evens) |
| Port C | 23, 25, 27, 29, 31, 33, 35, 37 (odds) |
| Port D | 39, 41, 43, 45, 47, 49, 51, 53 (odds) |

Port E (strobes): pins 5–8 for PE0–PE3, PE0 = CLK by convention (pins
9–12 are reserved as PE4–PE7 for compile compatibility; wire only if a
project needs them). Ribbon colours: bit 0 = white … bit 7 = red.

See `MegaDirect_Wiring.svg` for the picture.

Power: the Mega's 5 V may feed a small DUT; power larger DUTs externally
and share ground only. Always common the grounds.

**Buttons:** SW0 → pin 2, SW1 → pin 3, `INPUT_PULLUP`, momentary to GND.
Unwired = idle unpressed; every button function in the harnesses has a
serial equivalent, so wiring them is optional.

No level shifters, no terminations, no board pull-ups: what the Mega
drives is what the DUT sees. There is no direction hardware either —
"direction" on the Mega is just `pinMode`, and the Teensy-only
`portDrive`/`portRelease` API does not exist on this platform.

---

## 3. Software — One Sketch Folder, Two Boards

`AdapterPorts.h` selects the target from the compiler
(`__IMXRT1062__` vs `__AVR_ATmega2560__`) — pick the board in the Arduino
IDE's Tools menu, nothing else changes. The Mega section provides the same
API plus shims:

- `digitalWriteFast` / `digitalReadFast` → plain `digitalWrite` /
  `digitalRead`.
- `delayNanoseconds(ns)` → `delayMicroseconds`, **rounded up to whole
  µs**. Nanosecond-resolution experiments quantise to 1 µs steps on the
  Mega; use whole-µs values and don't read sub-µs meaning into them.
- `SW_PIN_MODE` = `INPUT_PULLUP`; `PLATFORM_NAME` — print it in the boot
  banner so logs identify the board.

### AVR-specific sketch rules

- **Wrap string literals in `F(...)`** — AVR string literals otherwise
  live in the 8 KB SRAM; a report-heavy harness will exhaust it.
  Harmless on the Teensy, mandatory habit for dual-target sketches.
- **`Serial` is a real UART.** The baud passed to `Serial.begin()`
  matters (115200 = the convention) and throughput is ~11 KB/s. Printing
  **can** be the bottleneck: change-detection reporting is essential, and
  per-vector prints inside test loops will dominate runtime.
  `while (!Serial ...)` is always true on AVR — keep the bounded wait for
  source compatibility.
- The unrolled port accessors stay unrolled for API compatibility, but on
  the Mega each bit costs a full `digitalWrite` (~4–5 µs).

### Timing profile

| Operation | Teensy 4.1 harness | Mega 2560 |
|---|---|---|
| Single pin write/read | ~1–2 ns | ~4–5 µs |
| 8-bit port write | ~10 ns, simultaneous | ~35–40 µs, bit-serial |
| Full 262,144-vector run | ≈ 4 s | ≈ 56 s |
| Clock pulse width (commanded 1 µs) | 1 µs | 1 µs + ~10 µs pin overhead |

---

## 4. Choosing the Platform

| Task | Platform |
|---|---|
| Final verification of a DUT ("is it correct?") | **Mega** first, or Teensy cross-checked against it |
| Fast iteration while debugging DUT logic | Teensy harness (seconds per exhaustive run) |
| Signal-integrity stress characterisation (simultaneous slews) | Teensy harness — only it can produce the aggressor |
| Sub-µs timing experiments | Teensy — the Mega quantises to µs |
| A result that must be believed | Whichever two platforms agree on |

The standing rule from the ALU campaign: all runtime settings reset on
re-flash — the boot banner prints the platform and any settings whose
defaults matter (clock polarity!); read it before trusting a run. And a
DMM reading "5.00 V" says nothing about nanosecond transients: wrong
latched values at clock edges are a signal-integrity symptom even when
every rail meters perfectly.
