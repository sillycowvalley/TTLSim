# Blinky ALU Rev 2 — Specification & Change Document

**Date:** 2026-06-13. **Applies to:** Mini Blinky and Blinky (the decode scheme is shared;
the mini simply populates the same rows). **GAL deliverables:** `GAL1_ALU.pld` rev 02,
`GAL2_STACKTOP.pld` rev 02, `GAL3_POINTERS.pld` rev 02, `GAL4_FLOW.pld` rev 03.

All rev-2 equations were brute-force verified against the control tables in this document
over all 256 IR values × both C-flag states (4,032 specified-bit checks, all passing), with
don't-cares honoured. Every output is at or under the 16V8's 8-product-term ceiling; the
worst are `TOS_M1`/`TOS_M0` at 7 terms.

**Binary compatibility:** ALU sub-op nibbles 0–7 and every other opcode are unchanged.
Existing programs, the worked examples in the master doc, and previously burned JEDs for
GALs whose equations carry no rev-2 change remain valid — but all four devices should be
re-burned together, since GAL2/GAL3 net semantics changed (see hardware deltas).

---

## 1. What rev 2 is

The Thumby ALU review identified four improvements that transplant cleanly onto Blinky:

1. **Per-flag C write enable** — `FLAG_WE` splits into `NZ_WE` (N and Z) and a new `C_WE`
   (C only). The C latch is enabled only by ops that define C, which retires the rev-1
   gotcha "XOR/NOT/TST latch C = 1 from the '181's held-high logic-mode Cn+4." C now
   *survives* logic ops — Thumby's "hold" carry source, for free.
2. **AND and OR sub-ops** — '181 rows 1011 and 1110, ordinary binary pops like ADD.
3. **CMP — non-destructive** — `( a b -- a b )`: '181 select 0110 arithmetic, Cn = L
   (SUB's exact carry treatment), flags latch, TOS and NOS hold, no stack RAM access,
   ΔDSP 0. The rev-1 ban applied to the *popping* form `( a b -- )`, a forbidden −2; the
   non-destructive form is TST's discipline applied to subtract and is fully legal under
   the stack rules.
3. (cont.) After `PUSH x` / `PUSH y`, CMP latches the flags of **y − x** (TOS − NOS, A = TOS,
   B = NOS): Z = equal, C = 1 means TOS ≥ NOS (no borrow), N = sign bit of the difference.
4. **ASR, ROL, ROR** — the master doc already earmarked this as "one GAL change plus a
   serial-fill mux feeding the '194." Rev 2 adds the mux (one '153) and the GAL rows.
   Rotates are **plain** rotates (wrap through the falling-out bit), not through-carry;
   if RCR/RCL are ever wanted, it is a one-wire change on the '153 input (C instead of
   Q0/Q7).

**Considered and rejected** (unchanged from the review): the **V flag** is parked — the
latch is nearly free (spare half-'74 plus a GAL3 macrocell), but signed branches need V as
a flag input on GAL4, which is an exact 18/18-pin fit; consuming V breaks the no-20V8
rule. The **GAL-182 lookahead** doesn't pay at two slices (one carry hop; the existing
inverter stands). The **barrel shifter** is architecturally Thumby's — Blinky's shifter
*is* the TOS '194 and the instruction word has no field for a shift amount.

---

## 2. Re-ratified ALU sub-op nibble map (rev 2)

| Nib | Op | Class | Nib | Op | Class |
|---|---|---|---|---|---|
| 0 | ADD | binary pop, arith | 8 | **AND** | binary pop, logic |
| 1 | ADC | binary pop, arith | 9 | **CMP** | flags-only, arith |
| 2 | SUB | binary pop, arith | A | *reserved (idle)* | |
| 3 | XOR | binary pop, logic | B | **ASR** | '194 right, sign fill |
| 4 | NOT | unary, logic | C | **OR** | binary pop, logic |
| 5 | TST | flags-only, logic | D | *reserved (idle)* | |
| 6 | SHL | '194 left, zero fill | E | **ROL** | '194 left, wrap |
| 7 | SHR | '194 right, zero fill | F | **ROR** | '194 right, wrap |

SYS nibbles 0–7 are untouched. The placements of the six new ops are **not aesthetic** —
they are forced by two constraints:

**(a) The serial-fill mux select is raw IR bits, costing zero GAL outputs.** The '194
ignores its serial inputs except while shifting, so the fill mux can be selected directly
by L3 (select B) and L2 (select A) with no decode and no gating:

