# Blinky ISA Extension — Cheap Recursion (Improvements 1 & 3)

## Purpose

Recursion- and stack-heavy workloads (Towers of Hanoi is the canonical case) expose the one
place a pure stack machine underperforms: **operand permutation**. Blinky's `CALL`/`RET` are
already single-cycle — unbeatable by any classic 8-bit CPU, which spend 6–17 cycles on a
subroutine round trip — but the machine loses much of that lead re-ordering the live values a
recursive call needs. This document specifies two additive ISA extensions that remove the
permutation tax while preserving single-cycle `CALL`/`RET` and the 16-bit instruction format:

- **Improvement 1** — the missing single-cycle stack ops: `OVER`, `NIP`, `TUCK`, `R@`.
- **Improvement 3** — a **base pointer (`BP`)** with BP-relative locals, giving random-access
  operands without permuting the stack, with `BP` **saved and restored automatically** by
  `CALL`/`RET`.

Both are strictly additive: no instruction-word format change, and existing W=4 / W=8 images
run unaltered.

---

## The problem: the four-argument shuffle

The Hanoi kernel keeps four live values per node — `n, from, to, via` — and needs them three
times per node, in a different order each time:

```
hanoi(n, from, to, via):
    hanoi(n-1, from, via, to)     ; call A  — pegs (from, via, to)
    move from -> to
    hanoi(n-1, via, to, from)     ; call B  — pegs (via, to, from)
```

Starting from the frame's natural peg order `(from, to, via)`:

- **Call A** wants `(from, via, to)` — swap the top two: one `SWAP`.
- **Call B** wants `(via, to, from)` — reorder the *outer* two of three, for which there is no
  primitive. It becomes a `ROT`/`SWAP` dance.
- Because **call A consumes its copies**, the whole set must be duplicated *before* call A so it
  still exists for the move and call B.

The root cause is the **single-port data stack**. `OVER` reaches item 2 in one cycle (it is the
NOS register), but items 3 and deeper live in the stack SRAM, and any op that also pushes or pops
needs a second SRAM access in the same cycle — which the single port forbids. So `ROT` is a
4-cycle macro (`>R SWAP R> SWAP`) and a general deep `PICK` cannot be single-cycle at all. The
net is ~15–25 shuffle cycles per node, dwarfing the two 1-cycle calls — which is exactly what
lets a 14 MHz WDC 65C02 (random-access zero-page operands) close most of Blinky's call/return
advantage.

The fix is to **stop permuting the stack and start addressing operands** — the register machine's
model, grafted onto the stack core.

---

## Improvement 1 — OVER / NIP / TUCK / R@

Four operand-less stack ops. They live in the **I = 0 opcode space** (the stack and return-stack
families already sketched in the encoding), so the cost is four opcodes in already-reserved space:
no operand, no format change, mini-image compatibility preserved.

### Semantics and micro-operations

| Op | Stack effect | Data-stack access | Micro-op |
|---|---|---|---|
| `OVER` | `( a b -- a b a )` | one **write** | `TOS<-NOS`, `NOS<-old TOS`, spill old NOS, `DSP+1` |
| `NIP`  | `( a b -- b )`     | one **read**  | `TOS` hold, `NOS<-stack read`, `DSP-1` |
| `TUCK` | `( a b -- b a b )` | one **write** | `TOS` hold, `NOS` hold, write old **TOS**, `DSP+1` |
| `R@`   | `( -- r )`         | one **write** + return read | `TOS<-RTOS`, `NOS<-old TOS`, spill old NOS, `DSP+1` |

### Hardware cost

**`OVER` and `NIP` — zero new hardware.** Each is an existing micro-operation with a different mux
select:

- `OVER` is a normal push with `TOS_SRC = NOS`. NOS is already an input on the TOS 8:1 mux (the
  `SWAP` path), and the spilled old-NOS is exactly the duplicated copy `OVER` needs.
- `NIP` is a `DROP` with `TOS` held instead of `TOS <- NOS`. `TOS`-hold is the default '194 mode;
  `NOS` refills from the stack read.

Both cost only new product terms in the decode GALs.

**`R@` — zero or one product term.** It is a data-stack push with `TOS_SRC = RTOS` (already an
input — the `RET` path) plus a return-stack read that must **not** pop. If the return-stack SRAM
is read continuously to keep `RTOS` live on the bus (which front-panel honesty implies), `R@` is
free. If the read strobe is welded to the pointer count-enable, add one term:
`rstk_read_en = pop OR R@`, with `RSP_UD = down` addressing the live top while `RSP_EN` stays
deasserted.

**`TUCK` — the one real datapath change.** `TUCK` must write **old TOS** into the stack SRAM while
`NOS` holds, but the data-stack write port only carries NOS (the '125 buffer taps NOS). Replace
that '125 with a **quad 3-state 2:1 mux ('257 ×2) selecting NOS vs TOS** onto the stack data-in
bus, plus **one GAL output** for the select. Then `TUCK = { TOS hold, NOS hold, write old TOS,
DSP+1 }`, single-cycle. The NOS-hold path already exists (unary ops hold NOS).

