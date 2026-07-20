# LVC Harness Board — Teensy 4.1 + HCT245/LVC245 Pairs

The project's Teensy-based test harness for 5 V TTL circuits: five 8-bit
ports behind directed, continuously-driven level shifters, two buttons, and
per-port direction policy. **All 42 signals live on the Teensy's
through-hole pins 0–41 — no bottom pads are used.**

This document matches the wired schematic (`Teensy_Adapter.ttlproj`)
one-for-one. Companions: `MegaDirect_Reference.md` (the independent
verification platform), `LVCBoard_Wiring.svg` (the picture),
`Teensy41_TTLSim_PinTable.md` (the schematic symbol ↔ GPIO bridge).
Software: `AdapterPorts.h` (selects the platform from the compiler).

---

## 1. Level Shifting — A Chip Pair Per Port

The Teensy is 3.3 V and not 5 V tolerant; the DUTs are 5 V HC/HCT logic.
Each direction gets the chip that does it correctly:

- **Read (DUT → Teensy): SN74LVC245AN at 3.3 V.** 5.5 V-tolerant inputs,
  clean 3.3 V out, ±24 mA drive, 6.3 ns max tpd, Ioff protection during
  power ramps. Genuine TI PDIP-20, ACTIVE.
- **Drive (Teensy → DUT): 74HCT245 at 5 V.** TTL-threshold inputs
  (VIH 2.0 V) read the Teensy's 3.3 V as solid high; outputs are full 5 V
  push-pull. (Plain HC245 cannot serve here — 3.3 V is below HC's 3.5 V
  VIH at 5 V — and an LVC cannot drive 5 V. The pair is the point.)

Every port A–E is such a pair. DIR pins are strapped permanently (HCT:
A→B; LVC: B→A); direction is switched by **/OE**, complementary per port
from the port's OE net via one section of a shared **74HCT04** (U2):

| OEx net | HCT /OE (inverted) | LVC /OE (direct) | Result |
|---|---|---|---|
| LOW (default, pulldown) | HIGH → disabled | LOW → enabled | Port **reads** |
| HIGH | LOW → enabled | HIGH → disabled | Port **drives** |

The chips' outputs live on opposite sides — HCT toward the DUT, LVC toward
the Teensy — so mutual contention is impossible and /OE overlap during
switching is harmless. Both static drive states are strong and continuous:
no auto-direction circuitry, no weak keepers, no one-shot edge detectors
anywhere in the signal path.

Chips: 5 × 74HCT245 (U3–U7), 5 × SN74LVC245AN (U8–U11, U15), 1 × 74HCT04
(U2) = **11 DIPs** plus the Teensy.

HCT note: inputs held at 3.3 V draw a small extra ICC (TTL-level delta,
~mA class board-wide). Normal; the per-chip 100 nF and solid 5 V cover it.

---

## 2. Teensy Pin Assignment (GPIO; all 42 through-hole pins)

| Signal group | Teensy pins | Notes |
|---|---|---|
| Port A bits 0–7 | 0–7 | |
| Port B bits 0–7 | 8–12, 32, 14, 15 | PB5 = pin 32 |
| Port C bits 0–7 | 16–23 | Runtime direction (DIR0) |
| Port D bits 0–7 | 24–31 | Runtime direction (DIR1) |
| Port E bits 0–7 | 13, 35–41 | **PE0 = CLK = pin 13 — onboard LED blinks with the clock.** PE6/PE7 shared with SW0/SW1 |
| DIR0 (→ port C) | 34 | |
| DIR1 (→ port D) | 33 | |

Ribbon colours: bit 0 = white … bit 7 = red per port. The schematic
symbol models the physical DIP-48 (pin numbers = solder positions); the
mapping above is mirrored in `AdapterPorts.h` and the pin-table doc.

Power: always USB; Vin is hard-wired to the board's 5 V rail. The 3.3 V
rail comes from the Teensy's onboard regulator (250 mA budget; five
LVC245s are a light load).

