# 8080-ish CPU — Instruction Set Analysis

Byte-fetched, variable-length instructions like the real 8080.
One machine cycle per fetch/memory/IO operation, each 3–5 T-states.
Cycle counts shown as **ours vs 8080** where they differ, or just the count where identical.

---

## Register Naming

| Slot | Primary | Shadow (Z80 EXX) |
|------|---------|-----------------|
| 0    | A       | A'              |
| 1    | F       | F'              |
| 2    | B       | B'              |
| 3    | C       | C'              |
| 4    | D       | D'              |
| 5    | E       | E'              |
| 6    | H       | H'              |
| 7    | L       | L'              |
| 8    | SP (16-bit)              |
| 9    | PC (16-bit)              |
| 10   | IX (Z80)                 |
| 11   | IY (Z80)                 |

Bank-select FF drives ADDR[3] for slots 0–7; toggled by EXX / EX AF,AF'.

---

## Group 1 — Register-to-Register MOV

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| MOV r, r'      | 1     | 5           | 5      | Read src port A, byte-merge into dst slot |
| MOV r, M       | 1     | 7           | 7      | HL→MAR, SRAM read, byte-merge write |
| MOV M, r       | 1     | 7           | 7      | HL→MAR, SRAM write from src |

**Assessment: identical cycle counts. Trivial.**

---

## Group 2 — 8-bit Immediate Load

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| MVI r, n       | 2     | 7           | 7      | Fetch opcode + immediate byte, write to register half |
| MVI M, n       | 2     | 10          | 10     | Fetch both bytes, HL→MAR, write to SRAM |

**Assessment: identical. Straightforward.**

---

## Group 3 — 16-bit Immediate Load

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| LXI B, nn      | 3     | 10          | 10     | Fetch opcode + 2 data bytes, write to BC slot |
| LXI D, nn      | 3     | 10          | 10     | |
| LXI H, nn      | 3     | 10          | 10     | |
| LXI SP, nn     | 3     | 10          | 10     | |

**Assessment: identical. Three fetch machine cycles, write 16-bit pair to slot.**

---

## Group 4 — 8-bit ALU (register and immediate)

All operate on accumulator (A slot, upper byte). '181 handles all natively.

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| ADD r          | 1     | 4           | 4      | A ← A + r |
| ADD M          | 1     | 7           | 7      | A ← A + SRAM[HL] |
| ADI n          | 2     | 7           | 7      | A ← A + imm |
| ADC r          | 1     | 4           | 4      | A ← A + r + C |
| ADC M          | 1     | 7           | 7      | |
| ACI n          | 2     | 7           | 7      | |
| SUB r          | 1     | 4           | 4      | A ← A − r |
| SUB M          | 1     | 7           | 7      | |
| SUI n          | 2     | 7           | 7      | |
| SBB r          | 1     | 4           | 4      | A ← A − r − C |
| SBB M          | 1     | 7           | 7      | |
| SBI n          | 2     | 7           | 7      | |
| ANA r          | 1     | 4           | 4      | A ← A AND r |
| ANA M          | 1     | 7           | 7      | |
| ANI n          | 2     | 7           | 7      | |
| ORA r          | 1     | 4           | 4      | A ← A OR r |
| ORA M          | 1     | 7           | 7      | |
| ORI n          | 2     | 7           | 7      | |
| XRA r          | 1     | 4           | 4      | A ← A XOR r |
| XRA M          | 1     | 7           | 7      | |
| XRI n          | 2     | 7           | 7      | |
| CMP r          | 1     | 4           | 4      | flags ← A − r, no write |
| CMP M          | 1     | 7           | 7      | |
| CPI n          | 2     | 7           | 7      | |

**Assessment: identical across the board. '181 function-select maps directly.**
**Carry flag into Cn for ADC/SBB. WE suppressed for CMP.**

---

## Group 5 — 8-bit Increment / Decrement

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| INR r          | 1     | 5           | 5      | r ← r + 1; flags S,Z,AC,P updated; C unchanged |
| INR M          | 1     | 10          | 10     | Read SRAM[HL], +1, write back |
| DCR r          | 1     | 5           | 5      | r ← r − 1; same flag rules |
| DCR M          | 1     | 10          | 10     | |

**Assessment: identical. GAL must suppress C flag update — one extra product term.**

---

## Group 6 — 16-bit Increment / Decrement / Add

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| INX B          | 1     | 5           | 5      | BC ← BC + 1; no flags |
| INX D          | 1     | 5           | 5      | |
| INX H          | 1     | 5           | 5      | |
| INX SP         | 1     | 5           | 5      | |
| DCX B          | 1     | 5           | 5      | BC ← BC − 1; no flags |
| DCX D          | 1     | 5           | 5      | |
| DCX H          | 1     | 5           | 5      | |
| DCX SP         | 1     | 5           | 5      | |
| DAD B          | 1     | 10          | 10     | HL ← HL + BC; C flag only |
| DAD D          | 1     | 10          | 10     | Two read ports: HL on A, BC/DE/SP on B |
| DAD H          | 1     | 10          | 10     | |
| DAD SP         | 1     | 10          | 10     | |

