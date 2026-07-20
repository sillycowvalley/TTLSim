# LVC Harness Board — Teensy 4.1 + HCT245/LVC245 Pairs

Design reference for the purpose-built successor to the RetroShield-derived
adapter. Same role (Teensy-driven test harness for 5 V TTL), with the
TXS0108E auto-direction shifters — the proven root cause of the adapter's
signal-integrity failures — replaced by directed, continuously-driven
level shifting. Own form factor; nothing inherited from the Mega layout.
**All 42 signals live on the Teensy's through-hole pins 0–41 — no bottom
pads are used.**

I/O complement: 32 bidirectional data lines (ports A–D, independent
direction control per port) + 4 fixed strobes + 2 jumper-shared
button/strobe channels.

This document matches the wired schematic (`Teensy_Adapter.ttlproj`)
one-for-one. Companions: `TeensyAdapter_Reference.md`,
`MegaDirect_Reference.md`, `LVCBoard_Wiring.svg`,
`Teensy41_TTLSim_PinTable.md`. Software: the three-platform
`AdapterPorts.h` (`#define LVC_HARNESS_BOARD`).

---

## 1. Level Shifting — Why a Chip Pair Per Port

The read-side part is the **SN74LVC245AN** (genuine TI PDIP-20, ACTIVE).
It runs at 3.3 V only, with 5.5 V-tolerant I/O — exactly half the solution:

- **Read (DUT → Teensy): LVC245 at 3.3 V.** 5 V HC levels into tolerant
  inputs, clean 3.3 V out. ±24 mA drive, 6.3 ns max tpd, Ioff protection
  during power ramps.
- **Drive (Teensy → DUT): 74HCT245 at 5 V.** The LVC's 3.3 V high is
  below 74HC's VIH (3.5 V at 5 V) — the marginal-threshold failure class
  this project has sworn off. HCT's TTL-threshold inputs (VIH 2.0 V) read
  3.3 V as solid high; outputs are full 5 V push-pull.

Each data port (A–D) is a pair: HCT245 (drive) + LVC245 (read). DIR pins
strapped permanently (HCT: A→B to VCC; LVC: B→A to GND); direction is
switched by **/OE**, complementary from one Teensy DIR line via one section
of a shared **74HCT04**:

| DIR line | HCT /OE (inverted DIR) | LVC /OE (DIR direct) | Result |
|---|---|---|---|
| LOW (power-up, pulldown) | HIGH → disabled | LOW → enabled | Board **reads** |
| HIGH | LOW → enabled | HIGH → disabled | Teensy **drives** |

The chips' outputs are on opposite sides — HCT drives the DUT, LVC drives
the Teensy — so mutual contention is impossible and /OE overlap during
switching is harmless. (TI's suggested /OE pull-up for power-up high-Z is
deliberately not used on the read chips: enabled-at-power-up drives toward
the Teensy, whose pins reset as inputs — that *is* the safe state.)

**Port E** (strobes): one HCT245 (U7), DIR strapped high via R5, /OE
grounded. Channels 1–4 = fixed strobes PE0–PE3; channels 5–6 = the
jumper-shared pairs (§5); channels 7–8 dormant, inputs A7/A8 grounded.

Chips: 4 × (HCT245 + LVC245) + 1 × HCT245 + 1 × HCT04 = **10 DIPs**.

HCT note: inputs held at 3.3 V draw a small extra ICC (TTL-level delta,
~mA class board-wide). Normal; the per-chip 100 nF and solid 5 V cover it.

---

## 2. Teensy 4.1 Pin Assignment (all 42 through-hole pins, 0–41)

Pins 13 and 32 are swapped between ports E and B so the onboard LED
(pin 13) sits on output-only PE0/CLK — a free clock activity light.

