# Mini Blinky CPU — Design Summary (W = 4)

A half-scale instance of the full TTL Blinky CPU. Same architecture, same programmer model, narrower buses. The mini exists to be **functionally equivalent to the future 8-bit machine** — not to be useful in its own right. Every datapath and control path in the full design is exercised by the mini; the only thing that changes between them is a width knob.

## Relationship to the Full Blinky — the `W` Parameter

Define **`W`** = the machine word width (data bus, ALU, stack, address). The mini is `W = 4`; the real machine is `W = 8`. The full design (`Blinky_TTL_CPU.md`) is the `W = 8` instance of the same architecture.

**Equivalence lives at the programmer-model level, not the bit-encoding level.** A program written and reasoned about on `W = 4` transfers to `W = 8` unchanged in *behavior* — same operations, same stack discipline, same flags, same control-flow semantics. The *bit pattern* of each instruction differs (the literal field is wider, opcodes may be renumbered), so the assembler retargets `W = 4` → `W = 8`. What does **not** change is how a programmer thinks about the machine.

The load-bearing invariant that makes this work:

> **Literal field width = `W`.**

That single rule is why `PUSH #imm`, `BRANCH addr`, and `CALL addr` are all single-word, single-cycle, full-reach on *both* machines — the literal is exactly as wide as the bus it feeds, so no high/low-byte sequence is ever needed.

### What scales with `W` vs. what is fixed

| Scales with `W` | Fixed regardless of `W` |
|---|---|
| Data bus / ALU width | The instruction *set* (same ops both machines) |
| D-memory address → 2^W cells | Three flags: N, Z, C |
| PC / I-memory address → 2^W instructions | ΔDSP ∈ {+1, 0, −1} per cycle |
| I/O port width | Single-port stack SRAM (one access per cycle) |
| TOS, NOS, data-stack width | Harvard, dual-stack structure |
| Both stack pointers → 2^W deep | Single-cycle: one tick = one instruction |
| Return-stack width (holds a PC) | Absolute branches and returns |
| **Literal / immediate field width (= W)** | |
| ALU = ⌈W/4⌉ × '181 (mini: 1, real: 2) | |
| TOS shift register = ⌈W/4⌉ × '194 (mini: 1, real: 2) | |

## Design Philosophy (inherited from the full Blinky)

- **Single-cycle execution** — one clock tick = one instruction, no exceptions.
- **Front-panel honesty** — every visible LED reflects real machine state; single-stepping shows complete, settled states.
- **Stack machine, not register machine** — operands are implicit (ALU A = TOS, B = NOS, result → TOS). No operand routing, no register-file decode.
- **Minimum chip count without architectural compromise.**

## `W = 4` Parameters

