# LVC Harness Board — Teensy 4.1 + HCT245/LVC245 Pairs, 40 Data Lines

Design reference for the purpose-built successor to the RetroShield-derived
adapter. Same role (Teensy-driven test harness for 5 V TTL), with the
TXS0108E auto-direction shifters — the proven root cause of the adapter's
signal-integrity failures — replaced by directed, continuously-driven
level shifting, and the line count grown from 32 to 40. Its own form
factor and connectors: nothing is inherited from the Mega/RetroShield
layout.

Companion documents: `TeensyAdapter_Reference.md` (the old adapter),
`MegaDirect_Reference.md` (the verification platform), `LVCBoard_Wiring.svg`
(the picture), `Teensy41_TTLSim_PinTable.md` (the editor part). Software:
the three-platform `AdapterPorts.h` (`#define LVC_HARNESS_BOARD`).

---

## 1. Level Shifting — Why a Chip Pair Per Port

The sourced through-hole part is the **74LVC245** (single-supply). It runs
at 3.3 V only (LVC maxes at 3.6 V), with 5 V-tolerant I/O — which makes it
exactly half of the solution:

- **Read direction (DUT → Teensy): LVC245 at 3.3 V.** 5 V HC levels into
  its tolerant inputs, clean 3.3 V out to the Teensy. Perfect.
- **Drive direction (Teensy → DUT): it cannot serve.** Its 3.3 V high is
  *below* 74HC's VIH of 3.5 V at 5 V — the marginal-threshold class of
  failure this project just finished exorcising. The drive side needs a
  **74HCT245 at 5 V**: TTL-threshold inputs (VIH 2.0 V) read the Teensy's
  3.3 V as solid high, and the outputs are full 5 V push-pull.

So each data port (A–D) is a pair: HCT245 (drive) + LVC245 (read). Both
DIR pins are strapped permanently (HCT: A→B, LVC: B→A); direction is
switched by the **/OE pins**, driven complementarily from one Teensy DIR
line through one section of a shared **74HCT04** inverter:

| DIR line | HCT /OE (= inverted DIR) | LVC /OE (= DIR) | Result |
|---|---|---|---|
| LOW (power-up, pulldown) | HIGH → disabled | LOW → enabled | Board **reads** |
| HIGH | LOW → enabled | HIGH → disabled | Teensy **drives** |

The two chips' outputs live on opposite sides — HCT drives the DUT, LVC
drives the Teensy — so they can never contend with each other, and /OE
overlap during switching is harmless by construction.

**Port E** (strobes) is drive-only: **one HCT245**, DIR strapped A→B,
/OE grounded. No LVC partner.

Chip count: 4 × (HCT245 + LVC245) + 1 × HCT245 + 1 × HCT04 = **10 DIPs**.

Electrical notes: HCT245 rated ±8 mA per line (stiffer in practice),
LVC245 ±24 mA — both continuous push-pull, nothing for crosstalk to
re-trigger. HCT245 and HCT04 are genuine in-production DIP parts; DIP
LVC245 is aftermarket (LVC never officially shipped in DIP), so verify the
vendor pinout against the datasheet and confirm 5 V-tolerant A-grade I/O,
then bench-test one before soldering four.

---

## 2. Teensy 4.1 Pin Assignment (all 48 of pins 0–47)

Contiguous runs — every through-hole pin 0–41 is plain GPIO on the 4.1.
Bottom pads 48–54 (QSPI group) stay free for possible future PSRAM.

| Signal group | Teensy pins | Notes |
|---|---|---|
| Port A bits 0–7 | 0–7 | |
| Port B bits 0–7 | 8–12, 32, 14–15 | PB5 = pin 32 (swapped with 13 — see Port E) |
| Port C bits 0–7 | 16–23 | |
| Port D bits 0–7 | 24–31 | |
| Port E bits 0–7 | 13, 33–39 | Strobe port; **PE0 = CLK = pin 13**: the onboard LED becomes a free clock activity light |
| SW1, SW2 | 40, 41 | Through-hole |
| DIR A, B, C, D | 42–45 | Bottom pads, 10 k pulldowns |
| SW3, SW4 | 46, 47 | Bottom pads |

Ribbon colour convention unchanged: bit 0 = white … bit 7 = red per port.

---

## 3. Per-Port Wiring (by signal name)

For each data port A–D:

| Signal | Connect to |
|---|---|
| HCT245: VCC | 5 V bus, 100 nF at the pin |
| HCT245: DIR | Strap to A→B (drive toward DUT) |
| HCT245: /OE | Inverter output (= NOT DIR line) |
| HCT245: A1–A8 | Teensy port pins (bit 0 → A1) |
| HCT245: B1–B8 | Port connector **through the 47–100 Ω series pack** |
| LVC245: VCC | Teensy 3.3 V rail, 100 nF at the pin |
| LVC245: DIR | Strap to B→A (read toward Teensy) |
| LVC245: /OE | DIR line directly |
| LVC245: B1–B8 | Port connector side (shares the DUT lines with HCT B side) |
| LVC245: A1–A8 | Same Teensy port pins as the HCT A side |
| DIR line | Teensy pad (42–45), **10 k pulldown to GND** |

