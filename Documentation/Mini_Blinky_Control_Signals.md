# Mini Blinky CPU — Control Signals by Instruction (`W = 4`)

Single-cycle: every signal below is the value the decode GALs assert **during the one cycle** that executes the instruction. State changes happen on the rising edge that ends the cycle. All address/operand paths come from the instruction currently in the IR.

This table is derived from the datapath defined in `Mini_Blinky_CPU.md`. The `'181` select codes are taken from the simulator's `Hc181.cs` (datasheet "Active-HIGH operands" function table). The `'194`/`'169`/`2114` control polarities are datasheet-derived — those parts are not yet modelled in the sim, so treat their exact pin senses as provisional until the models land.

---

## Control Signal Dictionary

**Group A — ALU / shifter / stack-top / flags**

| Signal | Values | Meaning |
|---|---|---|
| `ALU_S3..S0` | 4-bit | `'181` function select |
| `ALU_M` | arith / logic | `'181` mode (M=L arith, M=H logic) |
| `ALU_Cn` | H / L / /C | carry-in pin: H = no inject, L = +1 inject, /C = inject the C flag |
| `TOS_MODE` | hold / load / shL / shR | `'194` mode (load = parallel-load from `TOS_SRC`) |
| `TOS_SRC` | LIT / DMEM / INP / RTOS / ALU / NOS | parallel-load source (only when `TOS_MODE = load`) |
| `NOS` | hold / ←TOS / ←stkR | NOS latch: hold, load old TOS, or load data-stack read |
| `FLAG_WE` | ✓ / – | latch N/Z/C this cycle (ALU class only) |
| `C_SRC` | 181 / shf | C flip-flop source: `'181` Cn+4, or the bit shifted out of the `'194` |

**Group B — sequencing / memory / I/O**

| Signal | Values | Meaning |
|---|---|---|
| `PCSEL` | +1 / LIT / RTOS | NEXTPC mux: PC+1, IR literal (branch/call target), or RTOS (return). Conditional forms select LIT only if the flag is set, else +1. |
| `DSP` | 0 / +1 / −1 | data-stack pointer `'169`: count enable + up/down |
| `DSTK` | – / wr / rd | data-stack `2114`: idle, write (push spills old NOS), read (pop fills NOS) |
| `RSP` | 0 / +1 / −1 | return-stack pointer `'169` |
| `RSTK` | – / wr(PC+1) / wr(TOS) | return-stack `2114`: idle, or write with data-in = PC+1 (CALL) or TOS (>R). Read is implicit when RTOS is consumed. |
| `DMEM` | – / rd@LIT / wr@LIT | data memory `2114`, address = IR literal |
| `IO` | – / RD@LIT / WR@TOS | I/O strobe + address-source mux: IN reads port = literal, OUT writes port = TOS |
| `CLK` | run / **STOP** | clock-stop (HALT) |

**Idle / default vector** (what an instruction that "does nothing" asserts): `ALU` –, `TOS` hold, `NOS` hold, `FLAG_WE` –, `PCSEL` +1, `DSP` 0, `DSTK` –, `RSP` 0, `RSTK` –, `DMEM` –, `IO` –, `CLK` run. A blank/`–` cell below means this default.

---

The two tables below group signals **functionally** (A = datapath, B = sequencing/memory/I/O). This is for reading the control logic, not the chip assignment — the actual signal-to-device mapping is in the GAL Partition section at the end, where the groups are re-split across four devices.

## Table A — Group A (ALU / shifter / stack-top / flags)

| Instr | ALU_S | ALU_M | ALU_Cn | TOS_MODE | TOS_SRC | NOS | FLAG_WE | C_SRC |
|---|---|---|---|---|---|---|---|---|
| **SYS** NOP | – | – | – | hold | – | hold | – | – |
| HALT | – | – | – | hold | – | hold | – | – |
| RET | – | – | – | hold | – | hold | – | – |
| DUP | – | – | – | hold | – | ←TOS | – | – |
| DROP | – | – | – | load | NOS | ←stkR | – | – |
| SWAP | – | – | – | load | NOS | ←TOS | – | – |
| >R | – | – | – | load | NOS | ←stkR | – | – |
| R> | – | – | – | load | RTOS | ←TOS | – | – |
| **ALU** ADD | 1001 | arith | H | load | ALU | ←stkR | ✓ | 181 |
| ADC | 1001 | arith | /C | load | ALU | ←stkR | ✓ | 181 |
| SUB | 0110 | arith | L | load | ALU | ←stkR | ✓ | 181 |
| XOR | 0110 | logic | – | load | ALU | ←stkR | ✓ | 181 |
| NOT | 0000 | logic | – | load | ALU | hold | ✓ | 181 |
| CMP | 0110 | arith | L | load | NOS | ←stkR | ✓ | 181 |
| SHL | – | – | – | shL | – | hold | ✓ | shf |
| SHR | – | – | – | shR | – | hold | ✓ | shf |
| PUSH #imm | – | – | – | load | LIT | ←TOS | – | – |
| LOAD #addr | – | – | – | load | DMEM | ←TOS | – | – |
| STORE #addr | – | – | – | load | NOS | ←stkR | – | – |
| IN #port | – | – | – | load | INP | ←TOS | – | – |
| OUT | – | – | – | load | NOS | ←stkR | – | – |
| BRANCH addr | – | – | – | hold | – | hold | – | – |
| BEQ addr | – | – | – | hold | – | hold | – | – |
| BCS addr | – | – | – | hold | – | hold | – | – |
| BMI addr | – | – | – | hold | – | hold | – | – |
| CALL addr | – | – | – | hold | – | hold | – | – |