| L3 L2 | Right fill (DSR) | Left fill (DSL) | Ops |
|---|---|---|---|
| 0 1 | 0 | 0 | SHL, SHR (logical) |
| 1 0 | Q7 (old bit 7) | — | ASR (sign) |
| 1 1 | Q0 (old bit 0) | Q7 (old bit 7) | ROR, ROL (rotate) |

This pins ASR into 10xx and ROL/ROR into 11xx.

**(b) `TOS_M1`/`TOS_M0` were already at the 16V8's 8-term ceiling in rev 1.** The chosen
slots make three cheap cubes exist: `!L1 & !L0` covers {0,4,8,C} = ADD, NOT, **AND, OR**
(the load group); `L2 & L1 & !L0` covers {6,E} = SHL, **ROL**; `L1 & L0` covers {3,7,B,F}
= SHR, **ASR, ROR** (XOR re-covered harmlessly). Both outputs land on 7 of 8 terms.

---

## 3. Control-signal changes

| Signal | Device/pin | Status | Behaviour |
|---|---|---|---|
| `NZ_WE` | GAL2 pin 14 | **renamed** (was `FLAG_WE`) | N and Z latch enable. Asserts on every live ALU nibble (0–9, B, C, E, F); low on reserved A/D and outside the ALU class. |
| `C_WE` | GAL3 pin 13 | **new** (was spare) | C latch enable. Asserts on ADD, ADC, SUB, SHL, SHR, **CMP, ASR, ROL, ROR** only. Logic ops leave C untouched. |
| `C_SRC` | GAL1 pin 14 | extended | 1 = '194 shifted-out bit on all **five** shifts; 0 = '181 Cn+4 on ADD/ADC/SUB/CMP. Now don't-care wherever `C_WE` = 0, which is what keeps it at 2 terms. |
| `ALU_CN` | GAL4 pin 17 | extended | Cn = L on SUB **and CMP**; = /C on ADC; = H otherwise. |
| `TOS_M1/M0` | GAL1 pins 12/13 | extended | Load adds AND/OR; left adds ROL; right adds ASR/ROR; CMP holds. |
| `NOS_LD`, `DSP_EN` | GAL2/GAL3 | extended | AND/OR join the binary-pop group (cube `L3 & !L1 & !L0`). CMP appears in **neither** — no stack motion. |
| `TOS_SRC`, `NOS_SRC`, `DSP_UD`, `RSP_*`, `RDIN_SEL`, `CLK_RUN`, `PCSEL*`, `IO_*` | — | unchanged | Existing cubes already cover or don't-care the new nibbles. |

**C semantics on the new ops:** ASR and ROR latch C ← old bit 0; ROL latches C ← old
bit 7 — the same "bit shifted out" rule as SHR/SHL, so the existing direction-keyed
shifted-out-bit selection needs no change.

---

## 4. Hardware deltas (board and sim schematic)

