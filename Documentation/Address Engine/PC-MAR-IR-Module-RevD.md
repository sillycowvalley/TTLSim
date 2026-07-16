# PC + Counting MAR + IR Module — '163 Core (Rev D, consolidated)

Final consolidated design: 74HC163-based PC **and** 74HC163-based counting
MAR, plus the HC377 IR, with the microcode convention that makes the counting
MAR pay off maximally — the **shadow-MAR invariant**. This supersedes the
earlier PC-only ('163) and '191-substitution documents.

Headline: against the plain HC374×2 MAR baseline, the counting MAR saves **2
cycles on every instruction fetched**, rising to 4–6 cycles on multi-byte and
jump instructions, for +2 ICs and +1 control bit.

---

## 1. Module contents

| Block | Parts | Notes |
|---|---|---|
| PC | 4 × 74HC163 | sync load, sync clear, ENT/TC ripple cascade |
| MAR | 4 × 74HC163 | identical wiring; a '161 is drop-in **in this socket only** (see §7) |
| IR | 1 × 74HC377 | clock-enable register, outputs direct to microcode ROM |
| PC→DB drivers | 2 × 74HC541 | PCL/PCH onto data bus (JSR push, MAR reload, debug) |
| Address buffers | 0–2 × 74HC541 | optional; MAR Q pins can drive the address bus directly on a small board |

Full module: **11 ICs** (9 without optional buffers). Everything clocks on the
rising edge of Φ2; every control is a plain enable, valid for the whole cycle;
no gated clocks, no level-sensitive loads, no gating glue — the '163 needs
none of the HC00 machinery the '191 required.

## 2. Signal interface

| Signal | Active | Function |
|---|---|---|
| CLK | rising | System Φ2, common to all three blocks. |
| RESET# | low | Synchronous clear of **both** PC and MAR to $0000. |
| PC_INC / MAR_INC | high | Increment on next edge. |
| PC_LD_L# / PC_LD_H# | low | PC byte ← DB on next edge. |
| MAR_LD_L# / MAR_LD_H# | low | MAR byte ← DB on next edge. |
| PC_DB_L# / PC_DB_H# | low | Drive PC byte onto DB. |
| IR_LD# | low | IR ← DB on next edge. |
| DB[7:0] | — | System data bus. |
| A[15:0] | — | MAR outputs → address bus. |
| IR[7:0] | — | → microcode ROM address lines. |

Rules (all per-block): never assert INC with a *partial* load — the un-loaded
half counts while the other half loads. INC with a **full** load is pointless
but harmless (load wins on the '163). DB output enables exclusive with all
other bus drivers, as everywhere else in the machine.

One property worth naming because the recipes below lean on it: **a
synchronous load can be asserted during a memory-read cycle.** MAR holds its
old value for the entire cycle (the read uses it), and the load captures the
bus at the closing edge. The '191 could not do this — its outputs wandered
mid-window — and it is what lets jump targets land in PC and MAR *during* the
operand read, for free.

## 3. Wiring

### PC and MAR (identical, 4 × '163 each)

| '163 pin | chip 0 (3:0) | chip 1 (7:4) | chip 2 (11:8) | chip 3 (15:12) |
|---|---|---|---|---|
| 1 SR# | RESET# | RESET# | RESET# | RESET# |
| 2 CLK | CLK | CLK | CLK | CLK |
| 3–6 A–D | DB0–DB3 | DB4–DB7 | DB0–DB3 | DB4–DB7 |
| 7 ENP | x_INC | x_INC | x_INC | x_INC |
| 9 PE# | x_LD_L# | x_LD_L# | x_LD_H# | x_LD_H# |
| 10 ENT | +5 V | chip 0 TC | chip 1 TC | chip 2 TC |
| 15 TC | → chip 1 ENT | → chip 2 ENT | → chip 3 ENT | n/c |
| 14–11 QA–QD | Q0–3 | Q4–7 | Q8–11 | Q12–15 |

(x = PC or MAR.) PC Q pins go to the PC→DB '541s; MAR Q pins go to the address
bus (buffered or not). Carry-ripple worst case ~115 ns → fine beyond 8 MHz;
not the machine's critical path.

### IR (74HC377)

E# = IR_LD#, CLK = Φ2, D = DB0–7, Q = IR0–7 → microcode ROM. No clear needed:
the reset-forced step sequence loads IR before anything reads it.

## 4. The shadow-MAR convention

The counting MAR's full value comes from one invariant, maintained by
microcode:

> **At the start of every opcode fetch, MAR = PC.**

Hardware establishes it at reset for free — both blocks share RESET#, so both
wake at $0000 in lockstep on the same edge. Microcode then preserves it with
three rules:

1. **Fetch and operand reads increment both.** Every instruction-stream read
   is one step: `MEM_RD, dest_LD, PC_INC, MAR_INC`. No MAR reloads inside the
   instruction stream, ever.
2. **Memory-data instructions restore MAR at the tail.** Anything that pointed
   MAR at data (LDA/STA/etc.) ends with two steps:
   `PC_DB_L# + MAR_LD_L#`, then `PC_DB_H# + MAR_LD_H#`.
3. **Jumps load PC and MAR together.** Wherever a target byte lands in PC,
   assert the matching MAR load in the same cycle — same bus byte, both
   registers, zero extra steps. The invariant survives the jump untouched.

The accounting logic: the old design paid a 2-step MAR←PC reload before *every
single fetch*. The convention moves that cost to the tail of memory-data
instructions *only*, and deletes it everywhere else.

## 5. Microcode recipes

**Fetch (every opcode) — 1 step:**

| Step | Controls |
|---|---|
| F0 | MEM_RD, IR_LD#, PC_INC, MAR_INC |

**JMP abs — 3 steps after fetch:**

| Step | Controls | Effect |
|---|---|---|
| 1 | MEM_RD, X_LD, PC_INC, MAR_INC | X ← lo |
| 2 | MEM_RD, PC_LD_H#, MAR_LD_H# | hi lands in both, straight off the read |
| 3 | X→DB, PC_LD_L#, MAR_LD_L# | done; MAR = PC = target |

(Step 2: no INC — partial-load rule. The stale low halves don't matter; step 3
overwrites them.)

**LDA abs — 6 steps after fetch:**

| Step | Controls | Effect |
|---|---|---|
| 1 | MEM_RD, X_LD, PC_INC, MAR_INC | X ← EA lo |
| 2 | MEM_RD, PC_INC, MAR_LD_H# | EA hi → MAR high, PC steps past operand |
| 3 | X→DB, MAR_LD_L# | MAR = EA |
| 4 | MEM_RD, A_LD (via ALU pass) | A ← M[EA] |
| 5 | PC_DB_L#, MAR_LD_L# | restore |
| 6 | PC_DB_H#, MAR_LD_H# | restore; invariant holds |

(Step 2 is the subtle one: PC_INC with MAR_LD_H# is legal — the partial-load
rule is per block, and PC is doing a full increment while MAR does a
half-load. MAR's un-loaded low half must not count, and MAR_INC is indeed not
asserted.)

**JSR / RTS:** as in the Rev B doc, with MAR pointed at the stack via SP for
the pushes/pops, then rule 2's restore tail (JSR) or rule 3's paired loads
(RTS pops hi → PC_LD_H#+MAR_LD_H#, X → PC_LD_L#+MAR_LD_L#).

## 6. Cycle accounting (vs. HC374×2 MAR baseline)

Baseline charges 2 steps of MAR←PC before every instruction-stream read.

| Instruction shape | Baseline | Rev D | Saved |
|---|---|---|---|
| 1-byte (register op) | 3 | 1 | **2** |
| 2-byte (imm) | 6 | 2 | **4** |
| 3-byte JMP abs | 10 | 4 | **6** |
| 3-byte LDA abs | 12 | 7 | **5** |
| Block copy inner loop | reload MAR per byte | MAR_INC per byte | ~3/byte |

Every instruction executed gets faster; nothing gets slower. On typical code
(heavy in 1–2 byte ops) this is roughly a 30–40 % throughput gain for +2 ICs
and one spare control-word bit (MAR_INC — the MAR_LD lines already existed).

## 7. Stock and part-substitution notes

- **PC sockets: '163 required.** The synchronous clear is doing real work
  there (clean, lockstep reset release).
- **MAR sockets: '161 is drop-in identical.** MAR's SR# is tied to RESET# only
  to establish the reset invariant, and at reset the async-vs-sync clear
  distinction is harmless (the boot row fetches before anything depends on
  skew). Spend remaining '161 stock here and in the microcode step counter;
  put fresh '163s in the PC.
- **HC vs HCT:** as before — HCT at any input that sees LS-driven levels in
  the '181 machine, plain HC elsewhere. Both counters are commonly available
  in both families.
- Decoupling: 100 nF X7R per IC, as always.
