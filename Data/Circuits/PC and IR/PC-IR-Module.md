# Generic PC + IR Module for TTL CPUs

A reusable Program Counter and Instruction Register block designed to drop into
8-bit (and 16-bit) TTL CPU projects with a single 8-bit data bus, a MAR-driven
address bus, and a rising-edge Φ2 clock. All registered elements clock on the
same edge; no gated clocks anywhere.

---

## 1. Design goals

- **One clock edge, no gated clocks.** Everything registers on the rising edge of CLK; all control signals are enables, valid for the whole cycle.
- **Byte-wise access to a 16-bit PC over an 8-bit bus.** Split load enables and split output enables so JMP/JSR/RTS work on a single 8-bit data bus without extra latches inside the module.
- **RESET > LOAD > INC > HOLD priority for free**, because that is exactly the built-in priority of the 74HC163.
- **Portable across projects.** Works in the '181 Rev B machine, the 74670 register-file CPU, and (with both halves enabled together) a 16-bit-bus machine.

## 2. Signal interface (the contract)

| Signal | Dir | Active | Function |
|---|---|---|---|
| CLK | in | rising | System Φ2. Common to PC and IR. |
| RESET# | in | low | Synchronous clear of PC to $0000 (needs one clock edge while low). |
| PC_INC | in | high | PC ← PC + 1 on next edge. |
| PC_LD_L# | in | low | PC[7:0] ← DB on next edge. |
| PC_LD_H# | in | low | PC[15:8] ← DB on next edge. |
| PC_DB_L# | in | low | Drive PC[7:0] onto data bus (for pushing return address). |
| PC_DB_H# | in | low | Drive PC[15:8] onto data bus. |
| IR_LD# | in | low | IR ← DB on next edge. |
| DB[7:0] | bi | — | System data bus. |
| PC[15:0] | out | — | Buffered PC value to the MAR inputs / address path (always driven). |
| IR[7:0] | out | — | Instruction register outputs, straight to microcode ROM address lines. |

Rules of use:

- Never assert PC_INC in the same cycle as either load — the un-loaded half would count while the other half loads. The '163 gives load priority *per chip*, so a partial load plus INC produces a half-loaded, half-incremented PC. Just don't.
- PC_LD_L# and PC_LD_H# together = full 16-bit load (this is the 16-bit-bus configuration, with DB widened to 16 and both halves wired to it).
- PC_DB_L#/PC_DB_H# are ordinary bus enables; microcode discipline keeps them exclusive with every other data-bus driver, same as the rest of your machine.

## 3. PC core — 4 × 74HC163

Four synchronous 4-bit counters, cascaded low-nibble to high-nibble (U1 = PC[3:0] … U4 = PC[15:12]).

Per-chip wiring:

| '163 pin | U1 (PC 3:0) | U2 (PC 7:4) | U3 (PC 11:8) | U4 (PC 15:12) |
|---|---|---|---|---|
| 1 SR# (sync clear) | RESET# | RESET# | RESET# | RESET# |
| 2 CLK | CLK | CLK | CLK | CLK |
| 3–6 D0–D3 | DB0–DB3 | DB4–DB7 | DB0–DB3 | DB4–DB7 |
| 7 CEP (ENP) | PC_INC | PC_INC | PC_INC | PC_INC |
| 9 PE# (load) | PC_LD_L# | PC_LD_L# | PC_LD_H# | PC_LD_H# |
| 10 CET (ENT) | +5V | U1 pin 15 | U2 pin 15 | U3 pin 15 |
| 15 TC (RCO) | → U2 CET | → U3 CET | → U4 CET | n/c |
| 14,13,12,11 Q0–Q3 | PC0–3 | PC4–7 | PC8–11 | PC12–15 |

Notes:

- **Synchronous everything.** Load and clear both take effect on the clock edge, so a JMP is simply: put the byte on DB, assert the load, wait for the edge. No async glitching, no MAR corruption mid-cycle.
- **Cascade scheme:** ENP is the master count enable on all four chips; the carry ripples through the CET/TC chain. Worst case is clock→TC on U1 plus two CET→TC hops plus CET setup on U4 — roughly 100–120 ns in HC at 5 V. Comfortably good to ~6–8 MHz, which covers everything you're building. If you ever want more, replace the ripple with parallel AND gating of the TC outputs (one HC08), but it's not worth the IC on these machines.
- D inputs of U1/U3 and U2/U4 share the same DB nibbles because loads are byte-at-a-time; only the half whose PE# is low actually captures.

## 4. Bus drivers