1. **One 74HC153** (dual 4:1 mux) on the '194 serial inputs. Selects: **B = IR L3,
   A = IR L2** (raw IR bits, no decode). Inputs — DSR section: in0 = GND, in1 = GND,
   in2 = Q7, in3 = Q0; DSL section: in0 = in1 = in2 = GND, in3 = Q7. At W = 8 (two
   cascaded '194s) the pins that matter are the cascade ends: the right-shift serial
   input is DSR of the **high** '194 (Q7 end) and the left-shift serial input is DSL of
   the **low** '194 (Q0 end); Q7/Q0 taps come from those same ends. The mini (W = 4,
   one '194) wires DSR/DSL directly.
2. **C flip-flop enable rewire:** the C latch's enable input moves from the old
   `FLAG_WE` net to the new `C_WE` net (GAL3 pin 13). The N and Z latches keep the GAL2
   pin-14 net, now named `NZ_WE`. Same gating arrangement as before — one net becomes
   two.
3. **GAL3 pin 13 is now an output.** Anything that assumed it was a spare (the JEDTester
   rig drives GAL3 pins 12/13 low via the Mega) must be updated — see §7.
4. No '181, '194, flag-LED, or carry-chain changes. The Cn/Cn+4 ripple inverter and the
   HCT-at-LS-boundaries rules are untouched.

---

## 5. Replacement sections for `Mini_Blinky_CPU.md`

The master docs are large and the changes are surgical, so this section provides complete
drop-in replacements for exactly the parts that change (per the whole-section-replacement
convention), rather than regenerating multi-thousand-line documents around unchanged
content.

### 5.1 Replace the "ALU Sub-Ops (opcode 1)" table and its trailing paragraphs

Replace the sub-op table and the two paragraphs after it (from the table through the
"Rotates (`ROL`/`ROR`) are absent…" sentence) with:

> | Sub-op | Nib | ΔDSP | Stack effect | '181 S3–S0 | Mode | Cn | N/Z | C |
> |---|---|---|---|---|---|---|---|---|
> | `ADD` | 0 | −1 | `( a b -- a+b )` | 1001 | arith | H | ✓ | ✓ Cn+4 |
> | `ADC` | 1 | −1 | `( a b -- a+b+C )` | 1001 | arith | /C | ✓ | ✓ Cn+4 |
> | `SUB` | 2 | −1 | `( a b -- b−a )` | 0110 | arith | L | ✓ | ✓ Cn+4 |
> | `XOR` | 3 | −1 | `( a b -- a⊕b )` | 0110 | logic | — | ✓ | — held |
> | `NOT` | 4 | 0 | `( a -- ¬a )` | 0000 | logic | — | ✓ | — held |
> | `TST` | 5 | 0 | `( a -- a )` | 1111 | logic | — | ✓ | — held |
> | `SHL` | 6 | 0 | `( a -- a«1 )` | — | — | — | ✓ | ✓ old bit 7 |
> | `SHR` | 7 | 0 | `( a -- a»1 )` | — | — | — | ✓ | ✓ old bit 0 |
> | `AND` | 8 | −1 | `( a b -- a∧b )` | 1011 | logic | — | ✓ | — held |
> | `CMP` | 9 | 0 | `( a b -- a b )` | 0110 | arith | L | ✓ | ✓ Cn+4 |
> | `ASR` | B | 0 | `( a -- a»1 )` sign | — | — | — | ✓ | ✓ old bit 0 |
> | `OR` | C | −1 | `( a b -- a∨b )` | 1110 | logic | — | ✓ | — held |
> | `ROL` | E | 0 | `( a -- rol a )` | — | — | — | ✓ | ✓ old bit 7 |
> | `ROR` | F | 0 | `( a -- ror a )` | — | — | — | ✓ | ✓ old bit 0 |
>
> Nibbles A and D are reserved and decode to the idle vector (`NZ_WE`, `C_WE`, `DSP_EN`,
> `NOS_LD` and the '194 mode all exclude them explicitly).
>
> **Direction:** A = TOS, B = NOS, so `SUB` and `CMP` compute TOS − NOS (C = 1 means no
> borrow, TOS ≥ NOS). **Per-flag write masking (rev 2):** N/Z latch on every live sub-op
> (`NZ_WE`); C latches only where the C column shows ✓ (`C_WE`). The logic ops XOR, NOT,
> TST, AND, OR therefore *hold* C — the rev-1 "logic ops latch C = 1" gotcha is gone, and
> C survives across them (e.g. a shifted-out bit can be tested after intervening masking).
> **`CMP`** is the non-destructive compare `( a b -- a b )`: SUB's '181 row and carry-in
> with the result discarded — TOS and NOS hold, no stack access, ΔDSP 0. The old popping
> CMP `( a b -- )` remains forbidden (−2). **Shifts:** SHL/SHR are logical (zero fill);
> ASR fills with old bit 7 (sign); ROL/ROR are plain rotates wrapping the falling-out
> bit. The fill is a '153 dual 4:1 mux on the '194 serial pins, selected by **raw IR bits
> L3/L2** — the '194 ignores its serial inputs except while shifting, so no decode or
> gating is spent. Rotate-through-carry, if ever wanted, is a one-wire change on that mux
> ('153 input C instead of Q0/Q7).
> **'181 code sharing:** SUB/XOR/CMP share 0110 (mode and write-mask differ); ADD/ADC
> share 1001 (Cn differs).

### 5.2 Replace the ALU-family entries in §6 (detailed instruction descriptions)

Replace the `XOR`, `NOT`, `TST`, `SHL`, `SHR` entries and append the new ops, so the ALU
family block reads (ADD/ADC/SUB entries unchanged):

> **XOR** — Logic mode (M=1), select 0110 — the other '181 mode. Shares its select with
> SUB; only the mode bit differs. N/Z latch; **C held** (rev 2, `C_WE` deasserted).
>
> **NOT** — Unary, select 0000 logic: one operand, no NOS read. ΔDSP = 0, NOS held. N/Z
> latch; C held.
>
> **TST** — Non-destructive flags-only test of TOS: '181 select 1111 logic (F = A = TOS),
> `NZ_WE` asserted, `TOS_MODE = hold`, no stack or memory access (ΔDSP = 0). N = top bit
> of TOS, Z = TOS is zero; C held. This is the row that proves the flag write is
> independent of the TOS write.
>
> **AND** — Binary pop `( a b -- a∧b )`, select 1011 logic. Result → TOS, NOS ← stack
> read, ΔDSP −1. N/Z latch; C held. The bit-mask workhorse.
>
> **OR** — Binary pop `( a b -- a∨b )`, select 1110 logic. Same control pattern as AND.
>
> **CMP** — Non-destructive compare `( a b -- a b )`: '181 select 0110 **arith** with
> Cn = L — exactly SUB — but the result is discarded: TOS holds, NOS holds, no stack
> access, ΔDSP 0. Flags of TOS − NOS latch (N/Z via `NZ_WE`, C via `C_WE`): Z = equal,
> C = 1 = TOS ≥ NOS. Replaces a SUB-and-restore dance when both operands are still
> needed; the *popping* CMP `( a b -- )` remains a forbidden −2.
>
> **SHL** — Left single-bit shift on the '194 — a datapath the '181 never drives.
> ΔDSP = 0; zero fill; C ← old top bit (`C_SRC = shf`); N/Z from the shifted result.
>
> **SHR** — Right single-bit logical shift; zero fill; C ← old bottom bit.
>
> **ASR** — Right single-bit **arithmetic** shift: the '194 serial input is fed old bit 7
> (sign) by the fill mux. C ← old bottom bit. Signed halving.
>
> **ROL / ROR** — Single-bit plain rotates: the falling-out bit re-enters through the
> fill mux (ROL: bit 7 → bit 0, C ← old bit 7; ROR: bit 0 → bit 7, C ← old bit 0). Not
> through-carry.

### 5.3 Replace the `FLAG_WE` and `C_SRC` rows/entries in §3 and §7

In the §3 dictionary table, replace the `FLAG_WE` row with two rows:

> | `NZ_WE` | ✓ / – | latch N and Z this cycle (every live ALU sub-op; reserved nibbles and non-ALU ops deassert it) |
> | `C_WE` | ✓ / – | latch C this cycle (ADD ADC SUB CMP SHL SHR ASR ROL ROR only — logic ops hold C) |

In the §7 prose, replace the `FLAG_WE` entry with:

> **NZ_WE / C_WE** — Per-flag latch enables (rev 2). `NZ_WE` asserts on every live ALU
> sub-op; `C_WE` only on the ops that define C — the arithmetic group, the five shifts,
> and CMP. Splitting the enables is what lets logic ops preserve C and what makes the
> reserved nibbles A/D truly inert. Both decode from instruction bits alone.

and extend the `C_SRC` entry's op list to "old top on SHL/ROL, old bottom on SHR/ASR/ROR."

### 5.4 GAL partition table (§ "GAL Partition — 4× 16V8")

Replace the table and the "two spare macrocells" sentence with:

> | GAL | Part | Outputs | Inputs |
> |---|---|---|---|
> | **1 — ALU / shift select** | 16V8 | `ALU_S0..S3`, `ALU_M`, `C_SRC`, `TOS_M0`, `TOS_M1` (8) | opcode + sub-op = 8 |
> | **2 — stack-top + D-mem RAM** | 16V8 | `TOS_SRC0..2`, `NOS_LD`, `NOS_SRC`, `NZ_WE`, `DMEM_CS`, `DMEM_WE` (8) | opcode + sub-op = 8 |
> | **3 — pointers + ret data mux + clk + C enable** | 16V8 | `DSP_EN`, `DSP_UD`, `RSP_EN`, `RSP_UD`, `RDIN_SEL`, `CLK_RUN`, `C_WE` (7) | opcode + sub-op = 8 |
> | **4 — control flow + I/O + Cn** | 16V8 | `PCSEL0`, `PCSEL1`, `ALU_Cn`, `IO_RD`, `IO_WR`, `IOADDR_SEL` (6) | opcode + sub-op + **N, Z, C** = 11 |
>
> GAL 3 keeps one spare macrocell (pin 12) and GAL 4 keeps two — the remaining headroom
> for an immediate-ALU family later. The '194 serial-fill mux consumes **no** GAL output:
> its '153 selects are raw IR bits L3/L2, don't-care except while shifting.

---

## 6. Replacement text for `Blinky_TTL_CPU.md` (Open Decisions)

**Replace the "Shifts — resolved" bullet with:**

> - **Shifts — resolved: single-bit on the '194 TOS, five forms.** The TOS register *is*
>   a '194 bidirectional shift register (2× at W = 8): SHL/SHR (logical, zero fill), ASR
>   (sign fill), and plain rotates ROL/ROR (wrap the falling-out bit) are all TOS shift
>   modes. The fill source is one '153 dual 4:1 mux on the '194 serial pins, selected by
>   raw IR bits L3/L2 — don't-care except while shifting, so it costs no decode. C is
>   sourced from the bit shifted out (old bit 7 leftward, old bit 0 rightward) rather
>   than the carry chain. Multi-bit shifts remain loops — the instruction word has no
>   field for a shift amount. Rotate-through-carry, if ever wanted, is a one-wire change
>   on the '153 inputs.

**Replace the "Flags" bullet's "All three are written by every ALU op…" sentences with:**

> - **Flags.** Three flags: N, Z, C, written **only** by the ALU class — stack, memory,
>   I/O, and control instructions leave the flags alone. Within the ALU class the write
>   is per-flag (rev 2): N/Z latch on every ALU op (`NZ_WE`); C latches only on the ops
>   that define it — ADD/ADC/SUB/CMP (carry/borrow) and the five shifts (shifted-out
>   bit) — via its own enable, `C_WE`. The logic ops (XOR, NOT, TST, AND, OR) preserve
>   C, so a carry or shifted-out bit survives intervening masking ops until its consuming
>   branch. (This retires the old "logic ops latch C = 1 from the held-high logic-mode
>   Cn+4" caveat.) Cost: ~1.5 packages as before; the C flip-flop's enable simply moves
>   to the `C_WE` net.

**In the "Conditional execution granularity?" bullet,** replace "Comparisons are done by
TST ('181 F = A, flags only, write suppressed — replaces the old two-operand CMP, whose
clean `( a b -- )` form is a forbidden −2)" with:

> Comparisons are done by TST (one-operand, flags of TOS) or **CMP** (two-operand,
> non-destructive `( a b -- a b )`: SUB's '181 row with the write suppressed and no stack
> motion, ΔDSP 0 — legal where the old *popping* CMP's `( a b -- )` was a forbidden −2)

**In the instruction-set table's ALU row,** extend the op list with AND, OR, CMP, ASR,
ROL, ROR and change the flags note to "N/Z on every ALU op; C only on
arithmetic/shift/CMP (logic ops preserve C)."

---

## 7. Follow-ups (not in this delivery)

- **JEDTester (`BlinkyGalTest.ino`):** add the six new instructions to the enum/name/
  classification tables; GAL2 OUT0 renames to `NZ_WE`; GAL3's `GalDef` changes — pin 13
  is now an output to compare (`C_WE`), so `megaDrivesPins1213` no longer applies as-is
  for GAL3 (only pin 12 remains a held spare).
- **BlinkASM:** mnemonics AND, OR, CMP, ASR, ROL, ROR → opcode 1, nibbles 8/C/9/B/E/F.
- **TTLSim schematic:** add the '153 fill mux and the `C_WE` net/latch-enable split; then
  re-import the four WinCUPL-compiled JEDs and sweep all 256 IR values against the sim
  (the GAL JEDEC import follows the fuses, so no C# model change is needed).
- **Verification chain:** WinCUPL-compile all four rev-2 PLDs (device `g16v8`),
  cross-check fuse-for-fuse with BlinkyJED as before, JEDTester sweep on real silicon,
  then the TTLSim run above.

## 8. Term-budget summary (16V8 ceiling: 8 per output)

| GAL1 | terms | GAL2 | terms | GAL3 | terms | GAL4 | terms |
|---|---|---|---|---|---|---|---|
| ALU_S0 | 3 | TOS_SRC0 | 2 | DSP_EN | 6 | PCSEL0 | 5 |
| ALU_S1 | 4 | TOS_SRC1 | 1 | DSP_UD | 2 | PCSEL1 | 1 |
| ALU_S2 | 4 | TOS_SRC2 | 2 | RSP_EN | 3 | ALU_CN | 3 |
| ALU_S3 | 3 | NOS_LD | 6 | RSP_UD | 2 | IO_RD | 1 |
| ALU_M | 3 | NOS_SRC | 2 | RDIN_SEL | 1 | IO_WR | 1 |
| C_SRC | 2 | NZ_WE | 4 | CLK_RUN | 1 | IOADDR_SEL | 1 |
| TOS_M1 | 7 | DMEM_CS | 2 | C_WE | 4 | | |
| TOS_M0 | 7 | DMEM_WE | 1 | | | | |
