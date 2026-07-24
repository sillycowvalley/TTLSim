# The 74181 — Definitive Reference

A standalone reference for the 74LS181 4-bit ALU slice as used in the Blinky
family and modelled in TTLSim. It consolidates the datasheet facts, the
conventions this project has ratified, and every trap this part has sprung on
this project or is documented to have sprung on others. When this document
and a datasheet disagree, check the errata section before deciding which one
is wrong — the datasheets have form.

**Provenance.** Reconstructed and cross-witnessed rather than photographed:
the function tables agree with the tomnisbet.github.io/nqsap-pcb 74181 notes
(save that page's own S=0001 notation slip), satisfy the De Morgan dual
identity between columns row-for-row, and match the observed behaviour of a
faithful active-low implementation (TTLSim, 2026-07-24 runs) under the dual.
The normative printed source is the **TI 1988 datasheet**; diff against a
physical copy before treating any transcription — including this one — as
final.

---

## 1. What it is

A 4-bit arithmetic/logic slice: two 4-bit operands A and B, a 4-bit function
select S3–S0, a mode pin M (high = 16 logic functions, low = 16 arithmetic
functions), a carry in, and a 4-bit result F. It also produces carry-out,
carry-lookahead terms (P̄, Ḡ) for pairing with a 74182, and an
open-collector A=B output. Slices cascade to any multiple of 4 bits.

The '181 exists in **LS only** for practical purposes. HC181s are
effectively unobtainable; every design note below assumes LS levels and the
HCT fence that follows from them (§10).

## 2. The one decision that governs everything: the data convention

The datasheet publishes **two complete function tables** for the same
silicon: one assuming active-high data on the pins (pin high = logical 1),
one assuming active-low data (pin low = logical 1). They are De Morgan duals
of each other:

```
F_low(A, B, S, M) = NOT F_high(NOT A, NOT B, S, M)
```

with the carry pin senses inverting between the columns.

**This project's convention is ACTIVE-HIGH, everywhere, always.** True
binary on every A, B, and F pin; the active-high column applies verbatim;
all carry-pin inversion is absorbed at single defined points (a GAL
equation on the way in, one HCT gate on the way out — §5).

### The wrong-column trap

The columns agree on exactly the operations a casual test exercises:

- ADD is S=1001 in **both** columns.
- SUB (A−B−1 form) is S=0110 in **both** columns.
- Pass-A is M=1 S=1111 in **both** columns.

Everything else differs. A design (or a simulator model, or a datasheet
transcription) built from the wrong column **passes an add/subtract sweep
perfectly** and then returns the De Morgan dual for every logic operation:
AND↔OR swap (1011↔1110), XOR↔XNOR (0110↔1001 logic), constants 0↔1
(0011↔1100 logic), increment↔decrement (0000↔1111 arithmetic), and every
carry off by exactly one because the carry sense flips with the column.

This trap has now been sprung, independently, by: the Thumby ALU draft
(inverting boundary ratified alongside the wrong-column claim), the TTLSim
Hc181 model, and — in fragments — two of the three major published
datasheets. Treat any '181 source as wrong-column until it passes the
discriminator vectors in §13.

## 3. Pinout (DIP-24, TI N package)

| Pin | Name | Dir | Pin | Name | Dir |
|-----|------|-----|-----|------|-----|
| 1 | B0 | in | 24 | VCC | — |
| 2 | A0 | in | 23 | A1 | in |
| 3 | S3 | in | 22 | B1 | in |
| 4 | S2 | in | 21 | A2 | in |
| 5 | S1 | in | 20 | B2 | in |
| 6 | S0 | in | 19 | A3 | in |
| 7 | Cn | in | 18 | B3 | in |
| 8 | M | in | 17 | **Ḡ** | out |
| 9 | F0 | out | 16 | **Cn+4** | out |
| 10 | F1 | out | 15 | **P̄** | out |
| 11 | F2 | out | 14 | A=B | out (OC) |
| 12 | GND | — | 13 | F3 | out |

The 15/16/17 cluster is a known transcription hazard: P̄ = 15, Cn+4 = 16,
Ḡ = 17. One version of the TTLSim part definition had all three rotated.

## 4. Function tables — ACTIVE-HIGH column

### M = 1, logic (Cn is a don't-care)

| S3:0 | F | S3:0 | F |
|------|---|------|---|
| 0000 | /A | 1000 | /A + B |
| 0001 | /(A + B)  — NOR | 1001 | /(A ⊕ B)  — XNOR |
| 0010 | /A · B | 1010 | B |
| 0011 | 0 (all zeros) | 1011 | A · B |
| 0100 | /(A · B)  — NAND | 1100 | 1 (all ones) |
| 0101 | /B | 1101 | A + /B |
| 0110 | A ⊕ B | 1110 | A + B |
| 0111 | A · /B | 1111 | A |

S=0001 (NOR) and S=0100 (NAND) are distinct functions. Any table showing
them equal contains the known transcription typo (§11).

### M = 0, arithmetic (every entry *plus Cn*; Cn pin LOW adds 1)

| S3:0 | F (no carry) | S3:0 | F (no carry) |
|------|--------------|------|--------------|
| 0000 | A | 1000 | A plus (A·B) |
| 0001 | A + B | 1001 | **A plus B** |
| 0010 | A + /B | 1010 | (A + /B) plus (A·B) |
| 0011 | minus 1 (all ones) | 1011 | (A·B) minus 1 |
| 0100 | A plus (A·/B) | 1100 | A plus A |
| 0101 | (A + B) plus (A·/B) | 1101 | (A + B) plus A |
| 0110 | **A minus B minus 1** | 1110 | (A + /B) plus A |
| 0111 | (A·/B) minus 1 | 1111 | A minus 1 |

In the arithmetic rows `+` is logical OR and `plus`/`minus` are arithmetic —
the datasheet's own notation, kept deliberately: it is a second trap for the
careless reader.

### The workhorses

| Operation | M | S | Cn (logical) | Notes |
|-----------|---|------|----|-------|
| Pass A | 1 | 1111 | — | flags-safe pass on hosts that latch NZ |
| Pass B | 1 | 1010 | — | post-shifter MOV on ARM-flavoured hosts |
| Add | 0 | 1001 | 0 | |
| Add with carry | 0 | 1001 | C | |
| Subtract (true) | 0 | 0110 | 1 | A − B; the +1 completes the two's complement |
| Subtract w/ borrow | 0 | 0110 | C or ¬C | per host convention, §5 |
| Compare | 0 | 0110 | 1 | discard F, keep flags |
| Increment | 0 | 0000 | 1 | |
| Decrement | 0 | 1111 | 0 | |
| Shift left / ROL | 0 | 1100 | 0 or C | A plus A; the only shift it has |
| Negate | 0 | 0110 | 1 | with A = 0 |
| Constant 0 / −1 | 1 | 0011 / 1100 | — | note the logic-row constants are the *opposite* codes in the active-low column |

## 5. The carry system

### Pin senses (active-high data convention)

- **Cn (in): pin LOW = carry-in of 1.** Any arithmetic row gets +1 when the
  pin is low.
- **Cn+4 (out): pin LOW = carry-out of 1** on addition. On subtraction, pin
  LOW = no borrow (the result is ≥ 0 unsigned).

Both are therefore "active-low logical carry". Slices chain **directly** —
Cn+4 of slice n into Cn of slice n+1, no inverter — because both ends agree.
The senses invert together if you move to the active-low data column, which
is why a wrong-column model shows every arithmetic result off by exactly
one.

Absorb the inversion once, at defined points. In the Blinky ALU module: the
function-decode GAL computes the *logical* carry wanted and its OLMC
polarity fuse drives the pin (`PIN 14 = !CN` in the source — zero gates),
and one HCT inverter section conditions Cn+4 back to an active-high COUT.
Board-to-board carry crosses as the raw pin-level signal, needing nothing.

### Borrow conventions

C-flag semantics differ by host and both are one equation away:

- **'181-native / 6502-style**: C = 1 means no borrow. SBC/SUBC inject
  Cn = C. This is the multi-word-friendly convention — the carry a
  subtraction produces is the carry the next word's subtraction needs.
- **8080/Z80-style**: CY = 1 means borrow. SBB injects Cn = ¬CY, and the
  latched flag is the inverted Cn+4 on subtract ops (one XOR, gated by a
  SUB indicator).

### Multi-slice behaviour worth knowing cold

For ADD/SUB-class ops all slices perform the same function and the carry
chain does the rest. **Increment and decrement are different**: only the low
slice receives the injected carry; upper slices compute plain A (S=0000) or
A−1-plus-carry (S=1111) and the chain propagates naturally — INC of 0x0F
gives 0x10 because the low slice's carry-out promotes the upper slice from A
to A+1. It works with zero special-casing, but it breaks the tempting
"whole-word carry = CIN ⊕ COUT" detection shortcut, which holds for add and
subtract and fails for inc/dec. Derive word-level carry from Cn+4 of the top
slice and the operation class, not from an end-to-end XOR.

## 6. A=B (pin 14)

Three facts, each of which has fooled someone:

1. **It does not compare A with B.** It asserts when **all four F pins are
   high** — result = all ones. It means "A equals B" only for an operation
   whose result is all-ones on equality: A−B−1 (S=0110, carry-in of 0). It
   also reads A==0 after A−1. For any other operation it is noise.
2. **It is open-collector**: drives low, releases high. Slices wire-AND on
   one pull-up to give a multi-slice equality signal — that is the pin's
   entire reason for being OC. Two released slices + pull-up = equal across
   the word; any one slice pulls the shared net low.
3. **A panel LED must wire from VCC to the pin** (with its resistor), not
   pin-to-ground — the pin never drives high, so a ground-referenced LED
   never lights.

The pull-up also means the node swings rail-to-rail, so it can feed HC
inputs directly despite living on an LS part — but the OC rise through the
pull-up is the slowest signal in the neighbourhood; measure before trusting
it inside a tight flag-setup window.

**A=B is not a zero flag.** Zero detect in the active-high convention is a
'688 (or equivalent) on the result — asserting on all-*ones* is only a zero
detect if you inverted the data at the boundary, which this project
deliberately does not (§2, and see the Thumby post-mortem in §11).

## 7. P̄, Ḡ, and the '182

P̄ (propagate) and Ḡ (generate) are active-low in **both** data
conventions and pair directly with the 74182 lookahead generator: slice
P̄/Ḡ into the '182's P̄i/Ḡi inputs, '182 Cn+x/Cn+y/Cn+z (assert low) into
the upper slices' Cn pins — no inverters anywhere in the chain.

**Partially populated '182** (two slices on a byte board): tie the unused
inputs asymmetrically —

- unused Ḡ inputs → VCC (never generate),
- unused P̄ inputs → **GND (always propagate)**.

All-inactive is the intuitive tie and it is wrong for one specific purpose:
it makes the '182's *group* P̄/Ḡ outputs useless for a second lookahead
level, because a carry generated in a real slice can never "propagate"
through the phantom ones. Propagate-asserted/generate-inactive makes the
group terms byte-true, so a two-board pair can add an off-board second-level
'182 later without touching the boards. The '182's Cn+x/Cn+y are functions
of the real slices only and are unaffected by the tie either way.

The '182 is a **Schottky** part — the only one on a typical board. Budget
its current and decouple it properly. The quirk that group-Ḡ excludes P0 is
faithful in the project's GAL16V8 re-implementation and matters only at a
second lookahead level.

## 8. The asymmetries — what the '181 cannot do

- **Arithmetic is A-centric.** B appears only as an addend/subtrahend.
  There is no B+1, no B−1. Decrementing a B-side value takes two passes
  (negate: 0−B, then complement) — route the operand to the A side instead
  if the datapath allows.
- **There is no right shift.** A plus A (S=1100) is the only shift, and it
  goes left. Right shifts are a wiring permutation outside the part — a
  crossed buffer on the result with a serial-in bit at the top (LSR/ASR/ROR
  by choice of that bit).
- **NOT-B exists (0101 logic) but NOT as an arithmetic operand** — there is
  no A plus /B plus 1 single-code true subtract-reversed. Synthesise or
  restructure.
- **No BCD.** Half-carry is available at the slice-0/slice-1 boundary
  (Cn+x when using a '182, the inter-slice wire when rippling), which is
  everything a DAA implementation needs — but the adjust logic itself is
  external, and the '181 contributes nothing to it.

## 9. The V flag — why so many '181 machines dropped it

The textbook V = Cn+7 ⊕ Cn+8 is **unreachable in a 4+4 slicing**: the
carry into bit 7 is internal to the high slice and never reaches a pin. The
sign-bit formulation needs no internal carry and is exact wherever V is
defined (add and subtract):

```
Beff = B_msb ⊕ SUB          ; folds subtraction into the addition rule
V    = (A_msb ⊕ F_msb) · (Beff ⊕ F_msb)
```

Three XORs and an AND. On logic operations the result is garbage — the flag
policy must simply not latch it, which costs nothing on a host with split
flag-enable domains. SUB is one bit the function decode already knows.

## 10. Electrical

- **LS outputs into HC inputs is the classic intermittent**: LS VOH ≈ 2.7 V
  against HC VIH ≈ 3.5 V. It works on the bench and fails in the case.
  Every '181 output that feeds CMOS goes through an **HCT** stage — one
  HCT541 per byte of result as the single level fence, HCT gates on the
  carry conditioning. Parts *behind* the fence can be plain HC. HC→LS in
  the other direction needs nothing.
- The exceptions to the fence: the A=B node (rail-to-rail via its pull-up,
  §6) and raw pin-level carry crossing to another '181/'182 Cn input
  (LS→LS by construction).
- Tie every unused input. A floating S or M line produces results that look
  like a microcode bug for a long time before the wiring is suspected.
- The '181s are the board's current draw; budget the fully populated case.

## 11. Sources of truth, and the errata trail

- **TI 1988 datasheet: normative.** When sources disagree, it wins.
- **Fairchild 2000**: lists the A·/B row incorrectly as A·B, and garbles
  the XNOR row. Do not transcribe from it.
- **TI 1983 Databook**: misprints A plus A as A.
- **nqsap-pcb 74181 notes**: excellent on carry semantics and A=B; its
  operations list matches the active-high column except S=0001 logic, which
  it renders "not A or not B" without parentheses — reading as NAND, which
  would duplicate S=0100. The TI value is NOR, /(A+B). (A simulator model
  carrying exactly this duplicate suggests this page, or a shared ancestor,
  as the transcription source.)
- **This project's doctrine**, earned the hard way: no transcription is
  trusted, including this one. The shipped function table for any '181
  implementation — board or model — is the **sweep-captured table**: all 32
  rows, both Cn states on the arithmetic rows, walking-ones / all-ones /
  alternating / boundary operand patterns, captured from the implementation
  and diffed against this reference. Anything short of that has, on this
  project's record, a roughly one-in-one chance of being the wrong column,
  a rotated pin cluster, or a duplicated row.

## 12. Appendix — the ACTIVE-LOW column (for recognising it, not using it)

Derived by F_low(A,B) = /F_high(/A,/B); carry senses flip (Cn pin HIGH adds
1 in this column). Logic, M=1:

| S3:0 | F | S3:0 | F |
|------|---|------|---|
| 0000 | /A | 1000 | /A · B |
| 0001 | /(A · B) | 1001 | A ⊕ B |
| 0010 | /A + B | 1010 | B |
| 0011 | 1 | 1011 | A + B |
| 0100 | /(A + B) | 1100 | 0 |
| 0101 | /B | 1101 | A · /B |
| 0110 | /(A ⊕ B) | 1110 | A · B |
| 0111 | A + /B | 1111 | A |

Arithmetic signature rows: S=0000 is A **minus 1** plus carry (active-high:
A plus carry); S=1111 is A plus carry (active-high: A minus 1 plus carry).
If a device shows A−1 at S=0000/M=0, it is serving this column.

## 13. Discriminator vectors — the wrong-'181 detector

Ten minutes with these settles any implementation's honesty. Active-high
expectations; A = 0F, B = 33 for the logic rows.

| # | Drive | Correct | Wrong column gives | Other failure it catches |
|---|-------|---------|--------------------|--------------------------|
| 1 | M=1 S=1011 | 03 (AND) | 3F (OR) | |
| 2 | M=1 S=1110 | 3F (OR) | 03 (AND) | |
| 3 | M=1 S=0110 | 3C (XOR) | C3 (XNOR) | |
| 4 | M=1 S=0001 vs S=0100 | NOR ≠ NAND | (swapped, still distinct) | **equal ⇒ the duplicated-row typo** |
| 5 | M=0 S=0000, Cn pin H | A | A−1 | |
| 6 | M=0 S=1001, A=0F B=01, Cn pin H | 10 | — | carry-in sense if 11 |
| 7 | same, Cn pin L | 11 | — | carry-in sense if 10 |
| 8 | M=0 S=1001, A=FF B=01 | Cn+4 pin LOW | pin high | Cn+4 sense; also pin-map rotation if it appears on 15 or 17 |
| 9 | M=0 S=0110, A=B, Cn pin H | F = all ones, A=B releases | | A=B totem-pole modelling (contention on a wire-AND) |
| 10 | 8-bit pair, 0F+01 via '182 | 10 | | P̄/Ḡ pin rotation, '182 pairing |
| 11 | M=0 S=1100 (A plus A), A=81 | Cn+4 asserts; paired '182 carry asserts | | **lookahead exports inconsistent with the slice's own arithmetic** — the rotate codes are the first place a per-code P̄/Ḡ transcription slip surfaces |

Vectors 1 and 6 together are the sixty-second version: AND that returns OR
is the column; ADD that returns +1 is the carry sense. Both must pass —
they fail independently.
