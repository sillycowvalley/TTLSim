# Towers of Hanoi — a Blinky ISA Case Study

Towers of Hanoi is the canonical stress test for a call-heavy ISA: every node of the
recursion keeps **four live values** (`n, from, to, via`) and needs them **three times in
three different orders** — once for each child call and once for the move between them.
A machine is measured here on exactly two things: how cheaply it enters and leaves a
subroutine, and how cheaply it keeps four bytes in the right places across those calls.

This document implements the same algorithm on Blinky and on a 65C02, side by side, with per-instruction cycle counts, and closes with a same-clock (4 MHz) comparison.

```
hanoi(n, from, to, via):
    if n == 0: return
    hanoi(n-1, from, via, to)      ; child A
    move from -> to
    hanoi(n-1, via,  to, from)     ; child B
```

## How each machine answers the problem

**Blinky** answers with **hardware call frames**: `ENTER` opens a per-level frame in
D-memory, `LOCAL@`/`LOCAL!` give random access to the four values by BP-relative offset in any order, and `CALL`/`RET` save and restore `{PC, BP}` automatically in one cycle each. No value is ever permuted on the stack and no state is explicitly saved or
restored — each recursion level simply owns its slots.

**The 65C02** answers with **zero page as a register file**: the four values live at
fixed ZP addresses for fast random access, but ZP is *global*, so every level must
`PHA`-save all four before recursing and `PLA`-restore them after — and permute two of
them in place before each child call.

Same algorithm, same structure, same port-write move. The difference is purely what the ISA charges for calls and for state.

---

## Blinky implementation

Calling convention: caller pushes the four arguments on the data stack in the order
**via, to, from, n** (n on top, so the base case tests without touching the frame).
Frame layout after `ENTER 4`: `n` at BP−4, `from` at BP−3, `to` at BP−2, `via` at BP−1.

Every instruction is 1 cycle.

```
; ports
FROM_PORT = 0
TO_PORT   = 1

main:   PUSH #2          ; via  = peg 2                                   1
        PUSH #1          ; to   = peg 1                                   1
        PUSH #0          ; from = peg 0                                   1
        PUSH #8          ; n    = 8 disks                                 1
        CALL hanoi       ;                                                1
        HALT

; ---- hanoi ( via to from n -- ) ------------------------------ cycles --
hanoi:  TST              ; flags of n (non-destructive)                   1
        BEQ leaf         ; n == 0 -> unwind the arguments                 1
        ENTER 4          ; open this level's frame (BP += 4)              1
        LOCAL! -4        ; n                                              1
        LOCAL! -3        ; from                                           1
        LOCAL! -2        ; to                                             1
        LOCAL! -1        ; via                                            1

        ; child A: hanoi(n-1, from, via, to)
        LOCAL@ -2        ; to    -> callee's via                          1
        LOCAL@ -1        ; via   -> callee's to                           1
        LOCAL@ -3        ; from  -> callee's from                         1
        PUSH #1          ;                                                1
        LOCAL@ -4        ; n                                              1
        SUB              ; n-1   (TOS - NOS)                              1
        CALL hanoi       ; pushes {PC+1, BP}, one cycle                   1

        ; move from -> to
        LOCAL@ -3        ; from                                           1
        OUT #FROM_PORT   ; pops                                           1
        LOCAL@ -2        ; to                                             1
        OUT #TO_PORT     ; pops                                           1

        ; child B: hanoi(n-1, via, to, from)
        LOCAL@ -3        ; from  -> callee's via                          1
        LOCAL@ -2        ; to    -> callee's to                           1
        LOCAL@ -1        ; via   -> callee's from                         1
        PUSH #1          ;                                                1
        LOCAL@ -4        ; n                                              1
        SUB              ; n-1                                            1
        CALL hanoi       ;                                                1

        RET              ; PC and BP restored together — frame freed      1

leaf:   DROP             ; n (= 0)                                        1
        DROP             ; from                                           1
        DROP             ; to                                             1
        DROP             ; via                                            1
        RET              ;                                                1
```

**Internal node: 26 cycles. Leaf: 7 cycles. 31 instructions, 62 bytes.**

Points worth noticing:

- There is **no restore code at all**. `RET` restores BP, which both frees this level's
  frame and re-exposes the parent's — the 28-cycle `PLA`/`STA` block the 65C02 needs simply doesn't exist.
- The arguments are never permuted. Child A and child B read the *same* slots in
  different orders; the "shuffle" is just which offset each `LOCAL@` names.
- `TST`-before-`ENTER` keeps the leaf path frameless: test, drop, return.

## 65C02 implementation

Parameters in zero page; each level saves and restores all four around child A (the
subtree clobbers ZP), and permutes in place before each call. Cycle counts per the
65C02 data sheet.

```
; zero page
N     = $00
FROM  = $01
TO    = $02
VIA   = $03
PFROM = $C000            ; output ports
PTO   = $C001

; ---- hanoi ---------------------------------------------------- cycles --
hanoi:  lda N            ;                                                3
        beq done         ; n == 0                                         2
        pha              ; save n (A still holds it)                      3
        lda FROM         ; save frame                                     3
        pha              ;                                                3
        lda TO           ;                                                3
        pha              ;                                                3
        lda VIA          ;                                                3
        pha              ;                                                3
        dec N            ; n-1                                            5
        lda TO           ; swap TO <-> VIA  (child A peg order)           3
        ldx VIA          ;                                                3
        sta VIA          ;                                                3
        stx TO           ;                                                3
        jsr hanoi        ; child A                                        6
        pla              ; restore frame                                  4
        sta VIA          ;                                                3
        pla              ;                                                4
        sta TO           ;                                                3
        pla              ;                                                4
        sta FROM         ;                                                3
        pla              ;                                                4
        sta N            ;                                                3
        lda FROM         ; move from -> to                                3
        sta PFROM        ;                                                4
        lda TO           ;                                                3
        sta PTO          ;                                                4
        dec N            ; n-1                                            5
        lda FROM         ; swap FROM <-> VIA (child B peg order)          3
        ldx VIA          ;                                                3
        sta VIA          ;                                                3
        stx FROM         ;                                                3
        jsr hanoi        ; child B                                        6
done:   rts              ; caller's restore block repairs ZP              6
```

