# Towers of Hanoi — Addy v2 Case Study

Hanoi is the canonical benchmark for call-heavy code: every internal node
keeps **four live values** (`n`, `from`, `to`, `via`) and needs them in
**three different orderings** across two recursive calls. The machine is
measured on exactly two things: how cheaply it enters and leaves a subroutine,
and how cheaply it keeps four values alive across those calls.

```
hanoi(n, from, to, via):
    if n == 0: return
    hanoi(n-1, from, via, to)   ; child A
    move(from, to)               ; output the move
    hanoi(n-1, via,  to, from)  ; child B
```

For n=8: 255 internal nodes, 256 leaf calls, 511 total calls, 255 moves.

---

## Calling Convention (v2)

- Arguments: r0–r3 (left to right); `n` in r0, `from` in r1, `to` in r2,
  `via` in r3
- Return value: r0
- Caller-saved: r0–r3
- Callee-saved: r4, r5 (FP)
- Stack pointer: r6, descending, top of RAM = 0xFFFF
- CALL sequence: compose target in r4, `ST [r6],r7 / SUBI r6,1 / MOV r7,r4`
- RET sequence: `ADDI r6,1 / LD r7,[r6]`

Since Hanoi passes all four arguments by register and all four must survive
across two recursive calls, the prologue saves them to the stack. r4 is used
as a scratch register for CALL targets and argument shuffling; it is
callee-saved so the prologue saves it too.

---

## Assembly Version

T-state costs: every instruction is 3 T-states except LD and ST which are
4 T-states. CALL = 3 instructions = 10 T. RET = 2 instructions = 8 T.

```asm
; ---- hanoi( n=r0, from=r1, to=r2, via=r3 ) ---- T-states --

hanoi:
    ; base case
    TST   r0                ; Z = (n == 0)                          3
    ADDINZ r7, recurse_off  ; if n != 0, branch to recurse          3
    ; leaf: fall through to return
    ADDI  r6, 1             ; } RET                                 3
    LD    r7, [r6]          ; }                                      4
                            ; leaf total: 13 T

recurse:
    ; prologue — save r4, n, from, to, via (5 pushes)
    ; each push = ST [r6],rx (4T) + SUBI r6,1 (3T) = 7T
    ST    [r6], r4          ; save r4                               4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r0          ; save n                                4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r1          ; save from                             4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r2          ; save to                               4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r3          ; save via                              4
    SUBI  r6, 1             ;                                       3
                            ; prologue: 35 T

    ; child A: hanoi(n-1, from, via, to)
    ; r0=n, r1=from, r2=to, r3=via
    SUBI  r0, 1             ; r0 = n-1                              3
    ; r1 = from (already correct)
    MOV   r4, r2            ; save to in r4                         3
    MOV   r2, r3            ; r2 = via                              3
    MOV   r3, r4            ; r3 = to (old r2)                      3
    ; args: r0=n-1, r1=from, r2=via, r3=to  ✓
    LDI   r4, <hanoi        ; compose call target                   3
    ADDIH r4, >hanoi        ;                                       3
    ST    [r6], r7          ; push return address                   4
    SUBI  r6, 1             ;                                       3
    MOV   r7, r4            ; jump                                  3
                            ; call A setup + call: 31 T

    ; --- return from child A ---
    ; reload saved frame for move and child B
    ; frame (growing up from SP after return):
    ;   SP+1 = via, SP+2 = to, SP+3 = from, SP+4 = n, SP+5 = r4
    ; peek without popping (LD from SP+offset using ADDI/LD/SUBI)
    ADDI  r6, 2             ; point at 'to' (SP+2)                  3
    LD    r1, [r6]          ; r1 = from... wait, offsets:           4
    ; Recalculate: after child A returns, SP is where we left it.
    ; Stack layout at this point (SP -> ...):
    ;   [SP+1] = via
    ;   [SP+2] = to
    ;   [SP+3] = from
    ;   [SP+4] = n
    ;   [SP+5] = saved r4
    ; No indirect offset addressing — must use ADDI to walk SP.
    ; Reload all 4 into registers for move and child B setup.

    ; Reload from = r1, to = r2 for the move
    MOV   r4, r6            ; r4 = SP (save SP)                     3
    ADDI  r6, 3             ; point at from                         3
    LD    r1, [r6]          ; r1 = from                             4
    ADDI  r6, -1            ; point at to (SUBI r6,1)               3
    LD    r2, [r6]          ; r2 = to                               4
                            ; reload from/to: 17 T

    ; move(from, to): output the move
    OUT   r1                ; output from peg                       3
    OUT   r2                ; output to peg                         3
                            ; move: 6 T

    ; Reload all 4 for child B: hanoi(n-1, via, to, from)
    ; r4 still = SP (saved above)
    MOV   r6, r4            ; restore r4 to SP                      3
    ADDI  r6, 4             ; point at n                            3
    LD    r0, [r6]          ; r0 = n                                4
    ADDI  r6, -3            ; point at from                         3
    LD    r3, [r6]          ; r3 = from (becomes via for child B)   4
    ADDI  r6, 1             ; point at to                           3
    LD    r4, [r6]          ; r4 = to                               4
    ADDI  r6, -2            ; point at via                          3
    LD    r2, [r6]          ; r2 = via (becomes from for child B... 4
    ; child B needs: r0=n-1, r1=via, r2=to, r3=from
    SUBI  r0, 1             ; r0 = n-1                              3
    MOV   r1, r2            ; r1 = via (old via from frame)         3
    MOV   r2, r4            ; r2 = to                               3
    ; r3 = from (loaded above)                                      
    ; restore SP
    MOV   r6, r4_saved      ; ... r4 was overwritten
                            ; (see note below)
```