**Improvement 1 total:** ≈ 1–2 ICs (the `TUCK` write-select) + 1 GAL output + a few product
terms. Four opcodes, no encoding change.

---

## Improvement 3 — Base pointer and BP-relative locals

This is the extension that actually removes the shuffle. It adds random-access, **frame-relative**
locals so a callee reads its arguments by index in any order, never permuting them — and it makes
`BP` a first-class, hardware-managed register saved and restored automatically by `CALL`/`RET`.

### Model

Locals live in **data memory** (or an optional dedicated frame RAM), addressed relative to `BP`,
the base pointer for the current call frame. Locals are deliberately **not** placed in the data
stack: a stack-resident local read would need a frame-cell read *and* a push spill in the same
cycle — two accesses on the single-port stack, which is illegal. Because data memory is a
**separate port**, `LOCAL@` (D-mem read + data-stack push) and `LOCAL!` (data-stack pop + D-mem
write) each touch two different memories once, and stay single-cycle.

`BP` marks the top of the frame region (the next free slot). `ENTER k` reserves a function's `k`
locals; `RET` restores the caller's `BP`, which frees the callee's frame in the same motion.
Because `BP` is saved on `CALL` and restored on `RET` **in hardware**, each recursion level owns
its frame and a child can never clobber a parent's locals — which is what collapses the shuffle to
zero. There is no software BP bookkeeping in the prologue/epilogue.

### Automatic save/restore — the widened call stack

`CALL` pushes and `RET` pops **`{ PC+1, BP }`** as one unit. The return-stack word therefore
widens from `{PC}` to `{PC, BP}`: at W=8 the return-stack memory becomes a 16-bit-wide pair
(one SRAM for `PC`, one for `BP`); at W=4 it holds `{PC(4), BP(4)}`. Both halves are written on
`CALL` and read on `RET` in the one existing cycle — control flow stays single-cycle (see below).
`BP` rides the return stack exactly as the return address does, which is precisely why no explicit
`FREE`/`LEAVE` op is needed: `RET` restores `BP` the same way it restores `PC`.

### New opcodes

Three, all in the **I = 1 (has-immediate) space, reusing the existing 8-bit immediate field as a
frame offset** — no format change. `CALL` and `RET` keep their existing opcodes; only their
microarchitecture grows.

| Op | Effect | Encoding |
|---|---|---|
| `LOCAL@ n` | push local `n` of the current frame -> TOS | I=1, imm = n |
| `LOCAL! n` | pop TOS -> local `n` of the current frame | I=1, imm = n |
| `ENTER k`  | reserve a `k`-slot frame (`BP += k`) | I=1, imm = k |

There is **no `FREE`**: `RET`'s automatic `BP` restore frees the frame. (Tail-call frame collapse,
if ever wanted, reuses the `ENTER` adder path with a negative constant — a `BRANCH`-based
optimization, not a core opcode.)

The exact slot layout is a **software calling convention**; the hardware supplies only the
mechanism (base pointer, BP-relative addressing, automatic save/restore, frame reserve). A typical
prologue does `ENTER k`, copies the caller's stack arguments into the frame with `LOCAL!`, then the
body reads them by index with no `ROT`; `RET` tears the frame down.

### Initialization / reset

`BP` resets to **0**, uniform with `PC`, `DSP`, and `RSP`. It is a loadable register that
async-loads zero on `/RESET` using the same load-zero idiom as `RSP` — no extra parts, no magic
constant. Power-on state is `BP = 0`, i.e. the frame region begins at the base of data memory and
grows upward, with `BP` tracking the high-water mark.

Where the frame region sits relative to globals is a **software** decision, not a hardware one. If
low data-memory addresses are reserved for globals (keeping `LOAD 0`, `STORE 1`, … cheap), the
reset/init stub lifts `BP` above them with a single `ENTER <base>` before the first `CALL` — the
same way real startup code initialises SP/FP, rather than relying on a preset-to-nonzero register.
With no globals (or globals placed high), `BP = 0` needs no stub. The pre-`CALL` top level runs as
"frame 0" at `BP = 0`; nothing is saved below it, so returning out of `main` is undefined (halt).

### Hardware cost (itemized)

| Block | Part | ICs | Role |
|---|---|---|---|
| `BP` register | loadable reg, RSP idiom | 1 | Frame top; async-loads 0 on `/RESET`; loads on `ENTER`/`RET`, holds otherwise |
| `BP` source mux | '153 ×2 | 2 | Selects `BP` input: `ENTER` adder / restored-BP / hold |
| BP-save lane | 8-bit SRAM | 1 | Return word widens `{PC}` -> `{PC, BP}`; pushed/popped with the return stack |
| `BP + imm` adder | '283 ×2 | 2 | Computes `BP + k` (ENTER) and the BP-relative local address |
| D-mem address mux | '157 -> '153 | 0 net | Widen 2:1 (IMM/TOS) -> 3:1 (IMM/TOS/`BP`-rel) |