| Signal group | Teensy pins | Notes |
|---|---|---|
| Port A bits 0–7 | 0–7 | |
| Port B bits 0–7 | 8–12, 32, 14–15 | PB5 = pin 32 |
| Port C bits 0–7 | 16–23 | |
| Port D bits 0–7 | 24–31 | |
| Port E bits 0–3 | 13, 33, 34, 35 | **PE0 = CLK = pin 13** (LED = clock light) |
| DIR A, B, C, D | 36, 37, 38, 39 | 10 k pulldowns (R1–R4) |
| SW1 / PE4 | 40 | Dual role via J6 (§5) |
| SW2 / PE5 | 41 | Dual role via J7 (§5) |

Bottom pads 42–54 entirely unused. Teensy 3.3 V pins feed the LVC245s and
are otherwise unconnected in the schematic by intent. Vin is hard-wired to
the 5 V rail (always USB-powered; Vin sources USB 5 V).

Ribbon colours: bit 0 = white … bit 7 = red per port.

---

## 3. Per-Port Wiring ('245 pinout: DIR=1, A=2–9, GND=10, B8–B1=11–18, /OE=19, VCC=20)

For each data port A–D:

| Signal | Connect to |
|---|---|
| HCT245 VCC | 5 V, 100 nF at pin |
| HCT245 DIR | VCC (strap A→B) |
| HCT245 /OE | HCT04 output (= NOT DIR) |
| HCT245 A1–A8 | Teensy port pins (bit 0 → A1) |
| HCT245 B1–B8 | Port header **through the 100 Ω pack** |
| LVC245 VCC | Teensy 3.3 V rail, 100 nF at pin |
| LVC245 DIR | GND (strap B→A) |
| LVC245 /OE | DIR line directly |
| LVC245 B1–B8 | Same nodes as HCT B (chip side of termination) |
| LVC245 A1–A8 | Same Teensy pins as HCT A |
| DIR line | Teensy 36–39, 10 k pulldown (R1–R4) |

**Termination networks (verified fact, 2026-07):** TTLSim's
`resnet-dip16` — and the physical isolated 8×100 Ω DIP-16 — pairs
**element k = pins k ↔ k+8** (1–9, 2–10 … 8–16). Chip side on pins 1–8,
header side on pins 9–16, straight order. (Early drafts assumed k ↔ 17−k;
that wiring is bit-reversed — do not resurrect it.)

**B-side pulls per data port:** SIP-9 10 k (RN1–RN4), common via 3-pin
jumper J1–J4 to 5 V / open / GND, elements onto the B nodes — the
tri-state-bus idle that `INPUT_PULLUP` can no longer provide through
directed shifters. Fit/strap per project.

---

## 4. Connectors

| Connector | Contents |
|---|---|
| H1–H4 | 8-pin: ports A–D bits 0–7 (signal order verified straight through the k↔k+8 packs) |
| H5–H8 | 2-pin pairs: PE0(CLK)/PE1/PE2/PE3 + GND. Signal on **pin 2**, ground pin 1. Twist each pair |
| H9, H10 | 2-pin pairs: shared strobe channels PE4/PE5 (active when J6/J7 select strobe), same pin convention |
| H13 | 4 × +5 V, fed through J5 |
| H14 | 4 × GND |
| J5 | Board-powers-DUT jumper. **Never closed while the DUT has an external supply** (VUSB back-feed). External-supply DUTs share ground only |

(H11/H12 were removed during design; the designator gap is historical.)

---

## 5. Buttons / Shared Strobe Channels (J6, J7)

Teensy pins 40/41 are dual-role, selected by 3-pin jumpers (centre-common,
same idiom as J1–J4):

- **Centre (pin 2):** Teensy 40 (J6) / 41 (J7).
- **Position 1 — button (default):** through **1 k** (R6/R7) to SW1/SW2,
  momentary to GND, internal pull-ups (`SW_PIN_MODE = INPUT_PULLUP`).
  SW1 = start/advance, SW2 = abort/exit.
- **Position 3 — strobe:** to U7's A5/A6 → termination elements 5/6 →
  H9/H10. **100 k pulldowns** (R8/R9) on the A5/A6 nodes so the HCT inputs
  never float when the jumpers sit in button position.

