# 74-Series Logic IC Inventory

Counts are estimates based on visual inspection of drawer photos. Chips partially hidden by foam may not be reflected accurately.

## Inventory Table

| Part   | Function                       | LS   | HCT  | HC   | LS In Transit | LS Total |
|--------|--------------------------------|------|------|------|---------------|----------|
| 7400   | 4× 2i NAND                     | ~3   | ~4   | ~3   | 0             | ~3       |
| 7402   | 4× 2i NOR                      | ~5   |      | ~2   | 0             | ~5       |
| 7404   | 6× Inverter                    | ~7   |      | ~4   | 16            | ~23      |
| 7408   | 4× 2i AND                      |      |      | 8    | 8             | 8        |
| 7414   | 6× Schmitt Inverter            |      |      | ~14  | 8             | 8        |
| 7432   | 4× 2i OR                       | ~5   |      | ~13  | 0             | ~5       |
| 7447   | BCD to 7-segment Decoder       | 12   |      |      | 0             | 12       |
| 7474   | 2× D-Type Flip-Flop            |      |      | 14   | 8             | 8        |
| 7486   | 4× 2i XOR                      |      |      |      | 0             | 0        |
| 74107  | 2× JK Flip-Flop                |      |      |      | 0             | 0        |
| 74138  | 3-to-8 Demultiplexer           | ~5   |      | ~5   | 8             | ~13      |
| 74139  | Dual 2-to-4 Decoder            |      |      |      | 0             | 0        |
| 74151  | 8-to-1 Multiplexer             |      |      | ~3   | 8             | 8        |
| 74153  | Dual 4-to-1 Multiplexer        |      |      |      | 8             | 8        |
| 74157  | Quad 2-to-1 Multiplexer        |      |      |      | 16            | 16       |
| 74161  | 4-Bit Synchronous Counter      |      |      |      | 8             | 8        |
| 74169  | 4-Bit Up/Down Counter          |      |      |      | 8             | 8        |
| 74173  | 4-Bit D-Type Register          |      |      |      | 16            | 16       |
| 74181  | 4-Bit ALU                      |      |      |      | 8             | 8        |
| 74194  | 4-Bit Bidirectional Shift Reg  |      |      |      | 8             | 8        |
| 74244  | 8× Tri-State Buffer            | ~6   |      | ~2   | 0             | ~6       |
| 74245  | Octal Tri-State Bus            | ~4   |      | 1    | 16            | ~20      |
| 74273  | Octal D Flip-Flop              |      |      |      | 0             | 0        |
| 74283  | 4-Bit Binary Adder             |      |      |      | 4             | 4        |
| 74374  | Octal D-Type Latches           | ~3   |      | 4    | 10            | ~13      |
| 74393  | Dual 4-stage Binary Counter    |      | ~3   | 12   | 8             | 8        |

## Notes

- Counts marked with `~` are visual estimates from photos; physical verification recommended for accurate totals.
- Counts without `~` have been confirmed by the user.
- LS, HCT, and HC counts are now shown in separate columns. Blank means "none in stock".
- The **LS In Transit** column shows quantities ordered from Jameco (confirmation #21152955, dated 5/21/2026) that have not yet arrived. Once received, these should be merged into the LS column and this column reset.
- The **LS Total** column is `LS + LS In Transit` — the projected on-hand count once the order arrives. A `~` is carried through if either input is approximate.

## In-transit order summary

Ordered 5/21/2026 (Jameco confirmation #21152955), shipping FedEx International Priority:

- **74LS04** × 16 — speculative spares; LS Required was 0.
- **74LS08** × 8 — covers Required of 2 (Clock schematic), 6 spares.
- **74LS14** × 8 — covers Required of 1 (Clock schematic), 7 spares.
- **74LS74** × 8 — covers Required of 2 (Blinky N/Z/C flag latches), 6 spares.
- **74LS138** × 8 — covers Required of ~1, 7 spares.
- **74LS151** × 8 — covers Required of 1 (Blinky branch-decode mux), 7 spares.
- **74LS153** × 8 — speculative; not used by any current build.
- **74LS157** × 16 — covers Required of 2 (address-source mux / SMODE A-side mux), 14 spares.
- **74LS161** × 8 — covers Required of 1 (Blinky PC), 7 spares.
- **74LS169** × 8 — covers Required of 4 (DSP + RSP cascaded pairs), 4 spares.
- **74LS173** × 16 — covers Required of 4 (ALU testbench A/B halves), 12 spares.
- **74LS181** × 8 — covers Required of 2 (8-bit ALU slices), 6 spares.
- **74LS194** × 8 — speculative; not used by any current build.
- **74LS245** × 16 — covers Required of 1, 15 spares.
- **74LS283** × 4 — speculative; not used by any current build ('181 handles addition).
- **74LS374** × 10 — covers Required of 10 exactly (Blinky TOS/NOS + IR halves + LED matrix rows + status LEDs).
- **74LS393** × 8 — covers Required of 5 (Clock schematic), 3 spares.

### Not ordered, still outstanding

- **74LS00** — Required was ~2. No '00 on the order. Worth adding to the next order if firm LS-throughout discipline matters; otherwise the existing ~3 LS on hand may suffice once the control-unit schematic firms up demand.