## Table B — Group B (sequencing / memory / I/O)

| Instr | PCSEL | DSP | DSTK | RSP | RSTK | DMEM | IO | CLK |
|---|---|---|---|---|---|---|---|---|
| **SYS** NOP | +1 | 0 | – | 0 | – | – | – | run |
| HALT | +1 | 0 | – | 0 | – | – | – | **STOP** |
| RET | RTOS | 0 | – | −1 | – (rd) | – | – | run |
| DUP | +1 | +1 | wr | 0 | – | – | – | run |
| DROP | +1 | −1 | rd | 0 | – | – | – | run |
| SWAP | +1 | 0 | – | 0 | – | – | – | run |
| >R | +1 | −1 | rd | +1 | wr(TOS) | – | – | run |
| R> | +1 | +1 | wr | −1 | – (rd) | – | – | run |
| **ALU** ADD | +1 | −1 | rd | 0 | – | – | – | run |
| ADC | +1 | −1 | rd | 0 | – | – | – | run |
| SUB | +1 | −1 | rd | 0 | – | – | – | run |
| XOR | +1 | −1 | rd | 0 | – | – | – | run |
| NOT | +1 | 0 | – | 0 | – | – | – | run |
| CMP | +1 | −1 | rd | 0 | – | – | – | run |
| SHL | +1 | 0 | – | 0 | – | – | – | run |
| SHR | +1 | 0 | – | 0 | – | – | – | run |
| PUSH #imm | +1 | +1 | wr | 0 | – | – | – | run |
| LOAD #addr | +1 | +1 | wr | 0 | – | rd@LIT | – | run |
| STORE #addr | +1 | −1 | rd | 0 | – | wr@LIT | – | run |
| IN #port | +1 | +1 | wr | 0 | – | – | RD@LIT | run |
| OUT | +1 | −1 | rd | 0 | – | – | WR@TOS | run |
| BRANCH addr | LIT | 0 | – | 0 | – | – | – | run |
| BEQ addr | LIT if Z else +1 | 0 | – | 0 | – | – | – | run |
| BCS addr | LIT if C else +1 | 0 | – | 0 | – | – | – | run |
| BMI addr | LIT if N else +1 | 0 | – | 0 | – | – | – | run |
| CALL addr | LIT | 0 | – | +1 | wr(PC+1) | – | – | run |

---

## Notes on the subtle rows

- **CMP** runs the `'181` as a full `SUB` (S=`0110`, arith, Cn=L) so the flags are real, but `TOS_SRC = NOS` instead of `ALU` — the difference is computed, latched into flags, and thrown away. The ΔDSP = −1 is just the ordinary pop; the top after `CMP` is the old NOS. This is the row that proves `FLAG_WE` is independent of the TOS write path.
- **OUT** is the stack form `( data port -- data )`: port address = old TOS (so `IO = WR@TOS`), data written = old NOS, then TOS pops (new TOS = old NOS). The written value survives on top, which is what lets a loop fan one byte across several ports without re-pushing.
- **DUP** holds the `'194` (new TOS = old TOS) and drives `NOS ← TOS`; the old NOS spills to the stack `2114` (`DSTK = wr`) as DSP counts up.
- **STORE** data-in to the `2114` is the old TOS; the literal addresses D-memory. The pop fills NOS from the stack read.
- **Conditional branches** are the only place a flag feeds control: `PCSEL` is a function of opcode **and** the selected flag. `BRANCH`/`CALL` force `LIT` unconditionally.
- **HALT** asserts `CLK = STOP`; with the clock stopped the PC and all state freeze, so `PCSEL` is don't-care.
- **`'181` code sharing:** `SUB` and `XOR` are both select `0110`, separated only by `ALU_M`; `ADD` and `ADC` are both `1001`, separated only by `ALU_Cn`. The GAL spends one fewer select bit because of this overlap.
- **Stack read address:** on a pop the `2114` is read at the cell below the live top while the DSP `'169` counts down; on a push the old NOS is written as the pointer counts up. Whether that is pre- or post-increment addressing is a board-level wiring choice, not an architectural one — left open here.

