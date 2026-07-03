# Thumby ISA — The Liberated Encoding

## Design thesis

Thumb is a compression codec for ARM's 32-bit ISA, contorted to fit, and the first
thing it threw overboard was ARM's signature feature — the barrel shifter as a free
modifier on every data-processing instruction. This encoding abandons Thumb fidelity
entirely. ARM remains the *motivation*; the encoding is designed clean-sheet around
the actual hardware:

- a 74181 ALU (4 slices, with 74182 lookahead carry) whose 16 logic / 16
  arithmetic rows are the native operation menu,
- a barrel shifter wired permanently **in series on the B operand path**, in front of
  the '181, on every instruction,
- a 16-bit word and 3-bit register fields.

The result: **every ALU instruction carries an inline barrel shift on its source
operand** — the feature Thumb gave up — at zero datapath cost, because the series
shifter was already there. The op field selects a curated *(shifter type, '181 row,
carry source, flag mask)* tuple expanded by a decode GAL.

## Machine summary

- 16-bit Von Neumann, word-addressable, load/store. Memory-mapped I/O; no IN/OUT.
- 8 general registers r0–r7. Conventions (software, not hardware): r6 = SP, r7 = LR.
- PC is dedicated counter hardware, not in the register file. `PC+1` denotes the
  pre-incremented counter value (address of the next instruction) — available without
  an adder.
- Flags N Z C V. Consumed only by conditional branches and the carry-in of ADC/SBC;
  no predication.
- Control: fixed short sequencer — FETCH → EXECUTE, with a MEM state appended for
  memory access: class `10`, and the class `01` PC-relative literal load. No
  microcode.
- Reset: PC = 0; registers and flags power up undefined — startup code must
  establish them.

## Datapath shape

```
            register file (8 × 16, 2R 1W: '670 ×2 banks)
                A port                 B port
             (addr: Rd)        (addr: Rm / bits 10:8 for JA-JAL)
                    |                  |
            +-------+-------+  +-------+--------+
            | A-source mux  |  |  B-source mux  |  ← imm8 / offset8 (class 01, branches)
            | A port / PC+1 |  +-------+--------+  ← A-port tap (SHF only)
            +-------+-------+          |
                    |          +-------+--------+     amount mux: imm4 (class 00) /
                    |          | BARREL SHIFTER |  ←  B-port[3:0] (SHF) / 8 (ORH,
                    |          | LSL LSR ASR ROR|     JA/JAL) / ss (scaled mem) / 0
                    |          +-------+--------+  → last-bit-out → C-source mux
                    |                  |           ← type from op decode
             +------+------------------+------+
             |  74181 ×4 + 74182 lookahead    |  ← S3–S0, M, Cn from op decode
             +------------------+-------------+ → Cn+4 → C-source mux
                                |               → sign bits → V logic
                +---------------+----------------+
                |                                |
      write-back (WE from decode)     PC load: hi ← F[15:8],
                                      lo ← F[7:0], or IR[7:0] for JA/JAL
```

A = Rd (destructive accumulator side). B = shifter output. One write port, one result
path, no output mux — the shifter's output *is* what the '181 sees.

The A-source mux selects the register-file A port (default) or **PC+1** — the latter
for the relative-branch target add (PC+1 + offset8), ADR, and the PC-relative
literal load, all of which run the '181 ADD row with the offset on B at amount 0.
Immediates entering the B-source mux are zero-extended (class 01) except branch
offset8, which is sign-extended.

## Carry architecture — the 74182

The 16-bit carry chain uses full lookahead across the four '181 slices via a
**74182** carry-lookahead generator (20 ns parts).

Polarity facts, anchored in the simulator's `Hc181` model (the behavioural ground
truth the cascade must satisfy):

- The '181's X pin is /P and Y is /G, both **active-low** (ADD: P asserted when
  A+B ≥ 15, G when A+B ≥ 16; SUB: A ≤ B and A < B respectively).
- The '181's Cn input injects +1 when **LOW**, while Cn+4 is active-**high** — the
  polarity opposition that forces an inverter between ripple-cascaded '181s.
- The '182 asserts its three carry outputs **LOW**, so CNX/CNY/CNZ wire directly to
  slices 1–3's Cn pins with no inverters. The external carry source (the Cn mux:
  H / L / /C) drives slice 0's Cn and the '182's Cn input in parallel.
- Group /G and /P outputs are available for a hypothetical further lookahead level
  (note the '182's quirk that group-G excludes P0).