| Quantity | Value |
|---|---|
| Data path / ALU | 4-bit (one '181) |
| Program (I-memory) | 16 instructions |
| Data memory | 16 cells × 4-bit |
| Data stack depth | 16 |
| Return stack depth | 16 |
| Literal / immediate | 4-bit (full data range, full code reach) |
| Flags | N, Z, C |
| Instruction word | **8-bit** (see below) |

The 16-instruction program ceiling is the accepted price of being a faithful half-scale model: the `W = 8` machine tops out at 256 instructions *the same way*, because the PC width equals the literal width equals `W` on both. We do **not** widen the PC past the literal field — doing so would force multi-instruction address loads and break single-cycle execution.

## Instruction Format

Fixed **8-bit** instruction: a 4-bit opcode plus a 4-bit literal. The literal is **operand OR address depending on the opcode — never both**. Because the opcode alone determines the literal's role, no "immediate flag" bit is needed; decode is purely positional.

```
 7   6   5   4 | 3   2   1   0
+---------------+---------------+
|    opcode     |    literal    |
+---------------+---------------+
                 ↑ operand (data/op path) OR address (address mux), per opcode
```

This is the full Blinky's "low field is either an immediate or an address per opcode," given a fixed home and stripped of the parent's I-bit (the mini has opcode room to spare, so it spends an opcode per *form* instead of an encoding bit per *instruction*).

Literal role by opcode:

- **operand role** (literal → data / op-select path): `SYS` (sub-op), `ALU` (op-select), `PUSH` (immediate value)
- **address role** (literal → address mux): `LOAD`, `STORE`, `IN`, `BRANCH`, `BEQ`, `BCS`, `BMI`, `CALL`
- **unused**: `OUT` (stack-form; port comes from TOS, literal is don't-care)

## Top-Level Opcode Map

12 live opcodes, 4 reserved.

| Opcode | Mnemonic | Literal role | ΔDSP | Notes |
|---|---|---|---|---|
| 0 | `SYS` | operand (sub-op) | varies | operand-less family — see SYS table |
| 1 | `ALU` | operand (op-select) | −1 / 0 | TOS,NOS → TOS; sets N/Z/C; GAL expands the select |
| 2 | `PUSH #imm` | operand (immediate) | +1 | literal → TOS |
| 3 | `LOAD #addr` | address | +1 | push D-mem[addr] |
| 4 | `STORE #addr` | address | −1 | pop → D-mem[addr] |
| 5 | `IN #port` | address | +1 | immediate-form port read → push |
| 6 | `OUT` | — (port from TOS) | −1 | stack-form `( data port -- data )`; exercises the address-source mux |
| 7 | `BRANCH addr` | address | 0 | PC ← addr |
| 8 | `BEQ addr` | address | 0 | branch if Z |
| 9 | `BCS addr` | address | 0 | branch if C |
| 10 | `BMI addr` | address | 0 | branch if N (top bit set) |
| 11 | `CALL addr` | address | 0 | push absolute PC+1 to return stack, PC ← addr |
| 12–15 | — | — | — | reserved (indirect CALL / extension) |

`IN` is the immediate form and `OUT` is the stack form **on purpose**: together they cover the immediate-literal address path *and* the TOS-vs-IR address-source mux using two opcodes instead of four. The full Blinky offers both forms of each; the mini carries one of each because that is sufficient to exercise both mechanisms.

## SYS Sub-Ops (opcode 0)

The operand nibble selects one of the operand-less family. A small decode (one enable off `opcode == SYS`) expands it. This grouping is what lets a 4-bit opcode carry the full shared instruction set without cutting operations.

| Sub-op | ΔDSP (data) | Mechanism exercised |
|---|---|---|
| `NOP` | 0 | null single-cycle path |
| `HALT` | 0 | clock stop |
| `RET` | 0 | PC ← RTOS, RSP −1 |
| `DUP` | +1 | push sourced from TOS latch; stack-mem write at DSP+1 |
| `DROP` | −1 | pop; stack-mem read into NOS at DSP−1 |
| `SWAP` | 0 | TOS ↔ NOS cross path |
| `>R` | −1 (return +1) | data → return stack |
| `R>` | +1 (return −1) | return → data stack |

`ROT` is **not** encoded — it is not a single-cycle operation on a single-port stack. Synthesize it as `>R SWAP R> SWAP` on both machines.

## ALU Sub-Ops (opcode 1)

The operand nibble is a **curated op-select**, not the raw '181 function lines. The '181 needs ~6 control bits (S0–S3, mode M, carry-in source) plus this machine's per-op ΔDSP and result-write-enable. Two of the selects (`SHL`, `SHR`) do not drive the '181 at all — they drive the '194 shift-register TOS instead — so the GAL also carries the TOS mode and the flag-C source. The decode GAL (ATF22V10) expands the 4-bit select into:

```
operand nibble  →  { S0, S1, S2, S3, M, Cn-source, ΔDSP, write-enable,
                     TOS-mode (load / shift-L / shift-R),
                     C-source ('181 carry / shifted-out bit) }
```

Same GAL on both machines; the mini simply populates fewer table rows. "Pick fewer ALU ops" therefore means "fewer GAL rows," nothing structural.

Representative subset — chosen so each op is the *only* thing that lights a given mechanism:

| Sub-op | ΔDSP | Mechanism it uniquely exercises |
|---|---|---|
| `ADD` | −1 | binary arithmetic, M = 0, full carry chain, all three flags |
| `ADC` | −1 | carry-*in* mux (Cn ← C) — the path narrow `W` uses to build wide `W` arithmetic |
| `SUB` | −1 | '181 subtract (one's-complement B + Cn); borrow polarity on the cascade |
| `XOR` | −1 | logic mode M = 1 — the *other* mode; bit-toggle, the blinky workhorse |
| `NOT` | 0 | unary path: one operand, no NOS read |
| `CMP` | −1 | flags latch while result write is suppressed — proves write-enable is independent of flag-write |
| `SHL` | 0 | the '194 shift-register TOS — a left single-bit shift the '181 cannot perform; C ← old top bit |
| `SHR` | 0 | the *only* op that shifts toward the LSB; lights the '194's right-shift path; C ← old bottom bit |

`OR`, `AND`, and the remaining '181 functions light no new path and are omitted from the mini; they cost only GAL rows to add later and do not affect equivalence. `SHL`/`SHR` are kept for the opposite reason — they are the *only* ops that light the '194 shift-register TOS, a datapath the '181 never touches, so without them the mini fails to exercise the full machine's shifter at all. Rotates (`ROL`/`ROR`) are absent on **both** machines: the full Blinky's shift unit is logical single-bit only, so the mini matches it (an op the mini *uses* must also exist on the full machine, or programs stop transferring).

## Flags

Three flags, written **only** by the `ALU` class. Stack, memory, I/O, and control instructions leave them untouched, so a `CMP` or arithmetic op can be separated from its consuming branch by any number of non-ALU instructions.

| Flag | Definition | Used by |
|---|---|---|
| N | bit `(W−1)` of the result (top bit) | `BMI` |
| Z | result is all-zero | `BEQ` |
| C | arithmetic: carry / borrow out of the top bit. shift: the bit shifted out (old top bit on `SHL`, old bottom bit on `SHR`) | `BCS`, `ADC`, `SUB` |

N = bit `(W−1)` scales `W = 4` → `W = 8` with no logic change. `BMI` makes all three flag lines feed the flag→branch mux and gives the N LED real work — e.g. a Larson scanner shifts a lit bit upward and `BMI` is the cheap "the light hit the top" test, no compare-against-constant needed. For `SHL`/`SHR` the C flip-flop is fed from the bit shifted out of the '194 rather than the '181 carry chain (a one-bit C-source mux the GAL selects); N and Z are taken from the shifted result exactly as for any other ALU op.

## Stack Discipline

Every instruction changes the data stack pointer by exactly **+1, 0, or −1**. No instruction shifts the stack by two in one cycle. This keeps DSP a single up/down counter, keeps stack memory single-port (one read *or* one write per cycle), and keeps the TOS/NOS input muxes small. Identical on both machines.

#### TOS / NOS update rules

| Operation | TOS ← | NOS ← | Stack memory |
|---|---|---|---|
| Push (+1) | new value (immediate / fetched / ALU) | old TOS | write old NOS at DSP+1 |
| Unary / shift (0) | ALU or '194-shifted result of old TOS | unchanged | idle |
| `SWAP` (0) | old NOS | old TOS | idle |
| Binary ALU (−1) | ALU result of (old TOS, old NOS) | stack[DSP−1] read | read only |
| `DROP` (−1) | old NOS | stack[DSP−1] read | read only |
| stack-form `OUT` (−1) | old NOS | stack[DSP−1] read | I/O write of old NOS |

#### Stack-form `OUT` stays at −1

Conventional Forth `OUT`/`STORE` is `( data addr -- )` — a −2 operation that would need dual-port stack memory and DSP-by-2 logic. Instead, stack-form `OUT` consumes **only the port** (TOS) and writes **NOS** to it, leaving the data value on the stack:

> `OUT` (stack form): `( data port -- data )`. Writes NOS to the port at TOS, pops TOS.

Code that wants the value gone follows with `DROP`; code reusing it (e.g. writing the same byte to several ports) does not. This is how the same value gets written to a row of output ports in a loop without re-pushing it each iteration. Same idiom on both machines.

## CALL / RET Semantics (absolute)

- **`CALL addr`**: push the **absolute** return address (PC+1) onto the return stack, RSP +1, then PC ← addr. PC+1 is the pre-incremented PC value, available on the counter outputs before the next clock edge — no adder needed.
- **`RET`**: PC ← RTOS (absolute, no increment), RSP −1.

Because all control flow is absolute, there is no relative/return interaction to reason about: `RET` reaches the entire code space regardless of anything else. The return stack is `W`-wide (it holds a PC) and physically separate from the `W`-wide data stack. Widening `W = 4` → `W = 8` just makes RTOS and its LEDs wider; no logic changes. This is the same shape on both machines — which is the whole point.

## Architecturally Visible State

| Element | Width | Purpose |
|---|---|---|
| PC | `W` | program counter (I-memory address) |
| IR | 8 (mini) | instruction register |
| TOS | `W` | top of data stack (a '194 shift register: parallel-loads on writes, shifts on `SHL`/`SHR`) |
| NOS | `W` | next on data stack |
| DSP | `W` | data stack pointer |
| RTOS | `W` | top of return stack (return address) |
| RSP | `W` | return stack pointer |
| N / Z / C | 1 each | flags, set by ALU ops only |

## Memory & I/O

- **Harvard.** Separate instruction and data memories on separate buses; instruction fetch happens in parallel with data access, enabling single-cycle execution.
- **I-memory:** 16 instructions × 8-bit word → one 8-bit memory chip (only 4 address lines used).
- **D-memory:** 16 cells × 4-bit.
- **Two stacks:** data and return, each 16 deep × `W`, single-port SRAM, free-running modulo-16 pointers (no overflow detection — wraparound is a visible front-panel diagnostic, same philosophy as the full machine).
- **I/O:** separate I/O address space (Z80-style), `W`-bit ports. `IN #port` (immediate form) and `OUT` (stack form, port from TOS) between them exercise both the immediate address path and the TOS-vs-IR address-source mux.

## Scaling to `W = 8`: Instruction Word Width

Three different "widths" are in play; they do not scale together.

**1. Architecturally required width = opcode + literal.** Only the literal is pinned to `W`. Opcode width is free — set by opcode *count*, not by `W` — and the curated set fits in 4 bits at both scales.

| | opcode | literal (= W) | required |
|---|---|---|---|
| `W = 4` (mini) | 4 | 4 | **8-bit** |
| `W = 8` (real) | 4 | 8 | **12-bit** |

Pure scaling grows the instruction by exactly the literal growth (+4 bits): **12 bits at `W = 8`**, not 16.

**2. Physical word width = memory granularity.** Memory chips are 8 bits wide:

- `W = 4`: 8-bit instruction → **one** 8-bit chip. Exact fit, zero waste.
- `W = 8`: 12 bits needed → **two** 8-bit chips → physical word is **16 bits**, ~4 bits spare. You cannot buy a 12-bit-wide memory, so the word *is* 16 whether or not all of it is used. (The full Blinky confirms this: 16-bit I-bus, two 8-bit EEPROMs.)

So **yes — `W = 8` is a 16-bit instruction word**, but for a packaging reason, not an architectural one. The architecture needs 12; the memory hands you 16.

**3. The spare 4 bits — the one place mini and full machine diverge by design.**

- **Mini, scaled faithfully:** keep the 4-bit opcode, reserve the top 4 bits. 12 used, 4 dead. Maximally equivalent to `W = 4`.
- **Full Blinky:** *spends* those bits — its encoding is I-bit + 7-bit opcode + 8-bit literal. That is an enhancement the free headroom permits (larger opcode space, explicit immediate flag), not a requirement of equivalence.

Both are correct because equivalence is at the programmer-model level, not the bit encoding. The assembler retargets opcodes `W = 4` → `W = 8`; the program's behavior transfers unchanged even though its bit pattern does not.

## What the Mini Exercises

The subset is chosen to cover *mechanisms*, not operations — each instruction earns its slot only by being the sole thing that lights a given datapath or control path:

- **ALU:** both '181 modes (arithmetic via ADD/SUB, logic via XOR), full carry chain, carry-*in* mux (ADC — the path that lets narrow `W` emulate wide `W`), subtract/borrow polarity (SUB), unary path (NOT), flag-only with suppressed write (CMP).
- **Shifter:** the '194 bidirectional shift-register TOS — a datapath the '181 never drives — in both directions (`SHL` left, `SHR` right), with C sourced from the bit shifted out rather than from the carry chain.
- **Stack:** push from literal (PUSH) and from TOS latch (DUP), pop with stack-mem read (DROP), TOS↔NOS cross (SWAP), inter-stack path (>R / R>, which also synthesizes ROT).
- **Memory / I/O:** IR-sourced address (LOAD / STORE / IN), TOS-sourced address via the address mux (stack-form OUT), and the `( data port -- data )` −1 idiom.
- **Control:** unconditional branch (BRANCH), all three flag→branch paths (BEQ/BCS/BMI), return-stack write and absolute return (CALL / RET), null and clock-stop (NOP / HALT).

## Front Panel

Every architectural element gets LEDs, scaled to `W = 4`:

- **PC (4 LEDs)** — position in program.
- **IR (8 LEDs)** — current instruction, fully visible.
- **TOS (4 LEDs)** — top of data stack, the "accumulator" in spirit.
- **NOS (4 LEDs)** — next on stack.
- **DSP (4 LEDs)** — data stack depth.
- **RTOS (4 LEDs)** — top of return stack (where we'll return to).
- **RSP (4 LEDs)** — return stack depth.
- **Flag LEDs (3)** — N, Z, C, set by every ALU op.

Narrower fields are *easier* to read at a glance than 8-bit ones — arguably a better blinky for teaching the architecture.

## Chip Count Sketch (core, `W = 4`)

| Block | Chips |
|---|---|
| ALU (1× '181) | 1 |
| TOS shift register (1× '194) + NOS latch | 2 |
| Data stack memory (16-deep SRAM) + pointer (1× '169) | 2 |
| Return stack memory + pointer (1× SRAM + 1× '169) | 2 |
| PC (1× '161) | 1 |
| Instruction register (8-bit latch) | 1 |
| Decode / control (ATF22V10 GAL + glue) | 1–2 |
| Address-source mux (1× '157) | 1 |
| I-memory (1× 8-bit, 16 deep) | 1 |
| D-memory (1× SRAM) | 1 |
| Clock / reset / single-step | 2 |
| **Core total** | **~14–16** |

Roughly half the full machine's core, with the same block diagram — every block present, just narrower. The TOS is a '194 (bidirectional single-bit shift register), not a plain latch: it parallel-loads for every ordinary TOS write and shifts for `SHL`/`SHR`, so the shifter mechanism is present at no chip cost beyond the latch it replaces (one '194 at `W = 4`, a '194 pair at `W = 8`).

## Open Decisions / Notes

- **Reserved opcodes 12–15.** Candidates if needed later: indirect `CALL` (target from a small address register loaded by nibble pushes) for reach beyond the 16-instruction direct space; a second ALU bank; an immediate-form `STORE`/`IN`/`OUT` to mirror more of the full machine. None required for `W` equivalence.
- **Branch set is a shared decision.** The mini carries BEQ/BCS/BMI (one per flag). If the full machine keeps all six 6502-style conditionals (adding BNE/BCC/BPL), that is a superset the mini can ignore; but any branch the mini *uses* must also exist on the full machine, or programs stop transferring. Keep the shared set at three unless the full design is updated to match.
- **`ROT`** is a macro on both machines — never a single opcode.
- **Rotates (`ROL`/`ROR`)** are intentionally absent — the shift unit is logical single-bit on both machines. If the full Blinky later adds rotate-through-carry, the mini gains matching ops at the cost of one GAL change plus a serial-fill mux (0 vs old C) feeding the '194; until then neither machine has them, so programs transfer unchanged.
- **`SYS` is the encoding mechanism**, not a convenience. Dropping it would force cutting instructions to fit a flat 4-bit opcode, which is exactly the divergence equivalence forbids.
