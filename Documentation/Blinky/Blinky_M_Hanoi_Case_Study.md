# Towers of Hanoi — a Blinky-M ISA Case Study

Towers of Hanoi is the canonical stress test for a call-heavy ISA: every node of the
recursion keeps **four live values** (`n, from, to, via`) and needs them **three times
in three different orders** — once for each child call and once for the move between
them. A machine is measured here on exactly two things: how cheaply it enters and
leaves a subroutine, and how cheaply it keeps four bytes in the right places across
those calls.

This document implements the same algorithm on Blinky-M and on a 65C02, side by side,
with per-instruction cycle counts, and closes with a same-clock (4 MHz) comparison.
Blinky-M cycle counts are T-states from the rev 12 microcode; 65C02 counts are from
the data sheet.

```
hanoi(n, from, to, via):
    if n == 0: return
    hanoi(n-1, from, via, to)      ; child A
    move from -> to
    hanoi(n-1, via,  to, from)     ; child B
```

## How each machine answers the problem

**Blinky-M** answers with **hardware call frames**: `ENTER` opens a per-level frame in
page 0, `LOCAL@`/`LOCAL!` give random access to the four values by BP-relative offset
in any order, and `CALL`/`RET` save and restore `{PC, BP}` automatically — one
instruction each, with the frame save riding the return-stack lanes for free. No value
is ever permuted on the stack and no state is explicitly saved or restored — each
recursion level simply owns its slots.

**The 65C02** answers with **zero page as a register file**: the four values live at
fixed ZP addresses for fast random access, but ZP is *global*, so every level must
`PHA`-save all four before recursing and `PLA`-restore them after — and permute two of
them in place before each child call.

Same algorithm, same structure, same port-write move. The difference is what the ISA
charges for calls and for state — and, on a microcoded machine, what each instruction
charges in T-states.

---

## Blinky-M implementation

Calling convention: caller pushes the four arguments on the data stack in the order
**via, to, from, n** (n on top, so the base case tests without touching the frame).
Frame layout after `ENTER 4`: `n` at BP−4, `from` at BP−3, `to` at BP−2, `via` at
BP−1. Signed offsets wrap within page 0.

Cycle column = T-states, fetch included, per the rev 12 microcode.

```
; ports (I/O page 0x0100)
FROM_PORT = 0
TO_PORT   = 1

main:   PUSH #2          ; via  = peg 2                                   3
        PUSH #1          ; to   = peg 1                                   3
        PUSH #0          ; from = peg 0                                   3
        PUSH #8          ; n    = 8 disks                                 3
        CALL hanoi       ;                                                8
        HALT             ;                                                2

; ---- hanoi ( via to from n -- ) ----------------------------- T-states --
hanoi:  TST              ; flags of n (non-destructive)                   3
        BEQ leaf         ; n == 0 -> unwind the arguments             3 / 5
        ENTER 4          ; open this level's frame (BP += 4)              4
        LOCAL! -4        ; n                                              6
        LOCAL! -3        ; from                                           6
        LOCAL! -2        ; to                                             6
        LOCAL! -1        ; via                                            6

        ; child A: hanoi(n-1, from, via, to)
        LOCAL@ -2        ; to    -> callee's via                          6
        LOCAL@ -1        ; via   -> callee's to                           6
        LOCAL@ -3        ; from  -> callee's from                         6
        PUSH #1          ;                                                3
        LOCAL@ -4        ; n                                              6
        SUB              ; n-1   (TOS - NOS)                              4
        CALL hanoi       ; pushes {PC, BP} into the frame lanes           8

        ; move from -> to
        LOCAL@ -3        ; from                                           6
        OUT #FROM_PORT   ; pops                                           4
        LOCAL@ -2        ; to                                             6
        OUT #TO_PORT     ; pops                                           4

        ; child B: hanoi(n-1, via, to, from)
        LOCAL@ -3        ; from  -> callee's via                          6
        LOCAL@ -2        ; to    -> callee's to                           6
        LOCAL@ -1        ; via   -> callee's from                         6
        PUSH #1          ;                                                3
        LOCAL@ -4        ; n                                              6
        SUB              ; n-1                                            4
        CALL hanoi       ;                                                8

        RET              ; PC and BP restored together — frame freed      4

leaf:   NIP              ; discard from (pure pointer move)               1
        NIP              ; discard to                                     1
        NIP              ; discard via                                    1
        DROP             ; discard n (= 0)                                2
        RET              ;                                                4
```

**Internal node: 136 T-states. Leaf: 17 T-states (TST 3 + BEQ taken 5 + 9).
31 instructions, 56 bytes.**

Points worth noticing:

- There is still **no restore code at all**. `RET` restores BP, which both frees this
  level's frame and re-exposes the parent's — the 28-cycle `PLA`/`STA` block the 65C02
  needs simply doesn't exist.
- The arguments are never permuted. Child A and child B read the *same* slots in
  different orders; the "shuffle" is just which offset each `LOCAL@` names.
- `TST`-before-`ENTER` keeps the leaf path frameless, and `NIP` — a 1-T pure pointer
  move that touches no memory — unwinds three of the four arguments for 3 T total.
- An alternative convention was evaluated and rejected: because frames are contiguous
  in page 0, a caller *could* write the callee's slots directly (positive offsets) and
  skip the push/store round trip — but at 6 T per `LOCAL!` that costs more than the
  stack transport it replaces. The push convention stands.

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

---

## Where the cycles go (per internal node)