**Simulation note:** TTLSim has no '182 behavioural model (part drawing only), so
the simulated machine substitutes a ripple cascade — three '04 sections between the
'181 slices — for the '182. The two are functionally identical, differing only in
carry-path delay. When validating the cascade, sweep ADD (S=1001) and SUB (S=0110)
with both carry-in states, diffing all sixteen F bits and the final carry; the SUB
rows are the ones to watch — P/G there encode comparisons, not sums.

## Format map — 2-bit top-level class

| Bits 15:14 | Class | Contents |
|---|---|---|
| `00` | ALU | op4, inline-shifted register operand |
| `01` | Immediate | op3 + Rd + imm8 |
| `10` | Memory | LDR/STR, immediate or scaled-register offset |
| `11` | Control | branches, JA/JAL, JR/JALR, CLC/SEC, PUSH/POP (reserved) |

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
inject, **/C** = inject the C flag — a three-way mux.

| op4 | Mnemonic | Operation | Shifter | '181 S3–S0 | M | Cn | WE | N Z | C ← | V ← |
|---|---|---|---|---|---|---|---|---|---|---|
| 0000 | `MOV.LSL Rd, Rm, #i` | Rd = Rm << i | LSL i | 1010 (F=B) | H | — | ✓ | ✓ | shifter* | — |
| 0001 | `MOV.LSR Rd, Rm, #i` | Rd = Rm >> i, zero fill | LSR i | 1010 | H | — | ✓ | ✓ | shifter* | — |
| 0010 | `MOV.ASR Rd, Rm, #i` | Rd = Rm >> i, sign fill | ASR i | 1010 | H | — | ✓ | ✓ | shifter* | — |
| 0011 | `MOV.ROR Rd, Rm, #i` | Rd = Rm ror i | ROR i | 1010 | H | — | ✓ | ✓ | shifter* | — |
| 0100 | `ADD Rd, Rm, LSL #i` | Rd = Rd + (Rm<<i) | LSL i | 1001 (A plus B) | L(arith) | H | ✓ | ✓ | adder | ✓ |
| 0101 | `ADC Rd, Rm, LSL #i` | Rd = Rd + (Rm<<i) + C | LSL i | 1001 | L | /C | ✓ | ✓ | adder | ✓ |
| 0110 | `ORN Rd, Rm, LSL #i` | Rd = Rd ∨ ¬(Rm<<i) | LSL i | 1101 (A+/B) | H | — | ✓ | ✓ | — | — |
| 0111 | `SBC Rd, Rm, LSL #i` | Rd = Rd − (Rm<<i) − ¬C | LSL i | 0110 (A minus B minus 1) | L | /C | ✓ | ✓ | adder | ✓ |
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

- **SBC/CMP/EOR share '181 select 0110, ADD/ADC share 1001** — M and Cn carry the
  differences.
- **There is no plain SUB.** Subtraction is `SEC` then `SBC` (see class 11) — C is
  active-high no-borrow, so `SEC` primes a true subtract, 6502 discipline. CMP is
  unaffected: its −1 inject is hardwired L, so comparisons never depend on prior
  flag state.
- **The '181 logic-mode carry trap** (Cn+4 held HIGH when M = H, per the master doc)
  is neutralised by the flag mask: logic ops simply never write C. No instruction
  latches C from the '181 in logic mode.
- **'181 select lines may be left unqualified by class**: off
  class 00, WE and FLAG latching are suppressed, so S/M/Cn are don't-care and the
  decode equations stay small. Exception: the MEM state drives the ADD row for
  address generation (see class 10).
- **SHF (op4 1111) is the variable-amount shift**, destructive: the value is Rd, the
  amount is Rm[3:0] (mod 16 — x86 behaviour, the steering wires are 4 bits). The
  shift *type* tt comes from imm4[3:2]; imm4[1:0] reserved-zero. Datapath cost, stated
  honestly: one extra input on the shifter data mux (A-port tap) and one on the
  amount mux (B-port low nibble).
- **Omitted:** CMN (synthesize when needed), RSB — **the '181 has no B-minus-A row**
  (the arithmetic column offers A−B−1 but not the reverse; ARM had RSB only because
  its ALU was custom). With the shifter on B, you get exactly one subtract
  orientation. ×(2ⁿ−1) therefore costs three instructions: `MOV.LSL temp, x, #n`,
  `SEC`, `SBC temp, x`.

## Class 01 — Immediate operations

```
 15 14 | 13 12 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  0  1 |   op3    |    Rd    |          imm8
```

The immediate enters the B path zero-extended, shifter at amount 0 — except ORH,
which uses amount 8, type LSL: the high-byte placement is the series shifter doing
the work, not a dedicated path.

