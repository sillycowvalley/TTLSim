# Blinky Clock Module — Bill of Materials

Matches `blinky_clock_module.svg` and the verified module capture. Stock
figures are from `ChipInventory.md` where previously confirmed; anything
unverified is marked *check stock*.

## Integrated circuits

| Ref | Part | Description | Qty | Stock |
|-----|------|-------------|-----|-------|
| U1 | NE556 (bipolar) | Dual timer — timer 1: step Schmitt debouncer, timer 2: output-feedback astable | 1 | on hand |
| U2 | 74HC14 | Hex Schmitt inverter — OSC buffer, step, ~Q3, step-pulse, RST, can (6 of 6 used) | 1 | 12 on hand |
| U3 | 74HC00 | Quad 2-input NAND — mode SR latch + step one-shot latch (4 of 4 used) | 1 | *check stock* |
| U4 | 74HC273 | Octal D flip-flop, async clear — reset / step / select synchronizer (6 of 8 FFs used) | 1 | 8 on hand |
| U5 | DS1813-5 | Reset supervisor, TO-92 — POR + debounced pushbutton + 150 ms stretch, open-drain /RST | 1 | on hand |
| U6 | 74HC00 | Quad 2-input NAND — clock gate/mux: RUN leg, STEP leg, edge detect, combine (4 of 4 used) | 1 | *check stock* |
| U7 | 74HC08 | Quad 2-input AND — reset coupling (active-low OR): mode-set, step-clear (2 of 4 used) | 1 | *check stock* |

**Bipolar adaptation.** The NE556's output high sits ~1.2–1.7 V below the
rail (Darlington high side), marginal against 74HC's worst-case VIH of
3.5 V. R9/R10 (4k7 to VCC, one per timer output) fix this: the high side
stops sourcing once the pin rises past its reach and the resistor finishes
the pull to 5 V; the low side sinks the ~1 mA without raising VOL. The
totem-pole output draws supply spikes on every transition, so the 100 n
decoupler sits hard against pins 14/7 with the 10 µ bulk nearby. The '00s
and '08 could come from any family; HC keeps the board one family.

## Resistors (¼ W)

| Ref | Value | Function | Qty |
|-----|-------|----------|-----|
| R1 | 100k | Step button RC (with C1) | 1 |
| R2 | 330R | Power-LED (D8) current limit | 1 |
| R3 | 10k | STEP-mode button pull-up (into U7 gate-a) | 1 |
| R4 | 10k | RUN-mode button pull-up (into the SR latch reset) | 1 |
| R5–R8 | 330R | LED current limit (D1–D4) | 4 |
| R9, R10 | 4k7 | Pull-ups on 556 OUT1 / OUT2 (and SEL node) — restore bipolar VOH to the rail for HC inputs | 2 |
| R12 | 10k | Pull-up on the step one-shot's clear node (U3 pin 12) | 1 |
| RV1 | 100k multi-turn trimmer (Bourns 3296-style) | Oscillator frequency adjust | 1 |

With C9 = 10 µF the oscillator floor is roughly **0.7 Hz** at full RV1,
rising as RV1 is reduced — f = 1 / (2·R·C·ln 2). No astable floor resistor
is fitted (RV1 is a bare rheostat from OUT2 into the timing node); a 1k in
series is the one-part fix if RV1 will ever be trimmed near zero. R12 is
redundant with U7 gate-b's push-pull drive of the same node and may be
omitted on a clean respin.

## Capacitors

| Ref | Value | Function | Qty |
|-----|-------|----------|-----|
| C1 | 470n | Step bounce filter (47 ms with R1). Positive leg to the R1/SW1/THR1 node if polarized | 1 |
| C3, C5, C6, C7, C8 | 100n MLCC | Decoupling — one per IC (U3, U1, U4, U6, U2) | 5 |
| C4 | 47 µF electrolytic | Bulk on the supply rail | 1 |
| C9 | 10 µF (≥10 V, positive leg to THR2/TRG2) | Astable timing cap | 1 |

The 556 CONT pins (CV1, CV2) are left open in this build; a 10 n from each
to GND is optional for noise immunity. C9 as a polarized timing cap is
workable — the astable node stays at +1.7…+3.3 V — but its leakage skews
the slow end of the trim; a tantalum or film part tightens it.

## LEDs, switches, hardware

| Ref | Part | Function | Qty |
|-----|------|----------|-----|
| D1, D2 | 3 mm green LED | STEP mode / RUN mode indicators (on the latch — instant button feedback) | 2 |
| D3 | 3 mm amber LED | CLK activity | 1 |
| D4 | 3 mm red LED | RESET asserted (active-high RST) | 1 |
| D8 | 3 mm red LED | Supply present | 1 |
| SW1–SW4 | 6 mm momentary tactile pushbutton | STEP, mode-STEP, mode-RUN, RESET | 4 |
| S1 | SPDT slide/toggle switch | Supply on/off | 1 |
| X1 | Can oscillator (optional) | External clock — fit a DIP socket so the can is swappable; squared through U2 inv6 to HC levels | 1 |
| J2 | 3-pin 2.54 mm header + shunt | Clock source select: 556 / CAN. Default: 556 | 1 |
| H1 | 2-pin header | Power input | 1 |
| H2 | 4-pin header | Output: GND, VCC, CLK, RST | 1 |

## Totals

6 ICs + 1 TO-92 (+ optional socketed can), 11 resistors + 1 trimmer,
9 capacitors, 5 LEDs, 4 pushbuttons + 1 power switch, 2 jumpered/IO
headers. Spares left on the board for expansion: U7 gate-c / gate-d (AND),
U4 FF6 / FF7.