---

## 3. Direction Policy (as built)

A deliberate split, not a compromise:

- **Ports A, B, E — static, one jumper each** (J8, J12, J15; centre on the
  port's OE net, other side VCC, third pin = shunt parking). Fitted = the
  port drives; parked or absent = the pulldown holds the port reading.
  One jumper, one source: no combination of shunts can short sources.
- **Ports C, D — runtime, hard-wired** to DIR0 (Teensy 34) and DIR1
  (Teensy 33). High = drive, low = read; R3/R4 pulldowns = read at
  power-up, so an unprogrammed board reads on all five ports.

Consequences to plan looms around: dynamic buses (anything doing
write-then-read turnarounds — memory data buses, CPU data buses,
peripheral-chip ports) go to **C and D**; static stimulus and observation
go to A/B/E. Port E's jumper additionally never lets a runtime bit
tri-state the clock path — clock-drive is a physical commitment.

**The sketch must match the jumpers.** Print the assumed configuration in
the boot banner and read it; all runtime settings reset on re-flash.

---

## 4. Per-Port Wiring ('245 pinout: DIR=1, A=2–9, GND=10, B8–B1=11–18, /OE=19, VCC=20)

For each port:

| Signal | Connect to |
|---|---|
| HCT245 VCC | 5 V, 100 nF at pin |
| HCT245 DIR | VCC (strap A→B; U7 via R5) |
| HCT245 /OE | U2 section output (= NOT OEx) |
| HCT245 A1–A8 | Teensy port pins (bit 0 → A1) |
| HCT245 B1–B8 | Port connector **through the 100 Ω pack** |
| LVC245 VCC | Teensy 3.3 V rail, 100 nF at pin |
| LVC245 DIR | GND (strap B→A) |
| LVC245 /OE | OEx net directly |
| LVC245 B1–B8 | Same nodes as HCT B (chip side of termination) |
| LVC245 A1–A8 | Same Teensy pins as HCT A |
| OEx net | Its 10 k pulldown (R1–R4, R10) + its selector (§3) |

**Termination networks:** 100 Ω × 8 isolated DIP-16 (RN5–RN9), one per
port. The pack — and TTLSim's `resnet-dip16` symbol — pairs **element k =
pins k ↔ 17−k** (straight across). Chip side on pins 1–8, header side on
pins 16–9, bit order arriving straight at the connectors. Visual check on
the schematic: every used element row has a wire on *both* sides.

**B-side pulls:** a SIP-9 10 k pack per data port (RN1–RN4) and for port E
(RN13), common pin jumperable to 5 V / open / GND (J1–J4, J11), elements
on the DUT-side nodes — the defined idle for tri-state DUT buses, and the
level during C/D bus turnarounds.

---

## 5. Connectors

| Connector | Contents |
|---|---|
| H1–H4 | 8-pin: ports A–D bits 0–7, white → red |
| H5–H12 | 2-pin pairs: PE0(CLK)…PE7 + GND. Signal on **pin 2**, ground pin 1. Twist each pair |
| H13, H14 | 2-pin: +5 V pairs, fed through J5 |
| H15, H16 | 2-pin: GND pairs |
| J5 | Board-powers-DUT jumper. **Never closed while the DUT has an external supply** (VUSB back-feed). External-supply DUTs share ground only |

---

## 6. Buttons / Shared Channels (J6, J7)

Teensy pins 40/41 are dual-role, selected by 3-pin jumpers (centre-common):

- **Centre:** Teensy 40 (J6) / 41 (J7), via the SW bus.
- **Position 1 — button:** through **1 k** (R6/R7) to SW0/SW1, momentary
  to GND, internal pull-ups. SW0 = start/advance, SW1 = abort/exit.
- **Position 3 — port bit:** PE6/PE7 (a startBit-6 tap on the PE bus).
  **100 k pulldowns** (R8/R9) keep the shifter inputs defined in button
  position.

The buttons sit on the Teensy side of the shifters, so they work
identically whether port E drives or reads. The 1 k makes a jumper/sketch
mismatch (pin driven as a port bit while a pressed button grounds it)
harmless. With E driving in button configuration, U7's channels 7/8 output
constant low at H11/H12 — expected, not a fault.

---

## 7. Software — AdapterPorts.h

Platform is selected by the compiler: `__IMXRT1062__` = this board,
`__AVR_ATmega2560__` = the Mega. The header provides `PIN_PA0`..`PIN_PE7`,
port tables, `setPortMode`, unrolled fast accessors, `samplePorts()`
(A–D), `readPortE`/`writePortE`, `sw0Pressed`/`sw1Pressed`, and on the
Teensy the direction API:

- `initPortDirections()` — first in `setup()`: DIR0/DIR1 low (C/D reading,
  matching power-up), all port pins inputs. Then configure per the
  jumpers and banner-declare.
- `portDrive('C'|'D')` / `portRelease('C'|'D')` — the runtime turnaround,
  ~10 ns class. A/B/E have no runtime direction by design.

---

## 8. Verification & Bring-Up (in order)

**Stage 0 — bare-Teensy pin verification (before assembly):** walk every
pin with LED indicators — 8-bit LED Thing bars are ideal (1 µA floating
sense inputs, zero pad loading; power them from the Teensy's 3.3 V;
ground unused sense inputs). PortChaser walks A–E; check DIR0/DIR1 and
the buttons with the template's change reporting. This verifies
`AdapterPorts.h` against silicon; re-running the identical walks through
the assembled board isolates any later discrepancy to the board itself.

1. Bench-test one HCT+LVC pair on protoboard: 3.3 V in → clean 5 V out,
   5 V in → clean 3.3 V out, /OE handover both orders.
2. Power-up state: unprogrammed Teensy, jumpers parked → every port's DUT
   side high-Z (pulldowns doing their job).
3. Chaser through the board on all five ports at the connectors.
4. Boundary hammer against a bench '574: `B DF E0` and `B DF E0 1000 FF`
   through the real loom, CLK on PE0. Acceptance 0/500 both sides — the
   simultaneous-slew stress that phantom-clocks registers through long
   looms is the thing this board's drive path was designed to shrug off.
5. The ALU battery (`C`,`P`,`T`,`I`,`F`) as full-system proof,
   cross-checked once against the Mega (`MegaDirect_Reference.md`).

Until step 5 matches the Mega, the Mega remains the platform of record.

Open sim-model check: the `button-4` part's terminal pairing (signal
pin 3, ground pin 2) — verify once by pressing SW0 in TTLSim.

---

## 9. Parts Summary (matches schematic designators)

- U1: Teensy 4.1 (through-hole pins 0–41 only)
- U2: 74HCT04 (OEx → HCT /OE inversion; unused input grounded)
- U3–U7: 74HCT245 (drive A, B, C, D, E)
- U8–U11, U15: SN74LVC245AN (read A, B, C, D, E)
- RN1–RN4, RN13: SIP-9 10 k pulls + select jumpers J1–J4, J11
- RN5–RN9: 100 Ω × 8 isolated DIP-16 terminations (element k = pins k, 17−k)
- R1–R4, R10: 10 k OE-net pulldowns; R5: 10 k U7 DIR strap
- R6, R7: 1 k button legs; R8, R9: 100 k shared-channel pulldowns
- C1–C10: 100 nF per chip; C11: 100 µF bulk
- SW0, SW1: momentary buttons; J6, J7: button/port-bit select
- J8, J12, J15: drive-enable jumpers (A, B, E); C/D hard-wired to DIR0/DIR1
- H1–H4 (ports), H5–H12 (strobe pairs), H13–H16 (power), J5 (DUT power)