| op3 | Mnemonic | Operation | Flags |
|---|---|---|---|
| 000 | `MOV Rd, #imm8` | Rd = imm8 | N Z |
| 001 | `CMP Rd, #imm8` | Rd − imm8, discarded | N Z C V |
| 010 | `ADD Rd, #imm8` | Rd = Rd + imm8 | N Z C V |
| 011 | `SBC Rd, #imm8` | Rd = Rd − imm8 − ¬C | N Z C V |
| 100 | `LDR Rd, [PC, #imm8]` | Rd = mem[PC+1 + imm8] | — |
| 101 | `ADR Rd, #imm8` | Rd = PC+1 + imm8 | — |
| 110 | `ORH Rd, #imm8` | Rd = Rd ∨ (imm8<<8) | N Z |
| 111 | `ADC Rd, #imm8` | Rd = Rd + imm8 + C | N Z C V |

- imm8 covers 0–255; any 16-bit constant is `MOV Rd, #lo8` + `ORH Rd, #hi8` — two
  instructions, no pool traffic — or the literal pool (op3 100, the signature Von
  Neumann instruction: pools interleaved with code, fetched over the same bus, base =
  next-instruction address, forward reach 256 words). The literal load is a memory
  access and takes the MEM state, exactly like class 10.
- Immediate subtraction is `SEC` + `SBC Rd, #imm8`, same discipline as class 00.
- `ADC Rd, #0` is the multi-precision carry ripple — no zero-holding register
  needed. ADD and ADC give the add side both carry disciplines; the subtract side
  is SBC-only by design.
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