Port E: HCT245 only — VCC 5 V + 100 nF, DIR strapped to drive, /OE to
GND, A side to Teensy 32–39, B side through its series pack to J-E.

Shared: one HCT04 at 5 V (100 nF), four sections inverting DIR A–D for
the HCT /OEs; unused inputs grounded.

**B-side provisions per data port:** a SIP-9 resistor-pack footprint
(10 k, common pin jumperable to 5 V / GND / open) so tri-state DUT buses
have a defined idle. The Teensy's internal pull-ups cannot reach through
directed shifters — this replaces the old `INPUT_PULLUP` trick on the side
where it now belongs. Fit/strap per project.

**Series terminations:** 47–100 Ω packs on the DUT side of **all five**
ports, Port E very much included — the strobes deserve termination most.

---

## 4. Connectors (own form factor — nothing inherited)

| Connector | Contents |
|---|---|
| J-A … J-D | 8-pin header each: port bits 0–7, white → red |
| J-E | 16-way: E0, GND, E1, GND, … E7, GND. PE0 = CLK by convention |
| Power ×2 | 4-pin each: +5 V, +5 V, GND, GND. One +5 V position jumpered: closed = board powers a small DUT; open = DUT powered externally, ground shared |

The old adapter/Mega loom does not fit this board; the per-port headers
make each port's loom independent, which is the point.

---

## 5. Buttons

SW1–SW4, momentary to GND, **no board pull-ups** — `SW_PIN_MODE` on this
platform is `INPUT_PULLUP`. SW1/SW2 keep their harness-wide roles
(start/advance, abort/exit); SW3/SW4 are spare for per-project functions.
Debounce in software per convention.

---

## 6. Software — AdapterPorts.h, Third Platform

The compiler distinguishes Mega vs Teensy automatically, but both Teensy
boards are `__IMXRT1062__`, so the Teensy variant is chosen by one line at
the top of `AdapterPorts.h`:

```cpp
//#define LVC_HARNESS_BOARD     // uncomment when building for this board
```

The LVC section provides the standard API (`PIN_PA0`..`PIN_PD7`, tables,
unrolled accessors, `samplePorts()`, SW1/SW2) plus:

- `PIN_PE0`..`PIN_PE7`, `PORT_E_PINS[8]`, `readPortE()` / `writePortE(v)`.
- `PIN_SW3` / `PIN_SW4`, `sw3Pressed()` / `sw4Pressed()`.
- `PIN_DIR_A`..`PIN_DIR_D` and the direction API:
  - `initPortDirections()` — first thing in `setup()`: DIR lines low
    (everything reading, matching power-up), Port E driving.
  - `portDrive('A'..'D')` — DIR high (HCT takes the DUT side), then port
    pins OUTPUT.
  - `portRelease('A'..'D')` — port pins INPUT, then DIR low (LVC takes
    the Teensy side back).

Turnaround semantics: during `portDrive` the DUT bus is briefly undefined
(~µs) between the /OE handover and the Teensy pins driving — the same
window any real bus turnaround has. The B-side SIP pulls define the level
during it; fit them on ports that talk to tri-state DUT buses.

Existing sketches (ALUHarness, PortTemplate, PortChaser) use only Ports
A–D and SW1/SW2 and compile for this board unchanged.

---

## 7. Bring-Up Checklist (in order, tradition-approved)

1. Map the vendor pinouts (especially the aftermarket DIP LVC245) onto
   §3's signal names. Bench-test one HCT+LVC pair on protoboard: verify
   3.3 V in → clean 5 V out (drive), 5 V in → clean 3.3 V out (read), and
   the /OE handover in both orders, before building four more.
2. Power-up state: Teensy unprogrammed → every data-port DUT side high-Z
   (DIR pulldowns + inverter doing their job), Port E driving.
3. `PortChaser` on all five ports — the mapping verifier, as always.
4. Boundary hammer against a bench '574: `B DF E0` and `B DF E0 1000 FF`
   through the real loom, CLK on PE0, no conditioner. The mask-FF chase is
   the stress that scored ~90% phantom clocks on the TXS path; acceptance
   is 0/500 both sides.
5. The ALU battery (`C`, `P`, `T`, `I`, `F`) as full-system proof,
   cross-checked once against the Mega.

Until step 5 matches the Mega, the Mega remains the platform of record.

---

## 8. Parts Summary

- 1 × Teensy 4.1 (pins 0–47 used; QSPI pads 48–54 kept free)
- 5 × 74HCT245 DIP-20 (drive: ports A–E)
- 4 × 74LVC245 DIP-20, 5 V-tolerant I/O (read: ports A–D; aftermarket —
  verify pinout)
- 1 × 74HCT04 DIP-14 (DIR → HCT /OE inversion; unused inputs to GND)
- 5 × resistor pack 47–100 Ω × 8 (DUT-side series, every port)
- 4 × SIP-9 10 k footprints (data-port DUT-side pulls, jumperable)
- 4 × 10 k (DIR pulldowns)
- 11 × 100 nF (one per chip: 9 shifters + inverter + spare) + bulk
  47–100 µF at power entry
- 4 × momentary push buttons
- 4 × 8-pin headers, 1 × 16-way header, 2 × 4-pin power headers,
  DUT-power jumper
