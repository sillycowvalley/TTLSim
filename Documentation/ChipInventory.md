# 74-Series Logic IC Inventory

Counts verified against drawer photos 16 July 2026. Part numbers are family-neutral;
counts are split by family column. LS column exists only for the 74LS181 backup stock.

**In Transit** columns cover Jameco confirmation 21161678 (10 Jul 2026) and Mouser
invoice 91429859 (shipped 15 Jul 2026). Merge into the stock columns on receipt and
clear the In Transit cells.

## Inventory Table

| Part   | Function                                  | HC  | HCT | LS  | HC In | HCT In |
| ------ | ----------------------------------------- | --- | --- | --- | ----- | ------ |
| 7400   | 4× 2i NAND                                | 11  |     |     |       |        |
| 7402   | 4× 2i NOR                                 | 11  |     |     |       |        |
| 7404   | 6× Inverter                               | 16  | 16  |     |       |        |
| 7408   | 4× 2i AND                                 | 11  |     |     |       |        |
| 7410   | 3× 3i NAND                                | 8   |     |     |       |        |
| 7411   | 3× 3i AND                                 | 20  |     |     |       |        |
| 7414   | 6× Schmitt Inverter                       | 10  | 8   |     |       | 8      |
| 7420   | 2× 4i NAND                                | 8   |     |     |       |        |
| 7432   | 4× 2i OR                                  | 12  |     |     |       |        |
| 7473   | 2× JK Flip-Flop                           |     | 8   |     |       |        |
| 7474   | 2× D-Type Flip-Flop                       | 10  |     |     |       |        |
| 7485   | 4-Bit Magnitude Comparator                |     |     |     | 4     |        |
| 7486   | 4× 2i XOR                                 | 8   |     |     |       |        |
| 74125  | 4× Tri-State Buffer (active-low enable)   | 20  |     |     |       |        |
| 74126  | 4× Tri-State Buffer (active-high enable)  | 8   |     |     |       |        |
| 74132  | 4× 2i Schmitt NAND                        | 4   |     |     |       |        |
| 74138  | 3-to-8 Decoder                            | 24  | 4   |     |       |        |
| 74139  | Dual 2-to-4 Decoder                       | 8   |     |     |       |        |
| 74148  | 8-to-3 Priority Encoder                   |     |     |     | 4     |        |
| 74151  | 8-to-1 Multiplexer                        | 16  |     |     |       |        |
| 74153  | Dual 4-to-1 Multiplexer                   | 29  |     |     |       |        |
| 74157  | Quad 2-to-1 Multiplexer                   | 28  |     |     |       |        |
| 74161  | 4-Bit Synchronous Counter (async clear)   | 13  |     |     |       |        |
| 74163  | 4-Bit Synchronous Counter (sync clear)    | 8   |     |     | 8     |        |
| 74164  | 8-Bit SIPO Shift Register                 | 4   |     |     |       |        |
| 74165  | 8-Bit PISO Shift Register                 | 12  |     |     |       |        |
| 74173  | 4-Bit D-Type Register (tri-state)         | 10  |     |     |       |        |
| 74174  | Hex D Flip-Flop with Clear                | 16  |     |     |       |        |
| 74175  | Quad D Flip-Flop with Clear               | 16  |     |     |       |        |
| 74181  | 4-Bit ALU (LS — needs HCT fence)          |     |     | 32  |       |        |
| 74182  | Carry Lookahead Generator                 | 10  |     |     |       |        |
| 74191  | 4-Bit Up/Down Counter                     | 23  |     |     |       |        |
| 74193  | 4-Bit Up/Down Counter (dual clock)        | 27  |     |     |       |        |
| 74194  | 4-Bit Bidirectional Shift Register        | 10  |     |     |       |        |
| 74244  | Octal Tri-State Buffer                    | 16  | 4   |     |       |        |
| 74245  | Octal Bus Transceiver                     | 16  | 20  |     |       | 4      |
| 74273  | Octal D Flip-Flop with Clear              | 6   |     |     |       |        |
| 74283  | 4-Bit Binary Adder                        | 8   |     |     |       |        |
| 74373  | Octal Transparent Latch                   | 8   |     |     |       |        |
| 74374  | Octal D Flip-Flop                         | 24  | 16  |     |       |        |
| 74377  | Octal D Flip-Flop with Enable             | 8   |     |     |       |        |
| 74393  | Dual 4-Bit Binary Counter                 | 8   | 2   |     |       |        |
| 74541  | Octal Buffer                              | 8   |     |     |       |        |
| 74573  | Octal Transparent Latch (flow-through)    | 8   | 4   |     |       |        |
| 74574  | Octal D Flip-Flop (flow-through)          | 7   |     |     |       |        |
| 74590  | 8-Bit Counter with Output Register (3-st) |     |     |     | 8     |        |
| 74595  | 8-Bit Shift Register with Output Latch    | 12  |     |     |       |        |
| 74670  | 4×4 Register File (tri-state)             | 32  |     |     |       |        |
| 74688  | 8-Bit Identity Comparator                 |     | 3   |     | 4     |        |
| 744040 | 12-Bit Async Binary Counter               |     |     |     | 4     |        |
| 744060 | 14-Stage Async Binary Counter             |     |     |     | 4     |        |

## Notes

- Blank cells mean no stock in that family (no drawer, or drawer empty).
- HCT parts accept both HC and LS input levels and drive both families — they are
  the boundary parts wherever an LS output (notably the 74LS181) feeds the HC world.
- The 74LS181 is the only LS stock; every LS181 output boundary requires an HCT
  fence (HCT245, HCT04, HCT374).
- 74688: shelf stock is HCT, inbound stock (Mouser) is HC — keep the families in
  separate drawers.
- 744040 / 744060 are the CD4000-heritage async counters (74HC4040 / 74HC4060),
  listed under the family-neutral scheme.
