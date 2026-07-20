# Teensy 4.1 — Pin Table for the TTLSim Editor

Layout for a box-style part with real package pin numbers, matching the
TTLSim convention. The Teensy 4.1's edge pins form a DIP-48-style outline
(24 per side, 2.54 mm pitch), numbered here the DIP way: pin 1 at the USB
end, down one side, back up the other. Bottom SMT pads follow as pins 49–54
so the LVC-board signals are all connectable.

Source: PJRC quick-reference card (front). Roles/colours: the LVC harness
board assignment (`LVCBoard_Reference.md`).

**Electrical notes for the part description:** 3.3 V logic, pins are **not**
5 V tolerant (the shifter board exists for a reason); Vin accepts 3.6–5.5 V;
the 3.3 V rail can source 250 mA max; pin 13 carries the onboard LED.

---

## Edge pins, DIP-48 numbering

Side 1 (USB end, along the "0–32" edge):

| Symbol pin | Teensy label | LVC-board role | Ribbon colour |
|---|---|---|---|
| 1 | GND | GND | — |
| 2 | 0 | PA0 | white |
| 3 | 1 | PA1 | grey |
| 4 | 2 | PA2 | violet |
| 5 | 3 | PA3 | blue |
| 6 | 4 | PA4 | green |
| 7 | 5 | PA5 | yellow |
| 8 | 6 | PA6 | orange |
| 9 | 7 | PA7 | red |
| 10 | 8 | PB0 | white |
| 11 | 9 | PB1 | grey |
| 12 | 10 | PB2 | violet |
| 13 | 11 | PB3 | blue |
| 14 | 12 | PB4 | green |
| 15 | 3.3V | 3.3 V out (250 mA max) | — |
| 16 | 24 | PD0 | white |
| 17 | 25 | PD1 | grey |
| 18 | 26 | PD2 | violet |
| 19 | 27 | PD3 | blue |
| 20 | 28 | PD4 | green |
| 21 | 29 | PD5 | yellow |
| 22 | 30 | PD6 | orange |
| 23 | 31 | PD7 | red |
| 24 | 32 | PB5 | yellow |

Side 2 (continuing DIP-style — pin 25 sits opposite pin 24):

| Symbol pin | Teensy label | LVC-board role | Ribbon colour |
|---|---|---|---|
| 25 | 33 | PE1 | grey |
| 26 | 34 | PE2 | violet |
| 27 | 35 | PE3 | blue |
| 28 | 36 | PE4 | green |
| 29 | 37 | PE5 | yellow |
| 30 | 38 | PE6 | orange |
| 31 | 39 | PE7 | red |
| 32 | 40 | SW1 (to GND, internal pull-up) | — |
| 33 | 41 | SW2 (to GND, internal pull-up) | — |
| 34 | GND | GND | — |
| 35 | 13 | PE0 (CLK — onboard LED doubles as clock activity light) | white |
| 36 | 14 | PB6 | orange |
| 37 | 15 | PB7 | red |
| 38 | 16 | PC0 | white |
| 39 | 17 | PC1 | grey |
| 40 | 18 | PC2 | violet |
| 41 | 19 | PC3 | blue |
| 42 | 20 | PC4 | green |
| 43 | 21 | PC5 | yellow |
| 44 | 22 | PC6 | orange |
| 45 | 23 | PC7 | red |
| 46 | 3.3V | 3.3 V out | — |
| 47 | GND | GND | — |
| 48 | Vin | Vin (3.6–5.5 V in) | — |

## Bottom SMT pads (symbol pins 49–54)

| Symbol pin | Teensy label | LVC-board role |
|---|---|---|
| 49 | 42 | DIR_A (10 k pulldown; high = drive) |
| 50 | 43 | DIR_B |
| 51 | 44 | DIR_C |
| 52 | 45 | DIR_D |
| 53 | 46 | SW3 (to GND, internal pull-up) |
| 54 | 47 | SW4 (to GND, internal pull-up) |

Pads 48–54 (QSPI group) are deliberately excluded — kept free for possible
PSRAM, and unused by the harness.

---

## Transcription gotchas

- Teensy pin 13 lands mid-way along side 2 (symbol pin 35), because the
  card's "23…13" row runs *descending* — the physical order is 23 nearest
  the 3.3V/GND/Vin end. Easy to get backwards; the tables above are in
  true physical order.
- Ports B and C each straddle a power pin or the board end in physical
  order but are contiguous in Teensy numbering — the symbol-pin column is
  the physical truth, the Teensy-label column is what software uses.
- If the editor part is meant for harness schematics generally (not just
  the LVC board), keep the role column as a comment/label — the Teensy
  labels are the invariant; roles change per board.