**Address side — 2 × 74HC541 (U5, U6):** PC[15:0] through always-enabled buffers (both OE# pins grounded) to the MAR inputs / internal address path. This isolates the '163 Q pins from bus capacitance and gives you clean drive. In a machine where only the MAR ever looks at PC, you can omit these and wire Q straight across — they're cheap insurance for a *generic* module, since some hosts (the 16-bit machine) let PC drive the address bus directly during fetch; in that case wire the OE# pins to a PC_AB# control instead of ground.

**Data-bus side — 2 × 74HC541 (U7 = PCL, U8 = PCH):** inputs from PC[7:0] and PC[15:8], outputs onto DB, enables PC_DB_L# and PC_DB_H# (tie each chip's two OE# pins together). These exist solely so JSR can push the return address and so a front panel / debug port can read PC. If your host CPU has no CALL and no debug read-back, omit both and reclaim two ICs.

## 5. IR — 1 × 74HC377

| '377 pin | Connection |
|---|---|
| 1 E# | IR_LD# |
| 11 CLK | CLK |
| D1–D8 | DB0–DB7 |
| Q1–Q8 | IR0–IR7 → microcode ROM address lines |

The '377's clock-enable is exactly the right part here: the register clocks every cycle but only captures when IR_LD# is low, keeping the no-gated-clocks rule. Outputs are permanently enabled and go straight to the microcode ROM — no tri-state needed. If a host machine ever needs to read IR back onto the bus (front-panel tracing), swap in an HC374 plus a '541, but don't pay for it by default.

**IR at reset:** the '377 has no clear, so IR is garbage after power-up. That's fine as long as your microcode sequencer's step counter *is* cleared by reset and step 0 of every opcode row is the identical fetch sequence (which is how your Rev B microcode is laid out anyway). Alternative if a host needs it: gate the microcode ROM's IR address lines low during reset with a '541 whose OE is driven by RESET — but the duplicated-fetch convention is free.

## 6. Reset behaviour

RESET# to the '163 SR# pins clears PC to $0000 **synchronously** — the clock must be running during reset, which it is in all your designs (reset does not stop the oscillator). Boot code therefore lives at $0000; decode your EEPROM there.

If a host wants a 6502-style high vector instead, the cheapest trick is microcode: reserve opcode $00's step-0 row as "load PC from fixed vector" isn't possible without a constant source, so simpler options are (a) map ROM at the bottom, or (b) put a `JMP $E000` as the first bytes at $0000. Option (b) costs nothing and keeps the module generic.

Priority on any given edge, straight from the '163 datasheet: **SR# (clear) beats PE# (load) beats count beats hold.** RESET always wins.

## 7. Timing budget (HC @ 5 V, worst case)

| Path | Approx. |
|---|---|
| CLK → Q ('163) | 35 ns |
| CLK → TC (U1), then 2 × CET→TC hops | 35 + 2 × 30 = 95 ns |
| CET setup (U4) before next edge | 20 ns |
| **INC critical path total** | **~115 ns → ≥ 8 MHz** |
| DB valid → PE#/D setup ('163) | 20 ns before edge |
| PC_DB_x# → DB valid ('541 OE→Y) | 30 ns |
| CLK → IR outputs ('377) | 35 ns, then microcode ROM tAA on top |

The INC ripple is the module's longest internal path and it still isn't your machine's critical path — the microcode ROM access + ALU path will dominate, as it does in Rev B.

## 8. Microcode recipes

Signals not listed are inactive. "M[MAR]" means the memory cycle your host already does.

**Fetch (every opcode, step 0–1):**

| Step | Controls | Effect |
|---|---|---|
| F0 | MAR_LD from PC | MAR ← PC |
| F1 | MEM_RD, IR_LD#, PC_INC | IR ← M[MAR], PC ← PC+1 |

**JMP absolute (operands lo, hi):**

| Step | Controls | Effect |
|---|---|---|
| 1 | MAR ← PC; MEM_RD, X_LD, PC_INC | X ← lo, PC++ |
| 2 | MAR ← PC; MEM_RD, **PC_LD_H#** | PC[15:8] ← hi (PCL still old — harmless, no fetch this cycle) |
| 3 | X → DB, **PC_LD_L#** | PC[7:0] ← lo. Done. |

This is the whole reason for split loads: a 16-bit jump lands over an 8-bit bus in two load edges with only your existing X temp, no extra latch in the module.

**JSR absolute (host with SP, pushes return address = address after operands):**

| Step | Controls | Effect |
|---|---|---|
| 1 | MAR ← PC; MEM_RD, X_LD, PC_INC | X ← lo, PC++ |
| 2 | MAR ← PC; MEM_RD, Y/temp or reorder, PC_INC | hi in hand, PC now = return addr |
| 3 | MAR ← SP; **PC_DB_H#**, MEM_WR, SP− | push PCH |
| 4 | MAR ← SP; **PC_DB_L#**, MEM_WR, SP− | push PCL |
| 5–6 | load PC from hi then lo as in JMP | PC ← target |

(Exact ordering bends to fit each host's temp-register budget — on Rev B with a single X temp you push *before* fetching hi, i.e. steps 3–4 between 1 and 2, since PC_DB uses no temp. The module doesn't care; it just exposes the enables.)

**RTS:** pop lo → X, pop hi → DB with PC_LD_H#, then X → DB with PC_LD_L#. Same two-edge load pattern.

## 9. Bill of materials

| Ref | Part | Role | Optional? |
|---|---|---|---|
| U1–U4 | 74HC163 | PC counter, 16 bits | no |
| U5–U6 | 74HC541 | PC → address-path buffers | omit if MAR is sole consumer and traces are short |
| U7–U8 | 74HC541 | PCL/PCH → data bus | omit if host has no CALL / debug read |
| U9 | 74HC377 | IR | no |
| — | 100 nF X7R per IC | decoupling | no |

Full-featured: **9 ICs**. Minimal (no CALL, no buffers): **5 ICs**.

**HC vs HCT:** all-HC bus → HC throughout. In the '181 Rev B machine, where LS outputs share the data bus, use **HCT163/HCT377/HCT541** for anything whose inputs see LS-driven levels, consistent with your existing HCT boundary policy.

## 10. Drop-in notes per project

- **'181 Rev B:** direct replacement for the existing HC163×4 PC + HC374 IR; the '377 swap removes the IR's tri-state (unused there anyway) and the gated-load awkwardness. Split PC loads slot straight into the existing JMP microcode.
- **74670 register-file CPU:** use the 5-IC minimal build; the 16-opcode ISA has no CALL, so drop U7/U8, and PC feeds MAR directly.
- **16-bit machine:** widen DB to 16, tie PC_LD_L# and PC_LD_H# together (and likewise the DB enables), and jumps become single-edge loads. Same schematic, fewer control lines used.
