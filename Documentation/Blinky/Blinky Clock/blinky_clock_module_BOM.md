# Blinky Clock Module — Bill of Materials

Matches `blinky_clock_module.svg` exactly. Stock figures are from
`ChipInventory.md` where I previously verified them; anything I haven't
seen in the inventory is marked *check stock*.

## Integrated circuits

| Ref | Part | Description | Qty | Stock |
|-----|------|-------------|-----|-------|
| U1 | NE556 (bipolar) | Dual timer — timer 1: step Schmitt debouncer, timer 2: output-feedback astable | 1 | on hand |
| U2, U3 | 74HC00 | Quad 2-input NAND — SR latch + mux + edge detect (8 of 8 gates used) | 2 | *check stock* |
| U4 | 74HC14 | Hex Schmitt inverter — /OSC, /Q3, SP, RST (4 of 6 used) | 1 | 12 on hand |
| U5 | 74HC273 | Octal D flip-flop, async clear — reset / step / select sync (6 of 8 FFs used) | 1 | 8 on hand |
| — | DS1813-5 | Reset supervisor, TO-92 — POR + debounced pushbutton + 150 ms stretch | 1 | on hand |

**Bipolar adaptation.** The NE556's output high sits ~1.2–1.7 V below the
rail (Darlington high side), marginal against 74HC's worst-case VIH of
3.5 V. R9/R10 (4k7 to VCC, one per timer output) fix this: the high side
stops sourcing once the pin rises past its reach and the resistor finishes
the pull to 5 V; the low side sinks the ~1 mA without raising VOL. The
sub-microsecond RC tail is invisible at this module's frequencies. Two
bipolar-specific cares: (1) the totem-pole output draws supply spikes on
every transition, so put the 100 n decoupler hard against pins 14/7 and the
10 µ bulk nearby; (2) with the 100k trimmer the THRES bias current
(0.25 µA max) is only ~1.5 % of the ~17 µA charge current near threshold,
so the bipolar part's timing error is negligible across the whole trim
range. The '00s could come from any family; HC keeps the whole
board one family.

## Resistors (¼ W)

| Ref | Value | Function | Qty |
|-----|-------|----------|-----|
| R1 | 100k | Step button RC (with C1) | 1 |
| R2 | 1k | Astable series floor — protects timer 2's output when RV1 is trimmed to zero | 1 |
| R3, R4 | 10k | Mode-button pull-ups (SR latch inputs) | 2 |
| R5–R8 | 330R | LED current limit (D1–D4) | 4 |
| R9, R10 | 4k7 | Pull-ups on 556 OUT1/OUT2 — restore bipolar VOH to the rail for HC inputs | 2 |
| R11 | 10k | Pull-up on the can output node — defines U4E's input when no can is fitted; helps a TTL-output can's VOH | 1 |
| RV1 | 100k multi-turn trimmer (Bourns 3296-style) | Oscillator frequency adjust | 1 |

With C2 = 2.2 µ the frequency range is roughly **3.2 Hz** (RV1 at full
100k) up to **~330 Hz** (RV1 at zero, R2 only) — f = 1 / (2·R·C·ln 2). If
a kHz-range fast mode is ever wanted, a second, smaller C2 behind a range
switch is the one-part answer.

## Capacitors

| Ref | Value | Function | Qty |
|-----|-------|----------|-----|
| C1 | 470n | Step bounce filter (47 ms with R1). Tantalum fine: node stays 0…+5 V, positive leg to the R1/S1/THR1 node; rate ≥10 V | 1 |
| C2 | 2.2µ tantalum (≥10 V, positive leg to THR2/TRG2), film, or X7R MLCC (or several smaller MLCCs in parallel, e.g. 4× 470n) | Astable timing. Aluminum electrolytic is workable — node stays at +1.7…+3.3 V — but its leakage (up to ~3 µA worst case vs the ~17 µA charge current at full trim) can skew the slow end up to ~18 %; tantalum or non-polarized parts keep it within a few % | 1 |
| — | 10n | 556 CONT pins (CV1, CV2) to GND | 2 |
| — | 100n MLCC | Decoupling, one per IC (U1–U5) | 5 |
| — | 10µ electrolytic | Bulk on the supply rail | 1 |

## LEDs, switches, hardware

| Ref | Part | Function | Qty |
|-----|------|----------|-----|
| D1, D2 | 3 mm green LED | STEP mode / RUN mode indicators (on the latch — instant button feedback) | 2 |
| D3 | 3 mm yellow LED | CLK activity (visible at 3 Hz) | 1 |
| D4 | 3 mm red LED | RESET asserted | 1 |
| S1–S4 | 6 mm momentary tactile pushbutton | STEP, mode-STEP, mode-RUN, RESET | 4 |
| X1 | Can oscillator, e.g. 1 MHz (optional) | External clock source — fit a DIP socket so the can is swappable; hardwired through U4E, so TTL- and HCMOS-output cans both arrive at clean HC levels | 1 |
| J1 | 3-pin 2.54 mm header + shunt | Clock source select: 556 / CAN. Default: 556 | 1 |

## Totals

5 ICs + 1 TO-92 (+ optional socketed can), 11 resistors + 1 trimmer,
10 capacitors, 4 LEDs, 4 pushbuttons, 1 jumpered header. Spares left on
the board for expansion: U3C/U3D (NAND), U4F (Schmitt inverter),
U5 FF6/FF7.
