# 74-Series Logic IC Inventory

Counts verified against drawer photos 11 June 2026. All stock is HC family unless
the part name says otherwise; deliberate LS/HCT purchases get their own rows.

## Inventory Table

| Part     | Function                                     | On Hand | In Transit | Total |
| -------- | -------------------------------------------- | ------- | ---------- | ----- |
| 7400     | 4× 2i NAND                                   | 12      |            | 12    |
| 7402     | 4× 2i NOR                                    | 12      |            | 12    |
| 7404     | 6× Inverter                                  | 14      |            | 14    |
| 74HCT04  | 6× Inverter (HCT — LS boundary duty)         | 16      |            | 16    |
| 7408     | 4× 2i AND                                    | 8       |            | 8     |
| 7410     | 3× 3i NAND                                   | 8       |            | 8     |
| 7414     | 6× Schmitt Inverter                          | 12      |            | 12    |
| 7420     | 2× 4i NAND                                   | 8       |            | 8     |
| 7432     | 4× 2i OR                                     | 8       |            | 8     |
| 7473     | 2× JK Flip-Flop                              | 5       |            | 5     |
| 7474     | 2× D-Type Flip-Flop                          | 8       |            | 8     |
| 7486     | 4× 2i XOR                                    | 8       |            | 8     |
| 74125    | 4× Tri-State Buffer (active-low enable)      | 8       |            | 8     |
| 74126    | 4× Tri-State Buffer (active-high enable)     | 8       |            | 8     |
| 74132    | 4× 2i Schmitt NAND                           | 4       |            | 4     |
| 74138    | 3-to-8 Decoder                               | 8       |            | 8     |
| 74139    | Dual 2-to-4 Decoder                          | 8       |            | 8     |
| 74151    | 8-to-1 Multiplexer                           | 20      |            | 20    |
| 74153    | Dual 4-to-1 Multiplexer                      | 30      |            | 30    |
| 74157    | Quad 2-to-1 Multiplexer                      | 28      |            | 28    |
| 74161    | 4-Bit Synchronous Counter                    | 8       |            | 8     |
| 74164    | 8-Bit SIPO Shift Register                    | 4       |            | 4     |
| 74165    | 8-Bit PISO Shift Register                    | 4       |            | 4     |
| 74173    | 4-Bit D-Type Register (tri-state)            | 20      |            | 20    |
| 74181    | 4-Bit ALU                                    | 0       |            | 0     |
| 74LS181  | 4-Bit ALU (LS — backup, needs HCT fence)     | 26      |            | 26    |
| 74191    | 4-Bit Up/Down Counter                        | 20      |            | 20    |
| 74193    | 4-Bit Up/Down Counter (dual clock)           | 30      |            | 30    |
| 74194    | 4-Bit Bidirectional Shift Register           | 10      |            | 10    |
| 74244    | Octal Tri-State Buffer                       | 16      |            | 16    |
| 74245    | Octal Bus Transceiver                        | 16      |            | 16    |
| 74HCT245 | Octal Bus Transceiver (HCT — LS fence)       | 16      |            | 16    |
| 74273    | Octal D Flip-Flop with Clear                 | 8       |            | 8     |
| 74283    | 4-Bit Binary Adder                           | 8       |            | 8     |
| 74373    | Octal Transparent Latch                      | 8       |            | 8     |
| 74374    | Octal D Flip-Flop                            | 4       |            | 4     |
| 74HCT374 | Octal D Flip-Flop (HCT — HC374 unobtainable) | 16      |            | 16    |
| 74377    | Octal D Flip-Flop with Enable                | 8       |            | 8     |
| 74393    | Dual 4-Bit Binary Counter                    | 12      |            | 12    |
| 74541    | Octal Buffer                                 | 8       |            | 8     |
| 74573    | Octal Transparent Latch (flow-through)       | 8       |            | 8     |
| 74574    | Octal D Flip-Flop (flow-through)             | 8       |            | 8     |
| 74595    | 8-Bit Shift Register with Output Latch       | 4       |            | 4     |
| 74670    | 4×4 Register File (tri-state)                | 32      |            | 32    |
| 74688    | 8-Bit Identity Comparator                    | 3       |            | 3     |

## Notes

- Parts with a count of 0 have a labelled drawer reserved but no stock (the LS/HCT
  rows are new orders without dedicated drawers yet).
- **In Transit** shows quantities ordered but not yet arrived; merge into On Hand on
  receipt and clear the column.
- **Total** is On Hand + In Transit.
- HCT parts accept both HC and LS input levels and drive both families — they are
  the boundary parts wherever an LS output (notably the 74LS181) feeds the HC world.