**Net: ≈ 5–6 new ICs.**

### Single-cycle CALL / RET preserved

The `BP` machinery rides parallel to the existing PC save/restore, so control flow stays one tick:

- **`CALL`:** return-stack write of `{PC+1, BP}` (both lanes, one SRAM write) ‖ `PC` loads target ‖
  `RSP` counts up. `BP` is held (the callee opens its own frame with `ENTER`). The new BP-lane
  `/WE` is WRPH-gated exactly as the return stack is.
- **`RET`:** both lanes read at `RSP-1` (one cycle) ‖ `PC <- saved PC`, `BP <- saved BP` load in
  parallel ‖ `RSP` counts down. The caller's frame is implicitly restored because `BP` now points
  back at it.

---

## The binding constraint: control-signal budget

The datapath additions are easy; the **decode pins are tight**. Improvement 3 needs roughly 3–4
new GAL outputs — `BP_LOAD`, `BP_SRC`, the third D-mem-address select, and the BP-relative
read/write strobes. The machine already runs ~29 direct GAL outputs across four 16V8s (32 total),
leaving only ~3 spare. So this extension likely requires a **fifth GAL16V8** to host the `BP`
control, or reclaiming outputs by folding derived strobes into glue. This — not the adders or
muxes — is the real cost ceiling.

---

## Net effect

| | Opcodes added | Instruction word | New silicon | Mini-image compat |
|---|---|---|---|---|
| **Improvement 1** | 4 (I=0) | unchanged | ≈ 1–2 IC + 1 GAL pin | preserved |
| **Improvement 3** | 3 (I=1) | unchanged | ≈ 5–6 IC (+ likely a 5th GAL) | preserved |

Neither touches the 16-bit format: Improvement 1 consumes reserved operand-less opcodes,
Improvement 3 reinterprets the existing immediate field as a frame offset. Both are additive, so a
W=4 or earlier W=8 image executes unchanged.

---

## Performance impact — Hanoi

Cycles per internal recursion node, Blinky at 10 MHz (100 ns/cycle) vs WDC 65C02 at 14 MHz
(held at ~65 cyc/node, ~4.6 µs — fixed silicon, cannot answer):

| Version | Blinky cyc/node | vs 65C02 |
|---|---|---|
| Baseline stack ISA | ~30 (3.0 µs) | ~1.5× |
| + Improvement 1 | ~26 | ~1.8× |
| **+ Improvements 1 & 3** | **~18 (1.8 µs)** | **~2.6×** |
| + Improvements 1, 2 & 3 | ~17 | ~2.7× |

Improvement 3 is the load-bearing change: it nests the arguments (per-level frames, no
save/restore across a call), collapsing the ~20 shuffle cycles to ~0. Improvement 1 mops up
residual TOS/NOS juggling. Dropping the flat register file (Improvement 2) costs ~1 cycle/node
because 3 makes it largely redundant — 3's frame slots already provide random-access operands.

The weighted result is a bit better than 2.6× in practice: roughly half of Hanoi's nodes are
leaves (base case = return only), where Blinky pays 2 cycles against the 65C02's ~12 (JSR+RTS), a
6× edge that pulls the average toward ~3×. Before these extensions Blinky won call/return (~4×) but
lost the shuffle, netting ~1.5×; afterward it **wins both halves**.

---

## Scope notes — deliberately excluded

- **`FREE` / `LEAVE`.** Unnecessary — `RET` restores `BP` from the widened call stack, which frees
  the frame. `FREE` would only matter for tearing a frame down *without* returning through it
  (mid-function scratch scopes, tail-call collapse); those are rare and reuse the `ENTER` adder
  path. Keeping it would cost an opcode slot and a decode term for a case `RET` already handles.
- **Improvement 2 (flat `L0–L3` register file, e.g. '670 ×2).** Redundant once frame locals exist;
  it earns its keep only for hot **non-recursive** inner loops (checksums, scanners, pixel math)
  where a frame slot's memory-cycle cost is pure overhead. Its opcodes are additive, so it can be
  added later without repainting the ISA — defer it unless a real hot loop wants registers.
- **`PICK` / `ROLL`.** The single-port stack makes any deep `PICK (>=2)` multi-cycle, so it does
  not deliver single-cycle random access; frame locals do the job better.
- **Dual-port stack SRAM.** Would make `ROT`/`PICK` single-cycle generally, but it is a large
  chip-count hit against the minimal-chip ethos; the frame-local file captures ~90% of the Hanoi
  win for a fraction of the parts.

---

## Architectural note

`LOCAL@ n` *is* the register machine's random-access operand fetch grafted onto the stack core.
The shuffle disappears because the machine stops permuting the stack and starts addressing a frame
— which is exactly **Thumby's** operand model. On recursion-heavy or many-live-local workloads,
these extensions make Blinky win precisely by making it more register-like for operands while
keeping the stack for control flow. If such workloads are a primary target, that is a signal worth
weighing: adopt the hybrid on Blinky, or treat it as confirmation that Thumby's register model was
the right choice for that machine.