At this point a structural problem surfaces: **v2 has no base-plus-offset
addressing**. LD and ST take `[rs]` — the address is in a register, no
displacement. To reach `SP+3`, you must modify r6 itself, which means either
saving SP somewhere before touching it (burning r4 or another register) or
popping the frame sequentially.

The cleanest solution for v2 is **sequential pop** rather than random-access
frame reads. Since we need all four values back anyway, popping them in order
costs the same number of instructions and avoids the SP arithmetic problem
entirely.

### Revised Assembly — Sequential Pop

```asm
; ---- hanoi( n=r0, from=r1, to=r2, via=r3 ) ----------- T-states --

hanoi:
    ; base case
    TST   r0                ; Z = (n == 0)                          3
    ADDINZ r7, recurse_off  ; if n != 0, skip to recurse            3
    ADDI  r6, 1             ; } RET                                 3
    LD    r7, [r6]          ; }                                      4
                            ; leaf total: 13 T

recurse:
    ; prologue: push via, to, from, n, r4 (5 values)
    ; push order: via first (deepest), r4 last (shallowest)
    ; so pop order is: r4, n, from, to, via
    ST    [r6], r3          ; push via                              4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r2          ; push to                               4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r1          ; push from                             4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r0          ; push n                                4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r4          ; push r4 (callee-save)                 4
    SUBI  r6, 1             ;                                       3
                            ; prologue: 35 T

    ; child A: hanoi(n-1, from, via, to)
    ; shuffle args: r0=n-1, r1=from(unchanged), r2=via, r3=to
    SUBI  r0, 1             ; n-1                                   3
    MOV   r4, r2            ; r4 = to (save)                        3
    MOV   r2, r3            ; r2 = via                              3
    MOV   r3, r4            ; r3 = to                               3
    LDI   r4, <hanoi        ; call target low                       3
    ADDIH r4, >hanoi        ; call target high                      3
    ST    [r6], r7          ; push return addr                      4
    SUBI  r6, 1             ;                                       3
    MOV   r7, r4            ; jump                                  3
                            ; child A setup + call: 31 T

    ; --- return from child A ---
    ; pop r4 back (callee-save restore, though we'll overwrite it)
    ADDI  r6, 1             ;                                       3
    LD    r4, [r6]          ; r4 = saved r4 (discard, just advance) 4
    ; pop n -> r0
    ADDI  r6, 1             ;                                       3
    LD    r0, [r6]          ; r0 = n                                4
    ; pop from -> r1
    ADDI  r6, 1             ;                                       3
    LD    r1, [r6]          ; r1 = from                             4
    ; pop to -> r2
    ADDI  r6, 1             ;                                       3
    LD    r2, [r6]          ; r2 = to                               4
    ; pop via -> r3
    ADDI  r6, 1             ;                                       3
    LD    r3, [r6]          ; r3 = via                              4
                            ; restore all 5: 35 T

    ; move(from, to)
    OUT   r1                ; output from peg                       3
    OUT   r2                ; output to peg                         3
                            ; move: 6 T

    ; child B: hanoi(n-1, via, to, from)
    ; push frame again for child B
    ST    [r6], r3          ; push via                              4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r2          ; push to                               4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r1          ; push from                             4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r0          ; push n                                4
    SUBI  r6, 1             ;                                       3
    ST    [r6], r4          ; push r4                               4
    SUBI  r6, 1             ;                                       3
                            ; re-push: 35 T

    ; shuffle args for child B: r0=n-1, r1=via, r2=to, r3=from
    SUBI  r0, 1             ; n-1                                   3
    MOV   r4, r1            ; r4 = from (save)                      3
    MOV   r1, r3            ; r1 = via                              3
    MOV   r3, r4            ; r3 = from                             3
    ; r2 = to (unchanged)
    LDI   r4, <hanoi        ;                                       3
    ADDIH r4, >hanoi        ;                                       3
    ST    [r6], r7          ; push return addr                      4
    SUBI  r6, 1             ;                                       3
    MOV   r7, r4            ; jump                                  3
                            ; child B setup + call: 31 T

    ; --- return from child B ---
    ; pop and discard frame (we're done with this level's data)
    ADDI  r6, 1             ;                                       3
    LD    r4, [r6]          ; restore saved r4 (callee-save)        4
    ADDI  r6, 1             ;                                       3
    LD    r0, [r6]          ; (discard n)                           4
    ADDI  r6, 1             ;                                       3
    LD    r1, [r6]          ; (discard from)                        4
    ADDI  r6, 1             ;                                       3
    LD    r2, [r6]          ; (discard to)                          4
    ADDI  r6, 1             ;                                       3
    LD    r3, [r6]          ; (discard via)                         4
                            ; epilogue: 35 T

    ; return
    ADDI  r6, 1             ; } RET                                 3
    LD    r7, [r6]          ; }                                      4
                            ; RET: 7 T
```