imm6 is a word offset, 0–63 — generous struct-field reach (the bit came from
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
| 0010 CS | C | 0110 VS | V | 1010 GE | N=V | 1110 AL | always |
| 0011 CC | ¬C | 0111 VC | ¬V | 1011 LT | N≠V | 1111 | **Reserved** (SWI slot) |

One GAL: NZCV in, "taken" out. The signed rows (GE/LT/GT/LE) are where V earns its
gates. `B label` assembles as `BAL` — cond 1110 is the unconditional relative
branch, reach ±128 words; anything farther is `JA`.

**Absolute jump / jump-and-link** (`1101`):

```
 15 14 13 12 | 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  1  1  0  1 | L  |    Rs    |          imm8
```

L = 0: `JA Rs, #imm8` — PC = (Rs << 8) ∨ imm8. L = 1: `JAL Rs, #imm8` — r7 = PC+1,
then jump. Rs holds the target's **high byte**; the immediate supplies the low
byte. Reach: all 64K words, absolutely — `MOV rX, #hi8` then `JA rX, #lo8` lands
anywhere in two instructions. And because Rs carries the page, a cluster of calls
into one 256-word page pays its `MOV` once and one instruction per call after
that.

Datapath: Rs rides the B port through the series shifter at a hardwired LSL 8
('181 row F=B, the MOV row); PC loads its high byte from F[15:8] and its low byte
directly from instruction bits 7:0 — the ∨ is byte concatenation, so no combining
row is needed, just an 8-bit 2:1 mux on the PC's low-byte load path. Rs sits at
bits 10:8, so the B-port read address gains a 3-bit 2:1 mux (bits 5:3 / bits 10:8).

**Register branches / flag ops** (`1110`):

```
 15 14 13 12 | 11 10  9 | 8  7  6 | 5  4  3 | 2  1  0
  1  1  1  0 |   sub3   | 0  0  0 |    Rs   | 0  0  0
```

| sub3 | Mnemonic | Operation |
|---|---|---|
| 000 | `JR Rs` | PC = Rs (`RET` = alias for `JR r7`) |
| 001 | `JALR Rs` | r7 = PC+1, PC = Rs — long/computed calls |
| 010 | `CLC` | C = 0 (N Z V untouched; Rs field reserved-zero) |
| 011 | `SEC` | C = 1 (N Z V untouched; Rs field reserved-zero) |
| 100–111 | — | **Reserved** |

**Class `1111`: Reserved** for PUSH/POP register-list:

```
 15 14 13 12 | 11 | 10  9  8 | 7  6  5  4  3  2  1  0
  1  1  1  1 | L  | reserved |         rlist8
```

The one instruction that would make the sequencer iterate over data ('148 priority
encoder walking the mask, one memory cycle per set bit).

---

## Shifter contract (consolidated)

- **Position:** in series on the B operand path, every instruction, no bypass.
- **Amount source mux:** imm4 (class 00, ops 0000–1110) / B-port[3:0] (SHF) /
  hardwired 8 (ORH, JA/JAL) / hardwired 0 (rest of classes 01 and 11, and the
  imm-offset memory form) / ss zero-extended (scaled memory form).
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

- **N, Z** — written by every class-00 op and by MOV/CMP/ADD/ADC/SBC/ORH in
  class 01, from the 16-bit result. Never by memory instructions; never by control
  instructions (CLC/SEC write C only).
- **C** — five sources behind one mux:
  **adder** Cn+4 (active-HIGH: on subtract C=1 means no borrow) for
  ADD/ADC/SBC/CMP and their imm8 forms; **shifter** last-bit-out for MOV.x/SHF
  with amount ≠ 0; **constant 0 / constant 1** for CLC/SEC; **hold** otherwise
  (all logic ops including ORH, amount-0 shifts, everything else).
- **V** — computed outside the '181 from the sign bits of A, post-shift B, and F
  (same-signs-in/different-sign-out for add, mirrored for subtract). Written only
  where the C column says "adder". Note the operand for the V rule is the
  **post-shifter** B value.
- **CMP/TST** write flags with the register-file write suppressed: FLAG latching
  and WE are independent controls.

## Decode architecture

Instruction bits in, control vector out, GALs — all GAL22V10 (20 ns). The GAL
complement numbers two: the op4 decode and the condition evaluator — one part
number, one toolchain, all reprogrammable. The op4 → tuple mapping is the table
above, column-for-column: SHTYPE(2), S3–S0, M, Cn-select(2), WE, FLAG mask(3:
NZ / C-source / V). The class field gates WE and flag latching (a FLAG_WE guard),
letting the S/M lines stay unqualified. The condition evaluator
is its own small GAL (NZCV + cond4 → taken). Sequencer: 2 flip-flops + one GAL
(FETCH, EXEC, MEM).

## Deliberate omissions (the honest ledger)

- There is no three-operand arithmetic format — everything is destructive except
  MOV/MVN. The non-destructive MOVs absorb most of the pain (positioning a value and
  shifting it are now the same instruction).
- There is no plain SUB, register or immediate: subtraction is `SEC` + `SBC` (6502
  carry discipline). CMP is unaffected — its −1 inject is hardwired.
- RSB never existed in the '181 and has no slot to fake it in.
- Thumb's format map is not a crib; this document is the only map.

## Design highlights

- The inline barrel shift on **all twelve** two-operand ALU ops — ARM's signature
  feature, preserved rather than sacrificed.
- The scaled-index addressing mode — ARM's fourth modifier seat, recovered free.
- imm6 memory offsets, ORH for pool-free 16-bit constants, two-instruction absolute
  jumps and calls to anywhere in 64K (JA/JAL), a clean op space with no
  escape-format contortions, and a coherent 2-bit class map a front panel can
  decode by eye.

## Idiom gallery (now with single-instruction entries)

| Task | Code |
|---|---|
| ×5 | `ADD r0, r0, LSL #2` |
| ×10 | `ADD r0, r0, LSL #2` ; `MOV.LSL r0, r0, #1` |
| ×7 | `MOV.LSL r1, r0, #3` ; `SEC` ; `SBC r1, r0` |
| 16-bit constant | `MOV Rd, #lo8` ; `ORH Rd, #hi8` — no pool traffic |
| Far jump / call | `MOV rX, #hi8` ; `JA rX, #lo8` (or `JAL`) — anywhere in 64K |
| Same-page call cluster | `MOV rP, #hi8` once ; then `JAL rP, #lo8` per call |
| Absolute value | `MOV.ASR r1, r0, #15` ; `EOR r0, r1` ; `SEC` ; `SBC r0, r1` |
| Signed ÷2ⁿ, round to zero | ASR-mask, `MOV.LSR` by #(16−n), `ADD`, `MOV.ASR` #n |
| Extract bits [h:l] | `MOV.LSL Rd, Rm, #(15−h)` ; `MOV.LSR Rd, Rd, #(15−h+l)` |
| Sign-extend low byte | `MOV.LSL` #8 ; `MOV.ASR` #8 |
| Byte swap | `MOV.ROR Rd, Rm, #8` |
| Test bit b | `MOV.LSL r1, r0, #(15−b)` ; `BMI` (or #(16−b) ; `BCS`) |
| Odd/even | `MOV.LSR r1, r0, #1` ; `BCS` |
| Bitfield insert | mask with `BIC Rd, Rm, LSL #l` ; `ORR Rd, Rs, LSL #l` |
| Array index (word table) | `LDR Rd, [Rb, Ro, LSL #0]` — or #1–3 for multi-word records |
| Multi-word left shift | `ADD lo, lo` ; `ADC hi, hi` — the adder is the chained shifter |
| Add constant, multi-word | `ADD lo, #imm8` ; `ADC hi, #0` |
| Galois LFSR step | `MOV.LSR r0, r0, #1` ; `BCC skip` ; `EOR r0, taps` |