The 1 k exists for the mismatch case (sketch drives pin 40 as a strobe
while the jumper points at a pressed button): 3.3 mA instead of a dead
short. **The sketch must match the jumpers — print the assumed
configuration in the boot banner** and read it; this is the settings-reset
lesson in hardware form. Software uses the `PIN_PE4`/`PIN_PE5` aliases as
plain outputs in strobe configuration.

---

## 6. Software — AdapterPorts.h, Third Platform

Both Teensy boards compile as `__IMXRT1062__`; select the variant by
uncommenting one line at the top of `AdapterPorts.h`:

```cpp
//#define LVC_HARNESS_BOARD
```

The LVC section provides the standard API (`PIN_PA0`..`PIN_PD7`, tables,
unrolled accessors, `samplePorts()`, SW1/SW2) plus:

- `PIN_PE0`..`PIN_PE3`, `PORT_E_PINS[4]`, `readPortE()` / `writePortE(v)`
  (low nibble), and the `PIN_PE4`/`PIN_PE5` aliases for jumpered strobes.
- `PIN_DIR_A`..`PIN_DIR_D` and the direction API:
  - `initPortDirections()` — first in `setup()`: DIR low (all reading,
    matching power-up), Port E driving, strobes 0.
  - `portDrive('A'..'D')` — DIR high, then port pins OUTPUT.
  - `portRelease('A'..'D')` — port pins INPUT, then DIR low.

Turnaround: during `portDrive` the DUT bus is briefly undefined (~µs)
between /OE handover and the pins driving — same as any real bus
turnaround; the B-side pulls define it. Existing sketches (ALUHarness,
PortTemplate, PortChaser) compile unchanged.

---

## 7. Verification & Bring-Up (in order)

**Stage 0 — bare-Teensy pin verification (before the board exists):**
walk every pin with LED indicators — 8-bit LED Thing bars are ideal
(1 µA floating sense inputs, zero pad loading; power from the Teensy's
3.3 V so thresholds match; ground unused sense inputs on 4-wide walks).
Extend PortChaser with an E-walk, a DIR-group walk, and button echo, built
with `LVC_HARNESS_BOARD` — and read the banner. This verifies
`AdapterPorts.h` against silicon; re-running the identical walks through
the assembled board then isolates any discrepancy to the board itself.

1. Bench-test one HCT+LVC pair: 3.3 V in → 5 V out, 5 V in → 3.3 V out,
   /OE handover both orders.
2. Power-up state: unprogrammed Teensy → all data-port DUT sides high-Z,
   Port E driving.
3. Chaser through the board on all ports; PE0 activity mirrors on the
   onboard LED.
4. Boundary hammer vs a bench '574: `B DF E0` and `B DF E0 1000 FF`, CLK
   on PE0, no conditioner. Acceptance 0/500 both sides (the mask-FF chase
   scored ~90% phantom clocks on the TXS path).
5. ALU battery (`C`,`P`,`T`,`I`,`F`), cross-checked once against the Mega.

Until step 5 matches the Mega, the Mega remains the platform of record.

Open model assumption (sim only): the `button-4` part's terminal pairing
(signal pin 3, ground pin 2) — verify once by pressing SW1 in TTLSim.

---

## 8. Parts Summary (matches schematic designators)

- U1: Teensy 4.1 (through-hole pins 0–41 only)
- U2: 74HCT04 (DIR → HCT /OE; unused inputs grounded)
- U3–U7: 74HCT245 (drive A, B, C, D, E)
- U8–U11: SN74LVC245AN (read A, B, C, D)
- RN1–RN4: SIP-9 10 k pulls + J1–J4 select jumpers
- RN5–RN9: 100 Ω × 8 isolated DIP-16 (terminations A–E; RN9 elements
  7/8 spare)
- R1–R4: 10 k DIR pulldowns; R5: 10 k Port E DIR strap
- R6, R7: 1 k button legs; R8, R9: 100 k shared-channel pulldowns
- C1–C10: 100 nF per chip; C11: 100 µF bulk
- SW1, SW2: momentary buttons; J6, J7: 3-pin role-select jumpers
- H1–H4 (8-pin ports), H5–H10 (strobe pairs), H13/H14 (power), J5
