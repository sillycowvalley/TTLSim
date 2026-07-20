# Arduino Mega 2560 (Direct) — Reference for Test-Harness Sketches

Companion to `TeensyAdapter_Reference.md`. The Mega 2560, wired directly into
the same header positions the Teensy adapter presents, is the project's
**verification platform of record** for 5 V TTL circuits. Established
empirically (2026-07): the same DUT that failed 41% of full-speed vectors
through the Teensy/TXS path passed 262,144/262,144 twice on the Mega —
with and without clock conditioning. When a result must be trusted, run it
on the Mega.

---

## 1. Why the Mega Is the Ground Truth

| Property | Teensy 4.1 + adapter | Mega 2560 direct |
|---|---|---|
| Logic level | 3.3 V shifted to 5 V via TXS0108E | Native 5 V |
| Static drive | Weak TXS keeper (~4 kΩ effective) between edges | Strong CMOS push-pull, ~20 mA class, continuously |
| Edge rate | Sub-ns (one-shot accelerated) | Few ns, unassisted |
| Port write | 8 lines slew simultaneously (~ns apart) | Inherently bit-serial, ~4–5 µs per line |
| Series terminations | 33 Ω on board (some lines) | None (and none needed at these edge rates) |
| Signal-integrity failure modes observed | Phantom clocks from simultaneous slew; keeper/divider interactions | None observed |

Every residual signal-integrity failure in the ALU campaign was a property of
the Teensy-side drive path, not the DUT. The Mega's combination of strong
static drive and naturally serialized slew removes the entire class.

The trade is speed: the Mega runs the same exhaustive test ~14× slower
(≈56 s vs ≈4 s for 262,144 vectors). For verification runs that is a fine
price. Use the Teensy path for speed once its known issues are addressed
(see §6).

---

## 2. Wiring — RetroShield Form-Factor Equivalence

The adapter is a RetroShield-format board: its J1 signals sit at Arduino
Mega header positions. The Mega therefore **drops into the same wiring with
zero changes** — the DUT loom does not move. Verified against the adapter
mapping doc (2020-08-06) and the chaser-verified `AdapterPorts.h`.

Virtual-port map (see `MegaDirect_Wiring.svg` for the picture):

| Virtual port | Mega pins (bit 0 → bit 7) |
|---|---|
| Port A | 22, 24, 26, 28, 30, 32, 34, 36 (header evens) |
| Port B | 38, 40, 42, 44, 46, 48, 50, 52 (header evens) |
| Port C | 23, 25, 27, 29, 31, 33, 35, 37 (header odds) |
| Port D | 39, 41, 43, 45, 47, 49, 51, 53 (header odds) |

Ribbon colour convention unchanged: bit 0 = white … bit 7 = red.

Power: the dual-row header ends carry a +5 V pair and a GND pair, exactly as
J1 does. Always common the grounds. The Mega's 5 V may feed a small DUT;
power larger DUTs externally and share ground only.

**Buttons:** the adapter's SW1/SW2 do not exist off-adapter. On the Mega,
`AdapterPorts.h` maps SW1 → pin 2 and SW2 → pin 3 with `INPUT_PULLUP`
(active-low, switch to GND). Unwired, they idle unpressed — every button
function in the harnesses has a serial equivalent, so wiring them is
optional.

No TXS, no terminations, no board pull-ups: what the Mega drives is what the
DUT sees.

---

## 3. Software — One Sketch Folder, Two Boards

`AdapterPorts.h` is dual-platform and selects the target from the compiler
(`__IMXRT1062__` vs `__AVR_ATmega2560__`) — pick the board in the Arduino
IDE's Tools menu, nothing else changes. The Teensy section is the original
chaser-verified mapping, unchanged. The Mega section provides the same API
plus three shims:

- `digitalWriteFast` / `digitalReadFast` → plain `digitalWrite` /
  `digitalRead` (the FASTREAD pattern).
- `delayNanoseconds(ns)` → `delayMicroseconds`, **rounded up to whole µs**.
  Nanosecond-resolution experiments (chase delays) quantize to 1 µs steps on
  the Mega; use whole-µs values and don't read sub-µs meaning into them.
- `SW_PIN_MODE` — `INPUT` on the adapter (board pull-ups), `INPUT_PULLUP`
  on the Mega. Sketches use `pinMode(PIN_SW1, SW_PIN_MODE)`.
- `PLATFORM_NAME` — print it in the boot banner so logs identify the board.

### AVR-specific sketch rules