**Internal node: 120 cycles. Leaf: 12 cycles (`lda` 3 + `beq` taken 3 + `rts` 6).
~34 instructions, 63 bytes.**

The ZP-as-registers model gives the 65C02 genuinely fast random access — `lda zp` is
3 cycles — but because ZP is global, the *frame semantics* Blinky gets from BP must be
synthesized in software: 21 cycles of save, 28 of restore, and 24 of in-place permutation, every node.

---

## Where the cycles go (per internal node)

| Cost category                       | Blinky                     | 65C02                 |
| ----------------------------------- | -------------------------- | --------------------- |
| Base-case test                      | 2                          | 5                     |
| Frame open / state save             | 5 (`ENTER` + 4× `LOCAL!`)  | 21 (`PHA` ×4 + loads) |
| State restore                       | **0** (automatic on `RET`) | 28 (`PLA`/`STA` ×4)   |
| Argument ordering for the two calls | 6 (`LOCAL@` ×3, twice)     | 24 (two ZP swaps)     |
| n−1 (twice)                         | 6                          | 10                    |
| CALL/CALL/RET vs JSR/JSR/RTS        | **3**                      | 18                    |
| The move (two port writes)          | 4                          | 14                    |
| **Total**                           | **26**                     | **120**               |

Two lines carry the whole result. Keeping four bytes in the right places costs the
65C02 **73 cycles** per node (save + restore + permutation) against Blinky's **11** —
per-level frames make the save/restore vanish and turn permutation into offset
selection. And the call machinery itself costs 18 cycles against 3.

## Comparison at 4 MHz (n = 8, 255 moves)

Internal nodes 255, leaf calls 256.

| Metric                   | Blinky                    | 65C02                                            |
| ------------------------ | ------------------------- | ------------------------------------------------ |
| Cycles per instruction   | always 1                  | 2–7                                              |
| Cycles per internal node | 26                        | 120                                              |
| Cycles per leaf call     | 7                         | 12                                               |
| Average cycles per move  | 33                        | 132                                              |
| Total cycles             | 8,422                     | 33,672                                           |
| **Run time @ 4 MHz**     | **2.1 ms**                | **8.4 ms**                                       |
| Moves per second @ 4 MHz | ~121,000                  | ~30,300                                          |
| Code size                | 62 bytes (31 instr)       | 63 bytes (~34 instr)                             |
| Recursion depth limit    | 256 levels (return stack) | ~42 levels (6 bytes/level of the 256-byte stack) |
| **Relative speed**       | **4.0×**                  | 1×                                               |

## Advantages and disadvantages

**Where the Blinky ISA wins here:**

- **Single-cycle CALL/RET with automatic BP save/restore.** The entire enter/leave
  machinery — return address, frame pointer, frame free — is 3 cycles per node against the 65C02's 18, and it eliminates the 49 cycles of software save/restore entirely.
- **Frame locals kill operand permutation.** Both children read the same four slots in
  whatever order they need; the 65C02 physically rearranges zero page twice per node.
- **Determinism.** One cycle per instruction, no page-crossing or branch-taken
  variability — the cycle counts above are exact, which also makes the front panel
  legible: the BP LEDs breathe with recursion depth in real time.
- **Depth.** 256 return-stack levels and a frame region of 4 cells/level in 8K of
  D-memory, against the 65C02's ~42 levels before its 256-byte stack wraps.

**Where it loses, honestly:**

- **Argument marshalling is paid twice.** Every call pushes four values (`LOCAL@` ×4)
  that the callee immediately stores (`LOCAL!` ×4) — 8 cycles per call of pure copying
  that a machine passing arguments in registers, or reading the caller's frame
  directly, would avoid. It is the price of the pure-stack calling convention, and it
  is most of what remains of the 26-cycle node.
- **Instruction fetch bandwidth.** Every instruction is 16 bits — two memory devices on the I-bus against the 65C02's one. (Code *density* comes out even here — 62 vs 63 bytes — because 65C02 instructions average ~2 bytes too; the cost is bus width, not bytes.)
- **The equal-clock framing flatters Blinky.** 4 MHz is within the HC + 22V10 critical
  path, but production 65C02s clock to 14 MHz. At maximum rated clocks the 4.0×
  architectural advantage shrinks to roughly 1.2–1.5× in wall-clock time. The ISA wins;
  fabricated silicon claws most of it back on frequency.
- **8-bit data path.** Irrelevant to Hanoi (n = 8 is already 255 moves; the growth is
  exponential long before the width binds), but the 65C02's indexed addressing modes generalize to array-heavy workloads in ways `FETCH`'s 256-cell reach does not.

The summary: on the workload a stack machine is supposed to lose — many live locals,
reordered at every call — the frame-local extension makes Blinky win both halves.
Call/return was always ~6× cheaper; frames make the state-keeping ~7× cheaper too, and the composite is a clean 4× at equal clock.