| Cost category                       | Blinky-M                     | 65C02                 |
| ----------------------------------- | ---------------------------- | --------------------- |
| Base-case test                      | 6                            | 5                     |
| Frame open / state save             | 28 (`ENTER` + 4× `LOCAL!`)   | 21 (`PHA` ×4 + loads) |
| State restore                       | **0** (automatic on `RET`)   | 28 (`PLA`/`STA` ×4)   |
| Argument ordering for the two calls | 36 (`LOCAL@` ×3, twice)      | 24 (two ZP swaps)     |
| n−1 (twice)                         | 26                           | 10                    |
| CALL/CALL/RET vs JSR/JSR/RTS        | 20                           | 18                    |
| The move (two port writes)          | 20                           | 14                    |
| **Total**                           | **136**                      | **120**               |

The structure of the result has changed from the single-cycle story. The frame
*architecture* still deletes an entire cost category — restore is 0 against 28 — and
still avoids all permutation. But every surviving category now pays the microcode tax:
a `LOCAL@` is 6 T against `lda zp`'s 3, so keeping four bytes in the right places
costs Blinky-M **64 T** per node (save + ordering) against the 65C02's **73**
(save + restore + permutation) — a narrow win where the single-cycle machine's was
7×. The call machinery proper is 20 vs 18, effectively even — except that Blinky-M's
20 *includes* the frame save/restore and the 65C02's 18 buys none of it.

## Comparison at 4 MHz (n = 8, 255 moves)

Internal nodes 255, leaf calls 256. 4 MHz T-clock assumes the AT28C64B-45 μROM grade;
the on-hand -150 parts run ~2–2.5 MHz (§7 of the design document).

| Metric                     | Blinky-M                         | 65C02                                            |
| -------------------------- | -------------------------------- | ------------------------------------------------ |
| Cycles per instruction     | 1–8 T, fixed per opcode          | 2–7, data-dependent                              |
| Cycles per internal node   | 136                              | 120                                              |
| Cycles per leaf call       | 17                               | 12                                               |
| Average cycles per move    | 153                              | 132                                              |
| Total cycles               | 39,032                           | 33,672                                           |
| **Run time @ 4 MHz**       | **9.8 ms**                       | **8.4 ms**                                       |
| Moves per second @ 4 MHz   | ~26,100                          | ~30,300                                          |
| Code size                  | 31 instructions, **56 bytes**    | ~34 instructions, 63 bytes                       |
| Recursion depth limit      | 64 levels (page-0 frames, 4 B/level; the 256-frame return stack never binds) | ~42 levels (6 bytes/level of the 256-byte stack) |
| **Relative speed**         | 0.86×                            | **1×**                                           |

## Advantages and disadvantages

**Where the Blinky-M ISA wins here:**

- **Zero restore code.** `RET` restores BP as a side effect of the frame lanes; the
  65C02's 28-cycle repair block per node has no Blinky-M counterpart. This is the
  frame architecture's surviving structural win, intact from the single-cycle design.
- **No permutation, ever.** Both children read the same four slots in whatever order
  they need. The 65C02 physically rewrites zero page twice per node and pays for it
  on every path.
- **Code density.** 56 bytes against 63 — the byte-stream encoding (1-byte stack ops,
  2-byte frame ops) beats the 65C02 despite doing the same work, and the whole
  routine is 31 instructions.
- **Determinism.** Every opcode has a fixed T-count — no page-crossing or
  branch-taken variability beyond the documented 3/5 split — so the totals above are
  exact. The panel makes it visible: RSP counts frames, so its LEDs read recursion
  depth *directly*, and the T-state row shows every microstep of every `CALL`.
- **Depth.** 64 frame levels against ~42, with the 256-frame return stack far from
  binding. (Hanoi depth 8 uses an eighth of it.)
- **Structural safety.** Absolute-only branches with full reach — no far-branch
  idiom — and every unassigned opcode or overrun T-state lands on a HALT row instead
  of executing garbage.

**Where Blinky-M loses:**

- **The microcode tax outruns the frame dividend at equal clock.** 136 T against 120
  cycles per node is a 0.86× result: each `LOCAL@`/`LOCAL!` is 6 T against `lda zp`'s
  3, and the marshalling round trip (push four, store four = 52 T per call) is paid at
  those rates. The single-cycle premise that made the frames a 4× weapon is gone; the
  frames now roughly break even against zero page and the rest of the node is simply
  slower per instruction.
- **Argument marshalling is still paid twice.** Same critique as ever, at higher
  prices. Passing arguments through the frame directly (contiguous frames make it
  legal) was evaluated and costs *more* — 6 T per `LOCAL!` exceeds the stack
  transport it would replace.
- **Wall-clock honesty.** The 4 MHz framing assumes the -45 μROM grade; the on-hand
  -150 parts run this benchmark in ~17 ms. Production W65C02s clock to 14 MHz and run
  it in 2.4 ms. Today's hardware gap is ~7×; the ratified upgrade path (-45 parts,
  then the shadow-copy loader) closes it to ~2–3×, never to parity.
- **8-bit data path.** Irrelevant to Hanoi (n = 8 is already 255 moves), but the
  65C02's indexed addressing generalizes to array-heavy workloads in ways `FETCH`'s
  zero-page reach does not.

The summary: on the workload a stack machine is supposed to lose, the frame
architecture still deletes exactly the software it was designed to delete — restore
code and operand permutation — and wins on density, depth, determinism, and safety.
What it no longer buys is speed: at equal clock the microcode tax gives the 65C02 a
1.16× edge, and the fair conclusion is that Blinky-M's frames make it *civilised*,
not fast. The machine's case rests on 56 chips of visible, deterministic,
single-EEPROM-type hardware — not on beating forty years of NMOS refinement per
cycle.