**Assessment: identical. DAD uses both read ports simultaneously — clean.**
**INX/DCX set no flags at all — GAL suppresses all flag writes.**

---

## Group 7 — Direct Memory Load / Store

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| LDA nn         | 3     | 13          | 13     | A ← SRAM[nn]; immediate address to MAR |
| STA nn         | 3     | 13          | 13     | SRAM[nn] ← A |
| LHLD nn        | 3     | 16          | 16     | L ← SRAM[nn], H ← SRAM[nn+1]; two memory cycles |
| SHLD nn        | 3     | 16          | 16     | SRAM[nn] ← L, SRAM[nn+1] ← H |
| LDAX B         | 1     | 7           | 7      | A ← SRAM[BC] |
| LDAX D         | 1     | 7           | 7      | A ← SRAM[DE] |
| STAX B         | 1     | 7           | 7      | SRAM[BC] ← A |
| STAX D         | 1     | 7           | 7      | SRAM[DE] ← A |

**Assessment: identical. LHLD/SHLD need two consecutive SRAM cycles — extra T-states,**
**same solved pattern as Addy v2 LD/ST. MAR input mux selects between HL and immediate.**

---

## Group 8 — Stack Operations

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| PUSH B         | 1     | 11          | 11     | SP−−, SRAM[SP] ← B; SP−−, SRAM[SP] ← C |
| PUSH D         | 1     | 11          | 11     | |
| PUSH H         | 1     | 11          | 11     | |
| PUSH PSW       | 1     | 11          | 11     | Push A and F |
| POP B          | 1     | 10          | 10     | C ← SRAM[SP], SP++; B ← SRAM[SP], SP++ |
| POP D          | 1     | 10          | 10     | |
| POP H          | 1     | 10          | 10     | |
| POP PSW        | 1     | 10          | 10     | Pop F then A |
| SPHL           | 1     | 5           | 5      | SP ← HL |
| XTHL           | 1     | 18          | 18     | H↔SRAM[SP+1], L↔SRAM[SP] — two read-modify-write cycles |

**Assessment: identical. SP is a register slot; PUSH/POP are two sequential**
**byte-wide memory cycles with SP auto-increment/decrement between them.**

---

## Group 9 — Control Transfer (Jumps)

| Mnemonic       | Bytes | 8080 cycles | Ours       | Notes |
|----------------|-------|-------------|------------|-------|
| JMP nn         | 3     | 10          | 10         | PC ← nn |
| JZ nn          | 3     | 10          | 10         | Z=1 |
| JNZ nn         | 3     | 10          | 10         | Z=0 |
| JC nn          | 3     | 10          | 10         | C=1 |
| JNC nn         | 3     | 10          | 10         | C=0 |
| JP nn          | 3     | 10          | 10         | S=0 |
| JM nn          | 3     | 10          | 10         | S=1 |
| JPE nn         | 3     | 10          | 10         | P=1 — needs parity hardware |
| JPO nn         | 3     | 10          | 10         | P=0 — needs parity hardware |
| PCHL           | 1     | 5           | 5          | PC ← HL |

**Assessment: identical. PC is a register slot — conditional write based on flags.**
**Parity jumps need '280 parity generator (or equivalent) — see flag notes.**

---

## Group 10 — Subroutine Call / Return

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| CALL nn        | 3     | 17          | 17     | PUSH PC, PC ← nn |
| CZ nn          | 3     | 11/17       | 11/17  | Conditional: 11 if not taken, 17 if taken |
| CNZ nn         | 3     | 11/17       | 11/17  | |
| CC nn          | 3     | 11/17       | 11/17  | |
| CNC nn         | 3     | 11/17       | 11/17  | |
| CP nn          | 3     | 11/17       | 11/17  | |
| CM nn          | 3     | 11/17       | 11/17  | |
| CPE nn         | 3     | 11/17       | 11/17  | Parity |
| CPO nn         | 3     | 11/17       | 11/17  | Parity |
| RET            | 1     | 10          | 10     | PC ← SRAM[SP], SP += 2 |
| RZ             | 1     | 5/10        | 5/10   | Conditional return |
| RNZ            | 1     | 5/10        | 5/10   | |
| RC             | 1     | 5/10        | 5/10   | |
| RNC            | 1     | 5/10        | 5/10   | |
| RP             | 1     | 5/10        | 5/10   | |
| RM             | 1     | 5/10        | 5/10   | |
| RPE            | 1     | 5/10        | 5/10   | Parity |
| RPO            | 1     | 5/10        | 5/10   | Parity |
| RST n          | 1     | 11          | 11     | PUSH PC, PC ← 0x00/08/10/.../38 |

