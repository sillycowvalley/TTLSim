# Mini Blinky CPU — Consolidated BOM (W = 4)

Combines the core chip complement (`Mini_Blinky_CPU.md` §9) with the
clock module (`blinky_clock_module_BOM.md`). The old §9 placeholder row
**"Clock / reset / step — 7414 + glue — ~2"** is removed; the clock module
below replaces it in full. Its **74HC14 (U4)** *is* the '14 the placeholder
referred to — counted once, here.

Stock figures carried through as stated in the source docs; not
re-verified against `ChipInventory.md` (not on hand this turn).

---

## A. Core logic ICs

| Block | Part | Qty | Stock / sourcing |
|---|---|---|---|
| ALU | 74181 | 1 | 10 HC in transit (8 LS backup — LS needs HCT fence) |
| TOS shift register | 74194 | 1 | 10 in transit |
| TOS serial-fill mux | 74153 | 1 | ALU rev 2 |
| NOS latch | 74374 (or '273) | 1 | 16 HCT374 in transit (HC374 unobtainable) |
| Data-stack pointer | 74191 | 1 | 20 in transit (replaces unobtainable HC169) |
| Return-stack pointer | 74191 | 1 | same stock as DSP |
| Data / return / D-memory | 2114 SRAM | 3 | source / confirm |
| Program counter | 74161 | 1 | 8 on hand |
| Instruction register | 74374 (8-bit) | 1 | '374 stock as above |
| Address-source mux | 74157 | 1 | 8 on hand, 20 in transit |
| I-memory | 8-bit EEPROM (28C16) | 1 | source / confirm |
| Flags N/Z/C | 7474 | 2 | 8 on hand |
| Decode / control | GAL16V8 | 4 | in stock; ATF16V8B as active-production alt |
| Stack-strobe glue | '04 / '00 | ~1 | derives stack /CS,/WE from the '191 pointers |

**Core logic subtotal: 20 ICs.**
(74374 ×2 = NOS + IR; 74191 ×2 = DSP + RSP; 2114 ×3; GAL16V8 ×4; 7474 ×2.)

---

## B. Clock module ICs

| Ref | Part | Qty | Stock |
|---|---|---|---|
| U1 | NE556 (bipolar) | 1 | on hand |
| U2, U3 | 74HC00 | 2 | *check stock* |
| U4 | 74HC14 | 1 | 12 on hand |
| U5 | 74HC273 | 1 | 8 on hand |
| — | DS1813-5 (TO-92) | 1 | on hand |
| X1 | Can oscillator, ~1 MHz (optional, socketed) | 1 | *check stock* |

**Clock module subtotal: 5 ICs + 1 TO-92** (+ optional can).

---

## C. Clock module passives & front-panel parts

(Core logic has no passive list in the source — only the clock module is
specified to this level. Front-panel LED drivers/latches for the CPU
itself are still additional and not yet enumerated.)

**Resistors (¼ W):** R1 100k, R2 1k, R3/R4 10k (×2), R5–R8 330R (×4),
R9/R10 4k7 (×2), R11 10k, RV1 100k multi-turn trimmer.
**= 11 resistors + 1 trimmer.**

**Capacitors:** C1 470n, C2 2.2µ, CV1/CV2 10n (×2), 100n decoupling (×5,
one per IC U1–U5), 10µ bulk (×1).
**= 10 capacitors.**

**LEDs:** D1/D2 green (mode), D3 yellow (CLK), D4 red (RST). **= 4 LEDs.**

**Switches / hardware:** S1–S4 6 mm tactile (STEP, mode-STEP, mode-RUN,
RESET); J1 3-pin header + shunt (556 / CAN select). **= 4 buttons, 1 header.**

---

## D. Grand totals

| Category | Count |
|---|---|
| Core logic ICs | 20 |
| Clock module ICs | 5 |
| **Total DIP/IC** | **25** |
| Reset supervisor (TO-92) | 1 |
| Can oscillator (optional) | 1 |
| Resistors + trimmer | 11 + 1 |
| Capacitors | 10 |
| LEDs | 4 |
| Pushbuttons | 4 |
| Headers | 1 |

The previous §9 sketch read "~18–20 core ICs," but that was rough and folded
the clock into a 2-IC placeholder. Enumerated properly — 3× 2114, 4× GAL,
the doubled '374/'191 — the core alone is 20, and the real clock module adds
5 + a TO-92. **No '14 is double-counted:** the placeholder's '14 is U4.

**Open / unverified:**
- `ChipInventory.md` not consulted this turn — stock figures are as-stated.
- 74HC00, 2114, 28C16, can oscillator all flagged *source / confirm*.
- Stack-strobe glue (~1 IC) could partly draw on the clock module's spares
  (U3C/D NAND, U4F Schmitt) but the source treats it as its own part; kept
  separate here.
- CPU front-panel LED drivers/latches not yet enumerated.
