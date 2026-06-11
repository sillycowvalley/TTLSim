# Thumby ISA v2 — The Liberated Encoding

**PROPOSED — NOT RATIFIED.** Supersedes the Thumb-shaped v1 draft.

## Design thesis

v1 inherited Thumb's format map, and with it Thumb's central concession: Thumb is a
compression codec for ARM's 32-bit ISA, contorted to fit, and the first thing it threw
overboard was ARM's signature feature — the barrel shifter as a free modifier on every
data-processing instruction. v2 abandons Thumb fidelity entirely. ARM remains the
*motivation*; the encoding is designed clean-sheet around the actual hardware:

- a 74181 ALU (4 slices + '182 lookahead) whose 16 logic / 16 arithmetic rows are the
  native operation menu,
- a barrel shifter wired permanently **in series on the B operand path**, in front of
  the '181, on every instruction,
- a 16-bit word and 3-bit register fields.

The result: **every ALU instruction carries an inline barrel shift on its source
operand** — the feature Thumb gave up — at zero datapath cost, because the series
shifter was already there. The op field selects a curated *(shifter type, '181 row,
carry source, flag mask)* tuple expanded by a decode GAL: Blinky's curated-op
philosophy transplanted to a register machine, same GAL discipline, same BlinkyJED
toolchain.

## Machine summary

- 16-bit Von Neumann, word-addressable, load/store. Memory-mapped I/O; no IN/OUT.
- 8 general registers r0–r7. Conventions (software, not hardware): r6 = SP, r7 = LR.
- PC is dedicated counter hardware, not in the register file. `PC+1` denotes the
  pre-incremented counter value (address of the next instruction) — available without
  an adder; the same trick Blinky's CALL uses.
- Flags N Z C V. Conditional branches are the only consumers; no predication.
- Control: fixed short sequencer — FETCH → EXECUTE, with a MEM state appended for
  class `10`. No microcode.

## Datapath shape

```
            register file (8 × 16, 2R 1W: '670 ×2 banks)
                 A port (Rd)        B port (Rm)
                     |                  |
                     |          +-------+--------+
                     |          |  B-source mux  |  ← imm8 (class 01)
                     |          +-------+--------+  ← A-port tap (SHF only)
                     |                  |
                     |          +-------+--------+
                     |          | BARREL SHIFTER |  ← amount mux: imm4 / B-port[3:0] (SHF) / 0
                     |          | LSL LSR ASR ROR|  ← type from op decode
                     |          +-------+--------+ → last-bit-out → C-source mux
                     |                  |
              +------+------------------+------+
              |     74181 ×4  +  74182         |  ← S3–S0, M, Cn from op decode
              +------------------+-------------+ → Cn+4 → C-source mux
                                 |               → sign bits → V logic
                          write-back (WE from op decode)
```

A = Rd (destructive accumulator side). B = shifter output. One write port, one result
path, no output mux — the shifter's output *is* what the '181 sees.

## Format map — 2-bit top-level class

| Bits 15:14 | Class | Contents |
|---|---|---|
| `00` | ALU | op4, inline-shifted register operand |
| `01` | Immediate | op3 + Rd + imm8 |
| `10` | Memory | LDR/STR, immediate or scaled-register offset |
| `11` | Control | branches, BL, JR/JALR, PUSH/POP earmark |

---

## Class 00 — ALU (the liberated format)

```
 15 14 | 13 12 11 10 | 9  8  7  6 | 5  4  3 | 2  1  0
  0  0 |     op4     |    imm4    |    Rm   |    Rd
```

General form: **Rd = Rd ⊙ shift(Rm, #imm4)**. The inline shift type is **LSL** for all
two-operand ops; the four MOVs select their own type (they *are* the shift
instructions — dedicated shift opcodes no longer exist). MOV and MVN are
non-destructive (Rd and Rm independent), so "shift into a temp" is free.

### op4 table with GAL-ready decode columns

'181 columns use the active-HIGH convention of the simulator's `Hc181` model
(master doc §"Full 74181 Function Table"). Cn sources: **H** = no inject, **L** = +1
inject, **/C** = inject the C flag — the same three-way mux as Blinky's ALU_Cn.

| op4 | Mnemonic | Operation | Shifter | '181 S3–S0 | M | Cn | WE | N Z | C ← | V ← |
|---|---|---|---|---|---|---|---|---|---|---|
| 0000 | `MOV.LSL Rd, Rm, #i` | Rd = Rm << i | LSL i | 1010 (F=B) | H | — | ✓ | ✓ | shifter* | — |
| 0001 | `MOV.LSR Rd, Rm, #i` | Rd = Rm >> i, zero fill | LSR i | 1010 | H | — | ✓ | ✓ | shifter* | — |
| 0010 | `MOV.ASR Rd, Rm, #i` | Rd = Rm >> i, sign fill | ASR i | 1010 | H | — | ✓ | ✓ | shifter* | — |
| 0011 | `MOV.ROR Rd, Rm, #i` | Rd = Rm ror i | ROR i | 1010 | H | — | ✓ | ✓ | shifter* | — |
| 0100 | `ADD Rd, Rm, LSL #i` | Rd = Rd + (Rm<<i) | LSL i | 1001 (A plus B) | L(arith) | H | ✓ | ✓ | adder | ✓ |
| 0101 | `ADC Rd, Rm, LSL #i` | Rd = Rd + (Rm<<i) + C | LSL i | 1001 | L | /C | ✓ | ✓ | adder | ✓ |
| 0110 | `SUB Rd, Rm, LSL #i` | Rd = Rd − (Rm<<i) | LSL i | 0110 (A minus B minus 1) | L | L | ✓ | ✓ | adder | ✓ |
| 0111 | `SBC Rd, Rm, LSL #i` | Rd = Rd − (Rm<<i) − ¬C | LSL i | 0110 | L | /C | ✓ | ✓ | adder | ✓ |
| 1000 | `AND Rd, Rm, LSL #i` | Rd = Rd ∧ (Rm<<i) | LSL i | 1011 (A·B) | H | — | ✓ | ✓ | — | — |
| 1001 | `ORR Rd, Rm, LSL #i` | Rd = Rd ∨ (Rm<<i) | LSL i | 1110 (A+B) | H | — | ✓ | ✓ | — | — |
| 1010 | `EOR Rd, Rm, LSL #i` | Rd = Rd ⊕ (Rm<<i) | LSL i | 0110 (A⊕B) | H | — | ✓ | ✓ | — | — |
| 1011 | `BIC Rd, Rm, LSL #i` | Rd = Rd ∧ ¬(Rm<<i) | LSL i | 0111 (A·/B) | H | — | ✓ | ✓ | — | — |
| 1100 | `MVN Rd, Rm, LSL #i` | Rd = ¬(Rm<<i) | LSL i | 0101 (/B) | H | — | ✓ | ✓ | — | — |
| 1101 | `CMP Rd, Rm, LSL #i` | Rd − (Rm<<i), discarded | LSL i | 0110 | L | L | ✗ | ✓ | adder | ✓ |
| 1110 | `TST Rd, Rm, LSL #i` | Rd ∧ (Rm<<i), discarded | LSL i | 1011 | H | — | ✗ | ✓ | — | — |
| 1111 | `SHF.tt Rd, Rm` | Rd = shift_tt(Rd, Rm[3:0]) | tt, amt=Rm[3:0] | 1010 | H | — | ✓ | ✓ | shifter* | — |

\* shifter C: last bit shifted out; **C unchanged when the effective amount is 0**
(gate the C latch on amount ≠ 0 — dynamic for SHF).

Notes on the table:

- **SUB/EOR share '181 select 0110, ADD/ADC share 1001** — the same code-sharing
  Blinky exploits; M and Cn carry the differences.
- **The '181 logic-mode carry trap** (Cn+4 held HIGH when M = H, per the master doc)
  is neutralised by the flag mask: logic ops simply never write C. No instruction
  latches C from the '181 in logic mode.
- **'181 select lines may be left unqualified by class** (Blinky GAL1 style): off
  class 00, WE and FLAG latching are suppressed, so S/M/Cn are don't-care and the
  decode equations stay small. Exception: the MEM state drives the ADD row for
  address generation (see class 10).
- **SHF (op4 1111) is the variable-amount shift**, destructive: the value is Rd, the
  amount is Rm[3:0] (mod 16 — x86 behaviour, the steering wires are 4 bits). The
  shift *type* tt comes from imm4[3:2]; imm4[1:0] reserved-zero. Datapath cost, stated
  honestly: one extra input on the shifter data mux (A-port tap) and one on the
  amount mux (B-port low nibble). If that cost offends, SHF degrades gracefully to
  "reserved" and variable shifts become short loops.
- **Omitted:** CMN (synthesize when needed), RSB — **the '181 has no B-minus-A row**
  (the arithmetic column offers A−B−1 but not the reverse; ARM had RSB only because
  its ALU was custom). With the shifter on B, you get exactly one subtract
  orientation. ×(2ⁿ−1) therefore costs two instructions: `MOV.LSL temp, x, #n` then
  `SUB temp, x`.

## Class 01 — Immediate operations

```
 15 14 | 13 12 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  0  1 |   op3    |    Rd    |          imm8
```

The immediate enters the B path zero-extended, shifter at amount 0.

| op3 | Mnemonic | Operation | Flags |
|---|---|---|---|
| 000 | `MOV Rd, #imm8` | Rd = imm8 | N Z |
| 001 | `CMP Rd, #imm8` | Rd − imm8, discarded | N Z C V |
| 010 | `ADD Rd, #imm8` | Rd = Rd + imm8 | N Z C V |
| 011 | `SUB Rd, #imm8` | Rd = Rd − imm8 | N Z C V |
| 100 | `LDR Rd, [PC, #imm8]` | Rd = mem[PC+1 + imm8] | — |
| 101 | `ADR Rd, #imm8` | Rd = PC+1 + imm8 | — (proposed) |
| 110 | — | **Reserved** (candidate: `ORH Rd, #imm8` — Rd ∨= imm8<<8, two-instruction 16-bit constants without pool traffic) | |
| 111 | — | **Reserved** | |

- imm8 covers 0–255; anything wider is the literal pool (op3 100, the signature Von
  Neumann instruction: pools interleaved with code, fetched over the same bus, base =
  next-instruction address, forward reach 256 words) or the ORH candidate.
- ADR exists for jump tables and pool-free address formation; payload for `JR`.

## Class 10 — Memory

Two forms, selected by bit 13. Address arithmetic uses the main '181 (ADD row, Cn = H)
during the MEM state; the scaled form uses the barrel shifter on the index — the
shifter is in the address path because it is in *every* path.

**Immediate offset** (bit 13 = 0):

```
 15 14 | 13 | 12 | 11 10  9  8  7  6 | 5  4  3 | 2  1  0
  1  0 |  0 | L  |        imm6       |    Rb   |    Rd
```

| L | Mnemonic | Operation |
|---|---|---|
| 0 | `STR Rd, [Rb, #imm6]` | mem[Rb + imm6] = Rd |
| 1 | `LDR Rd, [Rb, #imm6]` | Rd = mem[Rb + imm6] |

imm6 is a word offset, 0–63 — double v1's struct-field reach (the bit came from
dropping Thumb's byte flag).

**Scaled register offset** (bit 13 = 1):

```
 15 14 | 13 | 12 | 11 | 10  9 | 8  7  6 | 5  4  3 | 2  1  0
  1  0 |  1 | L  | 0  |  ss   |   Ro    |    Rb   |    Rd
```

| L | Mnemonic | Operation |
|---|---|---|
| 0 | `STR Rd, [Rb, Ro, LSL #ss]` | mem[Rb + (Ro<<ss)] = Rd |
| 1 | `LDR Rd, [Rb, Ro, LSL #ss]` | Rd = mem[Rb + (Ro<<ss)] |

ss = 0–3. This restores ARM's fourth modifier seat (the scaled index) — free, since
the address add already flows through the series shifter. Bit 11 reserved-zero.

- Flags untouched by all memory ops. Word-only; byte addressing remains
  never-or-later, and reserved bits are where it would land.

## Class 11 — Control

**Conditional branch** (`1100`):

```
 15 14 13 12 | 11 10  9  8 | 7  6  5  4  3  2  1  0
  1  1  0  0 |    cond     |     offset8 (signed)
```

`B<cond>` — if true, PC = PC+1 + offset8. Offsets in **words** (no scaling — word
addressing pays off), reach ±128.

| cond | Test | cond | Test | cond | Test | cond | Test |
|---|---|---|---|---|---|---|---|
| 0000 EQ | Z | 0100 MI | N | 1000 HI | C ∧ ¬Z | 1100 GT | ¬Z ∧ N=V |
| 0001 NE | ¬Z | 0101 PL | ¬N | 1001 LS | ¬C ∨ Z | 1101 LE | Z ∨ N≠V |
| 0010 CS | C | 0110 VS | V | 1010 GE | N=V | 1110 | **Reserved** |
| 0011 CC | ¬C | 0111 VC | ¬V | 1011 LT | N≠V | 1111 | **Reserved** (SWI slot) |

One GAL: NZCV in, "taken" out. The signed rows (GE/LT/GT/LE) are where V earns its
gates.

**Unconditional branch / branch-and-link** (`1101`):

```
 15 14 13 12 | 11 | 10  9  8  7  6  5  4  3  2  1  0
  1  1  0  1 | L  |        offset11 (signed)
```

L = 0: `B label` — PC = PC+1 + offset11. L = 1: `BL label` — r7 = PC+1, then branch.
Reach ±1024 words. Long calls: `ADR`/`MOV` + `JALR`.

**Register branches** (`1110`):

```
 15 14 13 12 | 11 10  9 | 8  7  6 | 5  4  3 | 2  1  0
  1  1  1  0 |   sub3   | 0  0  0 |    Rs   | 0  0  0
```

| sub3 | Mnemonic | Operation |
|---|---|---|
| 000 | `JR Rs` | PC = Rs (`RET` = alias for `JR r7`) |
| 001 | `JALR Rs` | r7 = PC+1, PC = Rs — long/computed calls |
| 010–111 | — | **Reserved** |

**Class `1111`: Reserved**, earmarked for PUSH/POP register-list — the open decision:

```
 15 14 13 12 | 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  1  1  1  1 | L  | reserved |       rlist8 (earmark)
```

The one instruction that would make the sequencer iterate over data ('148 priority
encoder walking the mask, one memory cycle per set bit). Embrace as party trick or
keep deferred; the slot waits either way.

---

## Shifter contract (consolidated)

- **Position:** in series on the B operand path, every instruction, no bypass.
- **Amount source mux:** imm4 (class 00, ops 0000–1110) / B-port[3:0] (SHF) /
  hardwired 0 (classes 01, 11, and the imm-offset memory form) / ss zero-extended
  (scaled memory form).
- **Type:** from op decode — MOVs and SHF select; all other class-00 ops hardwired
  LSL; memory scaled form hardwired LSL.
- **Shift-by-zero is identity, C unchanged** — Thumb's "LSR #0 means #32" trick
  rejected (shift-by-16 needs a kill path the mux network lacks). MOV Rd, Rm is
  MOV.LSL #0.
- **Variable amounts are mod 16** (x86 behaviour, diverges from ARM): steering is 4
  wires, full stop. Documented loudly.
- **Fill line:** 0 (LSL/LSR), bit 15 of the pre-shift operand (ASR), wrapped data
  (ROR) — decoded once from the 2-bit type, fanned to the mux stages.
- **ROL doctrine:** no ROL anywhere; `ROL #n` assembles as `MOV.ROR #(16−n)`;
  register-amount ROL = negate then SHF.ROR (mod-16 wrap makes the negate
  sufficient). **Trap:** result-equivalent, flag-divergent — synthesized ROL leaves C
  = bit out the bottom, not the top.

## Flag rules (consolidated)

- **N, Z** — written by every class-00 op and the class-01 arithmetic/compare ops,
  from the 16-bit result. Never by memory or control instructions.
- **C** — three sources behind one mux (Blinky C_SRC, third input added):
  **adder** Cn+4 (active-HIGH: on subtract C=1 means no borrow) for
  ADD/ADC/SUB/SBC/CMP and their imm8 forms; **shifter** last-bit-out for MOV.x/SHF
  with amount ≠ 0; **hold** otherwise (all logic ops, amount-0 shifts, everything
  else).
- **V** — computed outside the '181 from the sign bits of A, post-shift B, and F
  (same-signs-in/different-sign-out for add, mirrored for subtract). Written only
  where the C column says "adder". Note the operand for the V rule is the
  **post-shifter** B value.
- **CMP/TST** write flags with the register-file write suppressed — Blinky-TST
  discipline: FLAG latching and WE are independent controls.

## Decode architecture

Mirrors Blinky's: instruction bits in, control vector out, GALs (ATF22V10 class for
the wide op4 expansion) compiled by BlinkyJED. The op4 → tuple mapping is the table
above, column-for-column: SHTYPE(2), S3–S0, M, Cn-select(2), WE, FLAG mask(3:
NZ / C-source / V). The class field gates WE and flag latching exactly as Blinky's
FLAG_WE guard does, letting the S/M lines stay unqualified. The condition evaluator
is its own small GAL (NZCV + cond4 → taken). Sequencer: 2 flip-flops + one GAL
(FETCH, EXEC, MEM; PUSH/POP would add the iterating state).

## What v2 gave up vs v1 (the honest ledger)

- The three-operand ADD/SUB format is gone — everything is destructive except
  MOV/MVN. The non-destructive MOVs absorb most of the pain (positioning a value and
  shifting it are now the same instruction).
- RSB never existed in the '181 and now has no slot to fake it in.
- Thumb's format map is no longer a crib; this document is the only map.

## What v2 gained

- The inline barrel shift on **all twelve** two-operand ALU ops — ARM's signature
  feature, the thing v1 mourned.
- The scaled-index addressing mode — ARM's fourth modifier seat, recovered free.
- imm6 memory offsets (was imm5), a clean 16-row op space with no escape-format
  contortions, JALR, and a coherent 2-bit class map a front panel can decode by eye.

## Idiom gallery (now with single-instruction entries)

| Task | v2 code |
|---|---|
| ×5 | `ADD r0, r0, LSL #2` |
| ×10 | `ADD r0, r0, LSL #2` ; `MOV.LSL r0, r0, #1` |
| ×7 | `MOV.LSL r1, r0, #3` ; `SUB r1, r0` |
| Absolute value | `MOV.ASR r1, r0, #15` ; `EOR r0, r1` ; `SUB r0, r1` |
| Signed ÷2ⁿ, round to zero | ASR-mask, `MOV.LSR` by #(16−n), `ADD`, `MOV.ASR` #n |
| Extract bits [h:l] | `MOV.LSL Rd, Rm, #(15−h)` ; `MOV.LSR Rd, Rd, #(15−h+l)` |
| Sign-extend low byte | `MOV.LSL` #8 ; `MOV.ASR` #8 |
| Byte swap | `MOV.ROR Rd, Rm, #8` |
| Test bit b | `MOV.LSL r1, r0, #(15−b)` ; `BMI` (or #(16−b) ; `BCS`) |
| Odd/even | `MOV.LSR r1, r0, #1` ; `BCS` |
| Bitfield insert | mask with `BIC Rd, Rm, LSL #l` ; `ORR Rd, Rs, LSL #l` |
| Array index (word table) | `LDR Rd, [Rb, Ro, LSL #0]` — or #1–3 for multi-word records |
| Multi-word left shift | `ADD lo, lo` ; `ADC hi, hi` — the adder is the chained shifter |
| Galois LFSR step | `MOV.LSR r0, r0, #1` ; `BCC skip` ; `EOR r0, taps` |