## GAL Partition (16V8 / 20V8)

Target devices: **GAL16V8** and **GAL20V8**. The leading number is the array's *input* capacity; the `V8` is the output count. Both have **8 output macrocells**. Counting signal pins (total − 2 power): a 16V8 (20-pin) has **18 signal pins**, a 20V8 (24-pin) has **22**. With all 8 outputs used, a 16V8 leaves ~10 inputs and a 20V8 leaves ~12–14 (exact dedicated-input counts shift with OLMC mode — confirm against the datasheet before committing a pinout).

### Per-RAM `/CS` + `/WE` decode

Each `2114` is decoded with **both** `CS` and `WE` (not `CS` tied low), so an idle RAM is fully deselected and its bidirectional I/O pins are high-Z except on its own access cycle — no reliance on `/WE` timing alone to keep a chip off the shared data bus. Both terms are pure functions of opcode / sub-op; **no flag inputs**.

| RAM | `CS` asserted on | `WE` asserted on (write) |
|---|---|---|
| Data stack | DUP, DROP, >R, R>, ADD, ADC, SUB, XOR, CMP, PUSH, LOAD, STORE, IN, OUT | DUP, R>, PUSH, LOAD, IN |
| Return stack | RET, >R, R>, CALL | >R, CALL |
| D-memory | LOAD, STORE | STORE |

Adding `CS` is **+1 line per RAM**, so the control vector grows **29 → 32**. Across four devices that is exactly 8 outputs each — every device runs full, which is what forces one of them to be the wider part (below). In Table A / Table B above, the `DSTK` / `RSTK` / `DMEM` cells (`– / rd / wr`) map onto this decode directly: `–` = `CS` deasserted; `rd` = `CS` asserted, `WE` high; `wr` = `CS` asserted, `WE` low.

### Four-device partition — 3× 16V8 + 1× 20V8

The governing constraint is that **all three flag inputs (N, Z, C) belong on one device**: only `PCSEL` (reads N, Z, C) and `ALU_Cn` (reads C) consume flags, so co-locating them pays the flag-input cost once instead of twice. Every other output decodes from instruction bits alone (opcode + sub-op nibble = 8 inputs).

| GAL | Part | Outputs (8) | Inputs |
|---|---|---|---|
| **1 — ALU / shift select** | 16V8 | `ALU_S0..S3`, `ALU_M`, `C_SRC`, `TOS_M0`, `TOS_M1` | opcode + sub-op = **8** |
| **2 — stack-top + D-stack RAM** | 16V8 | `TOS_SRC0..2`, `NOS_LD`, `NOS_SRC`, `FLAG_WE`, `DSTK_CS`, `DSTK_WE` | opcode + sub-op = **8** |
| **3 — pointers + R-stack RAM + clock** | 16V8 | `DSP_CNT`, `DSP_UD`, `RSP_CNT`, `RSP_UD`, `RSTK_CS`, `RSTK_WE`, `RDIN_SEL`, `CLK_RUN` | opcode + sub-op = **8** |
| **4 — control flow + I/O + Cn + D-mem RAM** | **20V8** | `PCSEL0`, `PCSEL1`, `ALU_Cn`, `IO_RD`, `IO_WR`, `IOADDR_SEL`, `DMEM_CS`, `DMEM_WE` | opcode + sub-op + **N, Z, C** = **11** |

**Why GAL 4 is the 20V8, as pin arithmetic.** It needs 8 outputs + 11 inputs = **19** signal pins. A 16V8 has only **18** — over by one. A 20V8 (22) fits with margin. This is structural, not a packing accident: with four devices carrying 32 outputs, three of them max out at 24, leaving the flag-reading device needing the remaining 8 — and a 16V8 that already spends 11 pins on inputs can host at most 18 − 11 = **7** outputs. One past the ceiling → 20V8.

### All-16V8 alternative — 5 devices

To avoid stocking a 20V8, split GAL 4: keep 7 outputs on a 16V8 (7 + 11 inputs = 18 pins, an exact fit) and move the eighth output to a fifth 16V8. One extra chip to standardise on a single part — a bench-stocking tradeoff, not an architectural one.

### Scaling note

At `W = 8` the same partitioning pressure (wider `PCSEL` reach is unchanged, but the full ALU/branch set and wider mux selects add outputs) is what puts the parent machine at **four GALs**. The mini lands at 4 (or 5 all-16V8) for the same reason, once `/CS` is decoded per RAM.