**Assessment: identical. CALL = PUSH PC then load nn. Conditional variants**
**skip the PUSH/jump if condition false — same flag evaluation as conditional jumps.**

---

## Group 11 — Exchange

| Mnemonic       | Bytes | 8080 cycles | Ours     | Notes |
|----------------|-------|-------------|----------|-------|
| XCHG           | 1     | 5           | 5 or *   | DE ↔ HL — address remap trick: 5 cycles. Multi-write path: costs extra cycles |
| XTHL           | 1     | 18          | 18       | Already in stack group above |
| EXX            | 1     | 4 (Z80)     | 4        | Toggle bank-select FF — zero register writes |
| EX AF, AF'     | 1     | 4 (Z80)     | 4        | Toggle AF bank-select bit |

**XCHG note: the address-remap trick (one FF that swaps DE/HL address decoding)**
**achieves this in 5 cycles with zero register file writes. Elegant and cheap.**

---

## Group 12 — Rotate and Shift (Accumulator)

| Mnemonic       | Bytes | 8080 cycles | Ours     | Notes |
|----------------|-------|-------------|----------|-------|
| RLC            | 1     | 4           | 4        | A rotate left, bit 7 → C and bit 0 |
| RRC            | 1     | 4           | 4        | A rotate right, bit 0 → C and bit 7 |
| RAL            | 1     | 4           | 4        | A rotate left through carry |
| RAR            | 1     | 4           | 4        | A rotate right through carry |

**Assessment: identical cycles. The '181 does NOT do shifts/rotates — this needs**
**a dedicated barrel shifter or a small rotate network (4 gates per bit for 1-bit**
**rotate). One '194 universal shift register per 4 bits = 2× '194 for 8-bit A.**
**Or: handle in GAL + a few gates. Small but real extra hardware.**

---

## Group 13 — Accumulator / Flag Specials

| Mnemonic       | Bytes | 8080 cycles | Ours     | Notes |
|----------------|-------|-------------|----------|-------|
| CMA            | 1     | 4           | 4        | A ← NOT A; '181 has NOT natively |
| CMC            | 1     | 4           | 4        | C ← NOT C; flag FF toggle |
| STC            | 1     | 4           | 4        | C ← 1; flag FF set |
| DAA            | 1     | 4           | *        | BCD adjust — see below |

**DAA note: the 8080 DAA is the single hardest instruction to implement.**
**It requires AC (auxiliary carry / half-carry) from the previous operation**
**and corrects A for BCD arithmetic. Needs: AC flag storage, a comparator,**
**a correction adder (+6 to low nibble and/or high nibble). Achievable but**
**non-trivial extra hardware. Omit for a first pass; mark as NOP or trap.**

---

## Group 14 — I/O

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| IN port        | 2     | 10          | 10     | A ← input port n |
| OUT port       | 2     | 10          | 10     | output port n ← A |

**Assessment: identical. Already in Addy lineage.**

---

## Group 15 — Control

| Mnemonic       | Bytes | 8080 cycles | Ours   | Notes |
|----------------|-------|-------------|--------|-------|
| NOP            | 1     | 4           | 4      | |
| HLT            | 1     | 7           | 7      | Assert /HALTREQ — clock module handles it |
| DI             | 1     | 4           | 4      | IFF ← 0 (interrupt enable FF) |
| EI             | 1     | 4           | 4      | IFF ← 1 |

---

## Hardware Requirements Summary

| Feature          | Status        | What's needed |
|------------------|---------------|---------------|
| 8-bit ALU ops    | ✅ Easy        | '181 function select |
| 16-bit pair ops  | ✅ Easy        | Two read ports + 16-bit '181 |
| Byte-merge write | ✅ Easy        | One '157 mux on write path |
| Flags S, Z, C, N | ✅ Easy       | '181 outputs + latch |
| Auxiliary carry AC | ⚠️ Extra     | Tap '181 internal carry between bits 3–4 |
| Parity flag P    | ⚠️ Extra      | 1× '280 parity generator on result bus |
| Rotates          | ⚠️ Extra      | 2× '194 shift register or gate network |
| XCHG remap trick | ✅ Easy       | One FF in decoder |
| Shadow regs EXX  | ✅ Trivial    | One FF on ADDR[3] |
| Stack PUSH/POP   | ✅ Moderate   | SP slot + sequential SRAM cycles |
| DAA              | ❌ Hard       | AC flag + correction logic — defer |

---

## What's Free Compared to the Real 8080

- **EXX / EX AF,AF'** — the real Z80 has two complete register arrays.
  Yours uses address-space banking. Cheaper and cleaner.
- **DAD** — two read ports make this single-cycle ALU work.
  The real 8080 uses an internal temp register and two passes.
- **CALL/RET** — PC being a plain register slot simplifies the push/pop of return address.