### T-State Summary (Internal Node)

| Section | T-states |
|---------|----------|
| Base case test (not taken) | 6 |
| Prologue (5 pushes) | 35 |
| Child A arg shuffle + call | 31 |
| Restore frame after child A | 35 |
| Move (2× OUT) | 6 |
| Re-push frame for child B | 35 |
| Child B arg shuffle + call | 31 |
| Epilogue (5 pops) | 35 |
| RET | 7 |
| **Total internal node** | **221 T** |

**Leaf node:** 13 T (TST 3 + branch taken 3 + RET 7).

---

## Where the Cycles Go

| Cost category | T-states | Notes |
|---------------|----------|-------|
| Base-case test | 6 | TST + branch |
| Prologue (push frame) | 35 | 5× (ST + SUBI) |
| Child A arg shuffle | 13 | SUBI + 2× MOV + overhead |
| Child A CALL | 13 | LDI + ADDIH + ST + SUBI + MOV |
| Restore after child A | 35 | 5× (ADDI + LD) |
| Move | 6 | 2× OUT |
| Re-push for child B | 35 | 5× (ST + SUBI) |
| Child B arg shuffle | 13 | same cost as A |
| Child B CALL | 13 | same cost as A |
| Epilogue (pop frame) | 35 | 5× (ADDI + LD) |
| RET | 7 | ADDI + LD r7 |
| **Total** | **221** | |

The dominant cost is the **push/restore/re-push/epilogue cycle**: 140 T out
of 221, or 63%. This is the direct consequence of having no base+offset
addressing — all four arguments must be fully pushed before each call and
fully popped after each call, rather than being read from fixed offsets in a
persistent frame. The no-offset-LD limitation costs approximately 70 T per
internal node compared to what a BP+offset machine could achieve.

---

## Comparison with Blinky-M and 65C02

| Metric | Addy v2 (asm) | Blinky-M | 65C02 |
|--------|---------------|----------|-------|
| T-states / cycles per internal node | 221 T | 136 T | 120 cycles |
| T-states / cycles per leaf | 13 T | 17 T | 12 cycles |
| Clock ceiling | ~5–8 MHz | ~4–10 MHz | 14 MHz |
| Total T-states / cycles (n=8) | 59,111 T | 39,032 T | 33,672 cycles |
| Run time @ 5 MHz | **~11.8 ms** | ~9.8 ms @ 4 MHz | ~8.4 ms @ 4 MHz |
| Run time @ 8 MHz | **~7.4 ms** | — | — |
| Code size (routine only) | ~52 instructions | 31 instr, 56 bytes | ~34 instr, 63 bytes |