- **Wrap string literals in `F(...)`** — `Serial.print(F("..."))`. AVR
  string literals otherwise live in the 8 KB SRAM; a report-heavy harness
  will exhaust it. Harmless on the Teensy, mandatory habit for dual-target
  sketches.
- **`Serial` is a real UART.** The baud passed to `Serial.begin()` matters
  (115200 = the convention), and throughput is ~11 KB/s — roughly 2,000×
  slower than the Teensy's native USB. Printing **can** be the bottleneck on
  the Mega: change-detection reporting is essential, and per-vector prints
  inside test loops will dominate runtime. `while (!Serial ...)` is harmless
  (always true on AVR) — keep the bounded wait for source compatibility.
- No `digitalWriteFast` speed to protect: the unrolled port accessors stay
  unrolled for API compatibility, but on the Mega each bit costs a full
  `digitalWrite`.

### Timing profile (measured / derived)

| Operation | Teensy 4.1 | Mega 2560 |
|---|---|---|
| Single pin write/read | ~1–2 ns | ~4–5 µs |
| 8-bit port write | ~10 ns, simultaneous | ~35–40 µs, bit-serial |
| Full 262,144-vector run | ≈ 4 s | ≈ 56 s (~210 µs/vector) |
| Clock pulse width (commanded 1 µs) | 1 µs | 1 µs + ~10 µs of pin-write overhead |

Consequences: the Mega behaves as if the harness's bit-serial write mode
(`V ~5`) were permanently on — a signal-integrity feature, but it means the
Mega **cannot generate** the eight-simultaneous-line stress events used to
characterize coupling on the Teensy path. Coupling stress tests (the chase)
are a Teensy-side instrument; on the Mega they are gentle by construction.

---

## 4. Choosing the Platform

| Task | Platform |
|---|---|
| Final verification of a DUT ("is it correct?") | **Mega** — strong drive, native 5 V, proven clean |
| Fast iteration while debugging DUT logic | Teensy (4 s exhaustive runs), gentle-timing modes |
| Signal-integrity stress characterization (chase, simultaneous slew) | Teensy — only it can produce the aggressor |
| Sub-µs timing experiments | Teensy — Mega quantizes to µs |
| Anything where a failure must be believed | Mega first; only trust Teensy full-speed results after §6 |

A discrepancy between the two platforms on the same DUT is itself
information: Mega-clean + Teensy-dirty = drive-path signal integrity, not
the DUT. That comparison closed the ALU campaign.

---

## 5. Campaign-Validated Facts (ALU project, 2026-07)

Kept here so they aren't re-derived:

1. **Simultaneous multi-line slews through the ~35 cm loom can phantom-clock
   '574 registers** driven from the Teensy path — up to ~90% per event with
   8 lines, regardless of how long after the real clock edge the slew occurs.
   Single-line slews are far weaker aggressors. Diagnosis method: the chase
   test (slew inputs N ns after the edge; exact re-captures prove a phantom
   capture event).
2. **The TXS static weak keeper forms voltage dividers with any external
   resistor network.** A 10 k pull-up against the keeper parks a node at
   ~1.6 V — mid-threshold. Any RC/Schmitt conditioning on a TXS-driven line
   must budget for the keeper: pull-ups ≥100 k, and park the line in its
   solidly driven state between operations (idle polarity matters).
3. **A Schmitt clock conditioner ('HC14 + ~1 k / 100 pF RC at the DUT,
   RC ground at the chip) kills coupled runts** — 95% → ~1% on the worst
   stress — but is only needed for the weak-drive path. With the Mega's
   push-pull drive the bare clock line is clean.
4. **All harness runtime settings (G/U/V/Y/K) reset on re-flash.** The boot
   banner prints platform and clock polarity; read it before trusting a run.
5. The DUT itself, once its genuine board faults were fixed (decoupling,
   supply, one chip's enable orbit), was correct: 262,144/262,144, twice.

---

## 6. Reinstating the Teensy Path (when speed is wanted)

Deferred work, with the before/after meters already built (boundary hammer
`B DF E0` / `B DF E0 1000 FF`, and the full test's failure count):

1. Refit the 'HC14 conditioner (schematic: `ClockConditioner.ttlproj`), with
   its input pull-up **100 k, not 10 k** (fact §5.2), and set `K 1`.
2. Dress the conditioned clock run away from operand/result wiring.
3. Verify: hammer 0/500 both sides at several chase delays, then the full
   battery clean at full speed.

Until then, the Teensy path is for gentle-timing work and stress
instrumentation only.
