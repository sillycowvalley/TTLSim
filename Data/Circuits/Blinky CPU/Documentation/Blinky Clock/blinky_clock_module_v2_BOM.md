# Blinky Clock Module v2 — Bill of Materials

Matches `blinky_clock_module_v2.ttlproj` and
`blinky_clock_module_v2_FEATURES.md`. Supersedes the v1 BOM. Stock figures
carried from `ChipInventory.md` where previously confirmed; anything
unverified is marked *check stock*.

## Integrated circuits

| Ref | Part | Description | Qty | Stock |
|-----|------|-------------|-----|-------|
| U1 | NE556 (bipolar) | Dual timer — timer 1: step Schmitt debouncer, timer 2: output-feedback astable | 1 | on hand |
| U2 | 74HC14 | Hex Schmitt inverter — OSC, step, ~Q3, step-pulse, RST, can (6 of 6) | 1 | 12 on hand |
| U3 | 74HC00 | Mode SR latch + step one-shot latch (4 of 4) | 1 | *check stock* |
| U4 | 74HC273 | Reset / step / select synchronizer (6 of 8 FFs; FF6/FF7 spare) | 1 | 8 on hand |
| U5 | DS1813-5 | Reset supervisor, TO-92 — POR + pushbutton + 150 ms stretch | 1 | on hand |
| U6 | 74HC00 | Clock gate/mux: RUN leg, STEP leg, edge detect, combine (4 of 4) | 1 | *check stock* |
| U7 | 74HC08 | Mode-set∨rst, one-shot-clr∨rst, **CLKG gate**, /BPMATCH·/HALTDST (4 of 4) | 1 | *check stock* |
| U8 | 74HC161 | Can divider — synchronous /2 /4 /8 /16 | 1 | on hand (project part) |
| U9 | 74HC151 | Divider tap select (/1../16 + parked-GND codes), W = /OSCCAN free | 1 | *check stock* |
| U10 | 74HC00 | Source SR latch (555/CAN) + mux legs OSC555·ENA, OSCCAN·ENB (4 of 4) | 1 | *check stock* |
| U11 | 74HC74 | Glitch-free mux enables — one FF per clock domain (2 of 2) | 1 | *check stock* |
| U12 | 74HC08 | /BRUN, mux D-terms ×2, KEEP1 (4 of 4) | 1 | *check stock* |
| U13 | 74HC74 | **HALT** flop + matched-last flop (2 of 2) | 1 | *check stock* |
| U14 | 74HC00 | OSC combine, /ENTRY, HOLD, DHALT (4 of 4) | 1 | *check stock* |
| U15 | 74HC00 | ARM latch, /INSTRMATCH, /STEPCLR (4 of 4) | 1 | *check stock* |
| U16 | 74HC08 | KEEP, /ANYMATCH, FETCH, ARMCLR (4 of 4) | 1 | *check stock* |
| U17 | 74HC02 | NOR(T0,T1), NOR(T2,T3), MATCHNOW, /OSC555 (4 of 4) | 1 | *check stock* |
| U18 | 74HC161 | T counter (board service) — CLKG-clocked, /TRST sync reload | 1 | on hand (project part) |
| X1 | Can oscillator, DIP-14, socketed | External clock source (any TTL/HCMOS can) | 1 | on hand |

U1 appears in the capture as **U1A/U1B** — two NE555s, since TTLSim's
library carries no 556. The physical part is one NE556; the two halves map
timer 1 → U1A (Schmitt) and timer 2 → U1B (astable).

**Bipolar adaptation** carried from v1: R9/R10 finish the NE556's pull to
the rail; 100 n decoupler hard against U1 pins 14/7, 10 µ bulk nearby. All
logic stays one family (HC).

## Resistors (¼ W)

| Ref | Value | Function | Qty |
|-----|-------|----------|-----|
| R1 | 100k | Step button RC (with C1) | 1 |
| R2 | 330R | Power-LED (D8) limit | 1 |
| R3 | 10k | STEP button pull-up | 1 |
| R4 | 10k | RUN-555 button pull-up | 1 |
| R5–R8 | 330R | LED limit D1–D4 | 4 |
| R9, R10 | 4k7 | 556 OUT1/OUT2 pull-ups (bipolar VOH fix) | 2 |
| R11 | 10k | RUN-CAN button pull-up | 1 |
| R13 | 10k | INSTR button pull-up | 1 |
| R14–R16 | 10k | DIP DIV0–DIV2 pull-ups | 3 |
| R17 | 10k | /TRST handshake pull-up | 1 |
| R18 | 10k | /BPMATCH handshake pull-up | 1 |
| R19 | 10k | /HALTDST handshake pull-up | 1 |
| R20–R25 | 330R | LED limit D5–D7, D9–D11 | 6 |
| RV1 | 100k multi-turn trimmer | Oscillator adjust | 1 |

R12 (v1's one-shot clear-node pull-up) is **omitted** — redundant with U7
gate-b's push-pull drive, retired on this respin. The proposed 1k astable
floor resistor remains open; keep RV1 off the bottom stop until fitted.

## Capacitors

| Ref | Value | Function | Qty |
|-----|-------|----------|-----|
| C1 | 470n | Step bounce filter (47 ms with R1) | 1 |
| C9 | 10u | Astable timing (with RV1) | 1 |
| C2–C8, C10–C13 | 100n | Decoupling, one per IC (18 ICs — physical only, not in the capture) | 18 |
| C14 | 10u | Bulk, near U1 | 1 |

## LEDs

| Ref | Colour | Function |
|-----|--------|----------|
| D1 | green | STEP mode |
| D2 | green | RUN mode |
| D3 | amber | CLKG activity (machine clock) |
| D4 | red | RST |
| D5 | red | HALT |
| D6 | green | Source: 555 |
| D7 | green | Source: CAN |
| D8 | red | Supply present |
| D9–D11 | amber | T0–T2 (T state) |

## Switches and buttons

| Ref | Type | Function |
|-----|------|----------|
| SW1 | momentary | Step (one cycle) |
| SW2 | momentary | STEP mode |
| SW3 | momentary | RUN-555 (mode + source) |
| SW4 | momentary | Reset |
| SW5 | momentary | RUN-CAN (mode + source) |
| SW6 | momentary | INSTR (one instruction) |
| SW7–SW9 | 3-pos DIP (SPST) | Divider select DIV0–DIV2 (closed = 0; all-open parks the CAN leg) |
| S1 | SPST toggle | Board power |

## Headers

| Ref | Pins | Signals |
|-----|------|---------|
| H1 | 2 | Power in (VCC, GND) through S1 |
| H2 | 10 | Out: GND, VCC, CLK, CLKG, RST, HALT, FETCH, T0, T1, T2 |
| H3 | 4 | Handshake in: GND, /TRST, /HALTDST, /BPMATCH |

T3 rides the board as a net for a 16-state machine — add an H2 pin at
build time if needed. **/TRST contract**: full-cycle level, asserted
during the instruction's final T state; strap to GND on a single-cycle
machine (T pins at 0, FETCH constantly true, INSTR ≡ Step); don't leave
it floating on a machine with instruction boundaries. S1,
H1–H3 and the decouplers are physical-build items only; in the capture the
module's interface is its named nets. J2 (v1's can jumper) is deleted —
source selection is now the RUN-555/RUN-CAN buttons and the DIP divider.