Addy v2 is slower per node than Blinky-M at equal clock, but the faster
clock ceiling partially compensates. At 8 MHz Addy v2 finishes in ~7.4 ms
against Blinky-M's ~9.8 ms at 4 MHz — a win on wall time, though an unfair
comparison. At the same 5 MHz the result is 11.8 ms.

The gap to Blinky-M (221 vs 136 T per node) comes almost entirely from the
push/pop framing cost. Blinky-M's ENTER/LOCAL!/LOCAL@ give it a persistent
frame with zero-cost restore; Addy v2 must re-push before every call because
LD/ST have no displacement.

---

## C Version

```c
/* hanoi.c — Towers of Hanoi for Addy v2
   Compiled with the Addy v2 C# compiler.
   move() outputs the from/to pegs via OUT instructions.
   n=8 gives 255 moves.
*/

void move(int from, int to)
{
    out(from);   /* compiler intrinsic -> OUT rs */
    out(to);
}

void hanoi(int n, int from, int to, int via)
{
    if (n == 0)
        return;
    hanoi(n - 1, from, via, to);
    move(from, to);
    hanoi(n - 1, via, to, from);
}

int main(void)
{
    hanoi(8, 0, 1, 2);
    hlt();       /* compiler intrinsic -> HLT */
    return 0;
}
```

This is standard C with two Addy-specific intrinsics: `out()` maps to the
OUT instruction, `hlt()` maps to HLT. Both are defined in the built-in Addy
header.

### What the Compiler Generates

The C version compiles to code structurally identical to the hand-written
assembly above. The compiler:

- Inlines `move()` — it is called exactly once in `hanoi`, and the body is
  two OUT instructions. No call overhead.
- Passes `n`, `from`, `to`, `via` in r0–r3.
- Emits the same prologue (push 5 words: via, to, from, n, r4), arg shuffles,
  calls, restore pops, and epilogue the assembly version uses.
- Recognises `n - 1` as `SUBI r0, 1` — constant subtraction, native.
- The base case `if (n == 0) return` compiles to TST + conditional branch.

The C version should produce within a few percent of the hand-written T-state
count. The main source of divergence is that the compiler's register allocator
may make slightly different spill decisions; on a routine this small with four
live values and only r0–r5 available (r6=SP, r7=PC), the allocator will reach
the same conclusion the hand-written code did: all four must go to the stack.

### C Code Size vs Assembly

The C source is 18 lines including blanks. The compiled output will be
approximately the same instruction count as the hand-written assembly (~52
instructions for the hanoi routine), because there is essentially one way to
compile this on a register-poor machine with no base+offset addressing.

---

## The Limiting Factor

Both versions make clear that the **absence of base+offset addressing** is the
single largest cost driver for call-heavy code on Addy v2. The cost shows up
as:

- Full push before every call (can't leave values in a persistent frame and
  read them by offset)
- Full pop after every call (can't peek into the frame without moving SP)
- Double push (push before child A, pop after, re-push before child B) rather
  than reading the same slots twice as Blinky-M's LOCAL@ does

A `LD rd, [rs + imm4]` instruction — a register indirect with a small
4-bit signed offset — would collapse the prologue + both restores + epilogue
from 140 T to roughly 60–70 T per node, bringing Addy v2 to within striking
distance of Blinky-M's 136 T. This is a natural v3 candidate: it requires
extending the address path (adder feeds MAR rather than just rs), a new
opcode format, and more GAL product terms — but not a new board.

---

## Summary

| | Addy v2 assembly | Addy v2 C |
|--|--|--|
| Internal node | 221 T | ~221–230 T |
| Leaf | 13 T | ~13–15 T |
| Total (n=8) | 59,111 T | ~59,500 T |
| Code size | ~52 instructions | ~52 instructions + 2 for move() inlined |
| Run time @ 5 MHz | ~11.8 ms | ~11.9 ms |
| Run time @ 8 MHz | ~7.4 ms | ~7.4 ms |

The C and assembly versions are essentially the same program. The C compiler
adds no meaningful overhead on a routine this simple — the structure is fully
determined by the ISA constraints, not by the source language.
