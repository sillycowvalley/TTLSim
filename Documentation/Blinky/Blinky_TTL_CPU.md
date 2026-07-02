# TTL Blinky CPU — Master Reference (W = 8)

> **This is the single authoritative Blinky (W = 8) document.** It defines the current state of the architecture: super-op encoding with a 13-bit address space, relative conditional branches, the SRAM-backed return stack with hardware base-pointer call frames, the interrupt subsystem (NMI / IRQ / BRK), and an all-22V10 control complement.

A minimal-but-honest TTL CPU aimed at "blinking lights" aesthetics: every LED on the front panel means something, every clock tick is one instruction, and the architecture is
interesting enough to be more than a toy.

## Design Philosophy

- **Single-cycle execution** — one clock tick = one instruction, no exceptions. Interrupt
  entry is one inserted cycle; return from interrupt is one cycle.
- **Front-panel honesty** — every visible LED reflects real machine state in real time.
  Single-stepping shows complete, settled states. Switchable to MHz clock though.
- **Stack machine, not register machine** — operands are implicit, control logic stays
  small, the front panel becomes more informative.

## Top-Level Architecture

**Stack machine, Harvard memory, dual data/return stacks, base-pointer call frames,
vectored interrupts.**

| Bus / Width         | Size                       | Notes                                                        |
| ------------------- | -------------------------- | ------------------------------------------------------------ |
| Data bus (D)        | 8 bits                     | ALU and data width (`W = 8`)                                 |
| Instruction bus (I) | 16 bits                    | Fixed-width instructions, single-cycle fetch                 |
| I-address bus (PC)  | 13 bits                    | 8K instructions                                              |
| D-address bus       | 13 bits                    | 8K data cells                                                |
| I/O port address    | 8 bits                     | Z80-style separate space, 256 ports                          |
| Data stack          | 8 bits wide, 256 deep      | TOS + NOS in registers, rest in stack SRAM                   |
| Return stack        | 2 × 16-bit lanes, 256 deep | `{PC, N, Z, C}` lane + `{BP, I, B}` lane — see §Return Stack |
| BP (base pointer)   | 13 bits                    | Frame top; saved/restored by CALL/RET in hardware            |

Four memories total: instruction memory, data RAM, data stack, return stack. Each on its own bus — all four can be accessed in the same clock cycle. That's the architectural
luxury that makes single-cycle execution feasible on TTL.

## Why a Stack Machine

On a register machine, an instruction must encode source and destination registers — those bits come out of the opcode budget. On a stack machine, operands are implicit: ALU input A is always TOS, input B is always NOS, result goes to TOS. No operand routing, no register file decode, no mux trees feeding the ALU.

- **Simpler ALU plumbing.** ALU inputs are wired directly to TOS and NOS. No operand mux.
- **More opcode space.** With 16-bit instructions and no register fields, the entire
  encoding is available for opcode and operand.
- **Better front panel.** TOS and NOS LEDs show live computation. Stack depth LEDs show program state.
- **Forth-friendly.** A small Forth interpreter is the natural software stack.

Historical precedents: Novix NC4016, MuP21, F21, Burroughs B5000 (in spirit).

The one place a pure stack machine underperforms — operand permutation in recursion-heavy code — is addressed by the **BP frame-local subsystem** (§Call Frames), which grafts register-machine-style random-access operands onto the stack core without touching the single-port stack.

## Why Harvard

Separate instruction and data buses eliminate fetch/execute contention. On a TTL machine with separate ROM and RAM chips, Harvard is essentially free.

- No bus arbitration logic; fetch parallel with data access.
- Independent address spaces (8K code AND 8K data).
- Enables true single-cycle execution.

Cost: can't execute code from RAM — irrelevant here; programs live in EEPROM.

## Why 16-Bit Fixed Instructions

The architecture needs a 4-bit-class opcode plus operand; 8-bit-wide memory chips set the physical instruction word at 16 bits. The super-op encoding **spends** the full word:
variable *operand* width inside a fixed *instruction* width.

- **Single-cycle execution.** Every instruction is one 16-bit word, one tick.
- **No fetch state machine.** Latch memory output on the clock edge, decode
  combinationally, done.
- **Full address reach in one instruction.** `JUMP`, `CALL`, `LOAD`, `STORE` carry a
  13-bit absolute address — single-word, single-cycle, across the whole 8K space. No
  high-byte/low-byte sequences, ever.

---

# 1. Instruction Encoding — the Super-Op Format

Bit 15 (**I**) is the **format selector**.

## I = 1 — Super-ops (wide operand)

Exactly four instructions get the wide format: the ones that need full address reach.

```
 15  14  13 | 12  11  10   9   8   7   6   5   4   3   2   1   0
+---+-------+---------------------------------------------------+
| 1 |  op   |              address (13 bits)                    |
+---+-------+---------------------------------------------------+
```

| I=1 op | Bits 14:13 | Mnemonic     | Effect                                             |
| ------ | ---------- | ------------ | -------------------------------------------------- |
| 0      | 00         | `JUMP addr`  | PC ← addr (unconditional, absolute, full 8K reach) |
| 1      | 01         | `CALL addr`  | push `{PC+1, BP}` to return stack; PC ← addr       |
| 2      | 10         | `LOAD addr`  | push D-mem[addr] `( -- m )`, ΔDSP +1               |
| 3      | 11         | `STORE addr` | pop TOS → D-mem[addr] `( a -- )`, ΔDSP −1          |

## I = 0 — Normal ops (8-bit operand)

```
 15  14  13  12  11  10   9   8 | 7   6   5   4   3   2   1   0
+---+---------------------------+-------------------------------+
| 0 |       opcode (7 bits)     |     operand / offset (8b)     |
+---+---------------------------+-------------------------------+
```

Everything else lives here: ALU ops (sub-op nibble in operand[3:0]), stack and
return-stack ops, `PUSH #imm`, `IN`/`OUT`, the relative conditional branches, the frame
ops `LOCAL@`/`LOCAL!`/`ENTER`, the interrupt ops `BRK`/`RTI`/`SEI`/`CLI`, and
`RET`/`NOP`/`HALT`. Operand-less instructions ignore the operand field — except the ALU family, where operand[3:0] is the sub-op select and bits L3/L2 double as the shifter
fill-mux select (§ALU).

The wide address reach lives only in the four super-ops; PC, BP, and the D-address bus
are 13 bits while the data path stays 8.

## Relative conditional branches

The six 6502-style conditionals plus a short unconditional are **PC-relative** with a
signed 8-bit offset:

- **Target = PC + 1 + offset**, offset ∈ −128…+127. Offset 0 falls through.
- Reach is ±~127 instructions around the branch — ample for loop bodies and local
  control flow.
- **Far conditionals** use the classic inverse-branch idiom: invert the condition and
  hop over a `JUMP`:

```
        ; BEQ far_target, out of relative reach:
        BNE +1          ; skip the JUMP when Z clear
        JUMP far_target ; full 13-bit reach
```

All six conditions have their inverse in the set (BEQ/BNE, BCS/BCC, BMI/BPL), so every
far conditional is exactly two instructions.

`BRA offset` (unconditional relative) exists alongside `JUMP addr`: it costs one I=0
opcode, makes short forward/backward hops relocatable, and keeps loop code independent of
load address.

---

# 2. Instruction Set

## I = 1 super-ops

Covered above: `JUMP`, `CALL`, `LOAD`, `STORE`. Note **writes are immediate-form only**
(`STORE addr`, `OUT #port`) — see §Stack Discipline.

## I = 0 families

| Family              | Instructions                                                        | Operand field                        | Flags                                                       | Notes                                                                                                                                                   |
| ------------------- | ------------------------------------------------------------------- | ------------------------------------ | ----------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ALU                 | ADD, ADC, SUB, XOR, NOT, TST, SHL, SHR, AND, CMP, ASR, OR, ROL, ROR | sub-op nibble (§ALU)      | N/Z every op; C only arith/shift/CMP — logic ops preserve C | TOS,NOS → TOS. ADC reads C. TST/CMP set flags without writing TOS. SBC and NEG unallocated (nibbles A/D reserved).                                      |
| Stack               | DUP, DROP, SWAP, OVER, NIP, TUCK                                    | —                                    | none                                                        | ROT is a macro (`>R SWAP R> SWAP`), never an opcode — not single-cycle on a single-port stack.                                                          |
| Return stack        | >R, R>, R@                                                          | —                                    | none                                                        | Move 8-bit values between stacks; see §Return Stack for lane semantics.                                                                                 |
| Frame               | LOCAL@ off, LOCAL! off, ENTER k                                     | signed 8-bit offset / unsigned count | none                                                        | BP-relative locals; §Call Frames.                                                                                                                       |
| Memory (stack form) | FETCH                                                               | — (addr = TOS)                       | none                                                        | `( addr -- value )`, ΔDSP 0. **Reach: low 256 D-mem cells** (TOS is 8 bits, zero-extended onto the 13-bit D-address bus). There is no stack-form STORE. |
| I/O                 | IN (stack form), IN #port, OUT #port                                | port (immediate forms)               | none                                                        | 8-bit port space. There is no stack-form OUT.                                                                                                           |
| Branch (relative)   | BRA, BEQ, BNE, BCC, BCS, BMI, BPL                                   | signed 8-bit offset                  | none                                                        | Target = PC+1+offset.                                                                                                                                   |
| Interrupt           | BRK, RTI, SEI, CLI                                                  | —                                    | RTI restores N/Z/C/I; SEI/CLI write I                       | §Interrupts.                                                                                                                                            |
| Control             | RET, NOP, HALT                                                      | —                                    | none                                                        | RET pops `{PC, BP}` — restores both, leaves flags alone.                                                                                                |
| Immediate           | PUSH #imm                                                           | 8-bit immediate                      | none                                                        | Full 8-bit data range in one word.                                                                                                                      |

Reads keep both forms: stack-form `FETCH` and `IN` take a run-time-computed address/port from TOS (genuinely ΔDSP = 0 — the value read replaces the address), and the immediate/super-op forms are denser for fixed accesses. **Writes are immediate-form only** (`STORE addr`, `OUT #port`): a clean stack-form write `( data addr -- )` is a forbidden −2 on the single-port stack, and a −1 compromise leaves a dangling operand on every write.
Run-time-computed write targets are unrolled to fixed addresses/ports, mapped to fixed ports, or handled through frame locals.

## CALL / RET semantics

- **`CALL addr`:** push `{PC+1, BP}` onto the return stack as one unit (both lanes, one
  write cycle); PC ← addr; RSP counts up. BP itself is **held** — the callee opens its
  own frame with `ENTER`.
- **`RET`:** read both lanes at RSP−1; PC ← saved PC, BP ← saved BP, loading in
  parallel; RSP counts down. Restoring BP frees the callee's frame in the same motion — there is no `FREE`/`LEAVE` opcode, deliberately. RET ignores the flag bits in the lanes; only RTI restores flags.

PC+1 is produced by the PC adder every cycle regardless (§Sequencing), so a CALL needs no extra arithmetic for the return address.

---

# 3. Programmer's Model

| Element   | Width  | Purpose                                                                                                                 |
| --------- | ------ | ----------------------------------------------------------------------------------------------------------------------- |
| PC        | 13     | Program counter (register + adder)                                                                                      |
| IR        | 16     | Instruction register                                                                                                    |
| TOS       | 8      | Top of data stack (2× '194: parallel-loads on writes, shifts on SHL/SHR/ASR/ROL/ROR)                                    |
| NOS       | 8      | Next on data stack ('374-class latch)                                                                                   |
| DSP       | 8      | Data-stack pointer (256 deep)                                                                                           |
| RTOS      | 13     | Top of return stack, PC lane (where we'll return to)                                                                    |
| RSP       | 8      | Return-stack pointer (256 deep)                                                                                         |
| BP        | 13     | Base pointer — top of the current call frame; hardware-saved/restored by CALL/RET                                       |
| N / Z / C | 1 each | ALU flags, written only by the ALU class (per-flag enables); restored by RTI                                            |
| I         | 1      | Interrupt mask: 1 = IRQ masked. Set on reset and on interrupt entry; SEI/CLI write it; RTI restores it. NMI ignores it. |

The B flag (BRK-vs-hardware discriminator) is not a register — it exists only in the
pushed return word, 6502-style (§Interrupts).

TOS and NOS in dedicated registers (not in stack memory) is non-negotiable — it lets ALU ops complete in one cycle without a stack-memory access.

## ALU flags (per-flag write enables)

N/Z/C are written **only** by the ALU class (and RTI) — stack, memory, I/O, frame, and
control instructions leave the flags alone, so a TST/CMP/arithmetic op can be separated
from its consuming branch by any number of non-ALU instructions. Within the ALU class the write is **per-flag**: N/Z latch on every live ALU sub-op (`NZ_WE`); C latches only
on the ops that define it — ADD/ADC/SUB/CMP (carry/borrow out of bit 7) and the five
shifts (the shifted-out bit) — via its own enable, `C_WE`. The logic ops (XOR, NOT, TST,
AND, OR) **preserve C**, so a carry or shifted-out bit survives intervening masking ops
until its consuming branch.

| Flag | Definition                                                                                                                                                         | Used by        |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------- |
| N    | bit 7 of the result                                                                                                                                                | BMI / BPL      |
| Z    | result is all-zero ('181 A=B, wire-ANDed across the pair)                                                                                                          | BEQ / BNE      |
| C    | arith/CMP: carry/borrow out of bit 7 (high '181 Cn+4, latched directly). Shifts: the bit shifted out (old bit 7 leftward, old bit 0 rightward). Held by logic ops. | BCS / BCC, ADC |

Cost: ~1.5 packages ('74s), the C flip-flop's enable on the `C_WE` net, plus one '157 on
the flag inputs for the RTI restore path (§Interrupts). "Carry set after SUB = no
borrow," same as the 6502.

---

# 4. ALU

2× '181 cascaded for 8 bits, active-high-operand convention. The sub-op nibble lives in
IR operand bits [3:0]:

| Nib | Op  | Class                 | Nib | Op                | Class                 |
| --- | --- | --------------------- | --- | ----------------- | --------------------- |
| 0   | ADD | binary pop, arith     | 8   | AND               | binary pop, logic     |
| 1   | ADC | binary pop, arith     | 9   | CMP               | flags-only, arith     |
| 2   | SUB | binary pop, arith     | A   | *reserved (idle)* |                       |
| 3   | XOR | binary pop, logic     | B   | ASR               | '194 right, sign fill |
| 4   | NOT | unary, logic          | C   | OR                | binary pop, logic     |
| 5   | TST | flags-only, logic     | D   | *reserved (idle)* |                       |
| 6   | SHL | '194 left, zero fill  | E   | ROL               | '194 left, wrap       |
| 7   | SHR | '194 right, zero fill | F   | ROR               | '194 right, wrap      |

The placements are **not aesthetic**: (a) the shifter fill-mux select is raw IR bits
L3/L2 (zero decode cost), which pins ASR into 10xx and ROL/ROR into 11xx; (b) the chosen slots give the `TOS_M1`/`TOS_M0` decode cheap shared cubes — under the 22V10's 8–16 term budget this is headroom rather than necessity, and the map is what the verified GAL rows implement. '181 code sharing: SUB/XOR/CMP share select 0110 (mode and write-mask differ); ADD/ADC share 1001 (Cn differs); TST = 1111.

**Shifts — five forms on the '194 TOS.** The TOS register *is* a '194 pair: SHL/SHR
(logical, zero fill), ASR (sign fill), plain rotates ROL/ROR (wrap the falling-out bit).
The fill source is one '153 dual 4:1 mux on the '194 serial pins, selected by raw IR
bits L3/L2 — don't-care except while shifting. The pins that matter are the cascade
ends: right-shift serial input = DSR of the **high** '194 (Q7 end); left-shift serial input = DSL of the **low** '194 (Q0 end); Q7/Q0 taps from those same ends. '153 inputs — DSR section: in0 = in1 = GND, in2 = Q7, in3 = Q0; DSL section: in0 = in1 = in2 = GND,
in3 = Q7. C sources from the bit shifted out (`C_SRC = shf`), not the carry chain.
Multi-bit shifts are loops — no shift-amount field. Rotate-through-carry, if ever
wanted, is a one-wire change on the '153 inputs.

**CMP** — non-destructive `( a b -- a b )`: '181 select 0110 arith, Cn = L (exactly
SUB), result discarded — TOS holds, NOS holds, no stack access, ΔDSP 0. Flags of
TOS − NOS latch: Z = equal, C = 1 means TOS ≥ NOS. A popping CMP `( a b -- )` would be a forbidden −2 and does not exist.

**Cn/Cn+4 polarity for the ripple wire.** The '181's Cn *input* takes HIGH = "no
carry-in", LOW = "+1" — opposite polarity from the Cn+4 *output* (HIGH when carry
occurred). Direct-wiring low-'181 Cn+4 to high-'181 Cn inverts the carry meaning during
ripple; a single inverter on the cascade wire fixes it — '04 in the all-HC build,
**HCT04 when the LS-181 backup is fitted** (fence rule; see Sourcing).

---

# 5. Stack Discipline

Every instruction changes DSP by exactly **+1, 0, or −1**. No instruction shifts the
stack by two in one cycle: DSP stays a single up/down counter, stack memory stays
single-port SRAM (one read *or* one write per cycle), and the TOS/NOS input muxes stay
small.

| ΔDSP | Instructions                                                                                                                            |
| ---- | --------------------------------------------------------------------------------------------------------------------------------------- |
| +1   | PUSH #imm, DUP, OVER, TUCK, R@, LOAD addr, IN #port, R>, LOCAL@ off                                                                     |
| 0    | NOT, SHL, SHR, ASR, ROL, ROR, TST, CMP, SWAP, NOP, HALT, all branches, JUMP, CALL, RET, ENTER, BRK, RTI, SEI, CLI, stack-form IN, FETCH |
| −1   | ADD, ADC, SUB, AND, OR, XOR, DROP, NIP, >R, OUT #port, STORE addr, LOCAL! off                                                           |

## TOS / NOS update rules

| Operation                       | TOS ←                                        | NOS ←             | Stack memory                                      |
| ------------------------------- | -------------------------------------------- | ----------------- | ------------------------------------------------- |
| Push (+1)                       | new value (imm / fetched / ALU / NOS / RTOS) | old TOS           | write old NOS at DSP+1                            |
| `TUCK` (+1)                     | hold                                         | hold              | **write old TOS** at DSP+1                        |
| Unary / shift (0)               | ALU or '194-shifted old TOS                  | unchanged         | idle                                              |
| `SWAP` (0)                      | old NOS                                      | old TOS           | idle                                              |
| Binary ALU (−1)                 | ALU(old TOS, old NOS)                        | stack[DSP−1] read | read only                                         |
| `DROP` / `NIP` (−1)             | old NOS / hold                               | stack[DSP−1] read | read only                                         |
| `STORE` / `OUT` / `LOCAL!` (−1) | old NOS                                      | stack[DSP−1] read | read only (the write goes to D-mem / I/O / frame) |
| `FETCH` / stack-form `IN` (0)   | value read (addr/port was old TOS)           | unchanged         | idle                                              |

- **`OVER`** `( a b -- a b a )` is a normal push with `TOS_SRC = NOS` — zero dedicated
  hardware (NOS already feeds the TOS mux via the SWAP path; the spilled old NOS is the duplicate).
- **`NIP`** `( a b -- b )` is a DROP with TOS held — zero dedicated hardware.
- **`R@`** `( -- r )` is a push with `TOS_SRC = RTOS` (the RET path), plus a
  return-stack read that must **not** pop: `rstk_read_en = pop OR R@`, addressing RSP−1 with `RSP_EN` deasserted.
- **`TUCK`** `( a b -- b a b )` writes **old TOS** into the stack SRAM while NOS holds.
  The stack data-in path is therefore a **quad 3-state 2:1 mux ('257 ×2) selecting NOS vs TOS**, with one GAL output for the select.

## Stack pointers

Both DSP and RSP are free-running modulo-256 counters (2× '191 each) with no
overflow/underflow detection — 6502 philosophy, improved: wraparound is *visible* on the front-panel depth LEDs, diagnostic rather than silent. Reset uses the '191's
asynchronous `/LOAD` with parallel inputs tied low, on the shared system reset net ('191 has no clear pin; load-of-zero is its reset idiom). 256 deep is 30×+ headroom over
Forth-shaped code.

---

# 6. Sequencing — PC, Adder, NEXTPC

- **PC register:** 13-bit loadable register (2× 8-bit '377-class, 13 of 16 bits used),
  loading NEXTPC on every rising edge.
- **PC adder:** 13-bit ('283 ×4), A = PC, B = the **offset mux**:
  - sequential / super-op / CALL / BRK cycles: B = +1 (constant 1),
  - taken relative branch: B = sign-extended IR[7:0].
    The adder output is simultaneously "PC+1" (the CALL/BRK return address — free, every cycle the offset mux selects +1) and the relative-branch target.
- **NEXTPC (3-state bus, 13 bits):**
  - **adder** — sequential flow and taken relative branches,
  - **IR[12:0]** — JUMP / CALL absolute target,
  - **RTOS (PC lane)** — RET / RTI, driven directly by the return-stack SRAM,
  - **vector** — interrupt/BRK entry: tied lines force the fixed vector address
    (§Interrupts).
    Conditional branches select *adder-with-offset* vs *adder-with-+1* purely in the
    offset mux; the flag test lives in the flow GAL — the only place flags feed control.
    The 3-state-bus form reuses the return stack's existing bus discipline; a '153 mux tree is the alternative at higher chip count.

---

# 7. Return Stack & Call Frames

The return stack is the machine's call/interrupt spine: an SRAM-backed stack whose
hardware pointer addresses a small RAM, so calls and interrupts nest to the full pointer
depth (256).

## The return word — two 16-bit lanes

Each entry is **two 16-bit lanes** (4× 8-bit SRAM chips of width):

| Lane    | Bits 12:0 | Bits 15:13      |
| ------- | --------- | --------------- |
| PC lane | saved PC  | **N, Z, C**     |
| BP lane | saved BP  | **I, B**, spare |

The flag bits ride in the lanes' spare width, so pushing and popping processor state
costs no extra cycle and no extra chip. Every push drives all lane bits (flags and BP are
simply wired to the data-in); what differs per instruction is what the **pop** loads:
`RET` loads PC and BP only; `RTI` loads PC, BP, N/Z/C, and I; `R>`/`R@` read the PC
lane's low 8 bits as data and load nothing else.

## Pointer convention and addressing — write at RSP, read at RSP − 1

RSP points at the **next-free slot**, so the live top-of-stack sits at RSP − 1. The '191
decrements on the same edge that loads the PC, so a pop must read `stk[RSP−1]` — reading at the raw pointer fetches the empty slot above the top. The addressing is spatial, not temporal:

- A **'283 decrementer** (×2) forms RSP − 1 continuously (A = RSP, B = all-ones,
  Cin = 0 → A − 1 mod 256; carry-out unused).
- A **'157 address mux** (×2) drives the SRAM address from RSP on a write
  (`RSP_UD = up`) and RSP − 1 on a read (`RSP_UD = down`). `/G` tied low.

A pop therefore reads the true top-of-stack, and PC/BP/flags latch it on the same edge
that decrements the pointer — no decremented-address path racing the clock. Verified in TTLSim: nested CALL/RET returns correctly on every pass, RSP unwinds cleanly with single counter edges, worst-case back-to-back CALL → RET included.

## Clocking — no gated clocks, ever

RSP runs **straight off the system clock**; direction is the '157 select, never a gated
or muxed counter clock. Gating the counter clock with a combinational signal (RSP_UD is combinational off the IR, which changes *on* the edge) produces runt pulses and
double-counts. The decrementer removes any need for clock tricks: back-to-back
CALL → RET counts up then down, once each, cleanly.

## Write strobe — WRPH = /CLK

- `/WE` is emitted **directly by the pointers/strobes PLD**, which takes CLK as a combinational input: **`/WE = !(push · /CLK)`** where **push = RSP_EN·(RSP_UD = up)**. The write window is confined to the settled **second half** of the cycle. At the start of a cycle the IR is still resolving and RSP_UD can twitch; a full-clock-high window punches that twitch through to /WE as a spurious write. /CLK keeps it off the strobe. 
  Verified: zero spurious writes under worst-case packing.
- The RSP_EN qualification in the push term is **mandatory**: with `>R` in the
  instruction set, RSP_UD alone does not identify a push.
- `/CS` is driven directly by `RSP_EN` (both active-low) — the RAM is selected exactly
  on push/pop/R@ cycles and its bus is high-Z on every idle cycle.
- Timing margin: /WE deasserts at the cycle-ending edge; the address moves only after counter-plus-mux propagation, while /WE rises after one PLD propagation delay — /WE is high before the address moves. Enormous margin at demo clock rates; only a very fast clock would want a bounded write pulse.

## Data-in and lane sources

- **PC lane bits 12:0:** a 3-state mux drives **PC+1** (CALL, BRK), **raw PC**
  (hardware interrupt entry — the interrupted instruction has not executed, and
  re-executes on return), or **TOS zero-extended** (`>R`), selected by `RDIN_SEL`;
  released on reads so the SRAM drives the RTOS bus onto NEXTPC.
- **PC lane bits 15:13:** the live N/Z/C flags, always driven.
- **BP lane:** current **BP** (bits 12:0, always driven — writing BP on `>R` is
  harmless), the **I** flag, and **B** = 1 for `BRK`, 0 for hardware IRQ/NMI (and
  don't-care on CALL/`>R`, which RET/R> never read back as flags).
- **BP restore is RET/RTI-only.** `R>` and `R@` move data and must not load BP; the BP
  register's load-enable asserts on RET, RTI, and ENTER only.
- Mixing `>R` data with return words is the standard Forth-machine discipline hazard:
  a `RET`/`RTI` through a `>R`'d data value vectors the PC (and BP) into garbage —
  visibly, on the front panel.

## Base pointer and frame locals

Random-access, frame-relative locals remove the operand-permutation tax (the Hanoi
four-argument shuffle) that is the stack machine's one weakness. Locals live in **data
memory**, addressed relative to **BP** — deliberately *not* in the data stack, where a
local read would need a frame read plus a push spill in one cycle (two accesses, illegal
on the single port). D-memory is a separate port, so `LOCAL@` (D-mem read + stack push) and `LOCAL!` (stack pop + D-mem write) each touch two different memories once and stay single-cycle.

| Op           | Effect                              | Encoding                 |
| ------------ | ----------------------------------- | ------------------------ |
| `LOCAL@ off` | push D-mem[BP + off]                | I=0, signed 8-bit offset |
| `LOCAL! off` | pop TOS → D-mem[BP + off]           | I=0, signed 8-bit offset |
| `ENTER k`    | reserve a k-slot frame: BP ← BP + k | I=0, unsigned count      |

- BP marks the **top** of the frame region (next free slot); after `ENTER k` the frame
  occupies BP−k … BP−1, so the standard convention addresses locals at **negative
  offsets**. Exact slot layout is a software calling convention; the hardware supplies
  only BP-relative addressing, automatic save/restore, and frame reserve.
- **No `FREE`/`LEAVE`:** RET's automatic BP restore frees the frame. Tail-call frame
  collapse, if ever wanted, reuses the ENTER adder with a negative constant.
- **Reset:** BP async-loads **0** on `/RESET`, the same load-zero idiom as RSP —
  uniform with PC/DSP/RSP. If low D-memory is reserved for globals, the init stub lifts
  BP above them with one `ENTER <base>` before the first CALL; with no low globals, no stub. The pre-CALL top level runs as "frame 0"; returning out of `main` is undefined (halt).
- **Hardware:** BP register (13-bit, 2× '377-class, async-load-zero), BP source 2:1 mux
  ('157 ×4: ENTER-adder vs saved-BP lane; hold via load-enable), **BP + offset adder
  ('283 ×4)** computing both `BP + k` (ENTER) and the BP-relative local address
  (sign-extended offset, same sign-extension pattern as the branch offset), and the
  D-address mux at 3:1 (super-op IR[12:0] / zero-extended TOS / BP-relative).
- A typical prologue: `ENTER k`, copy stack arguments into the frame with `LOCAL!`,
  body reads them by index with no ROT; RET tears the frame down. Per-level frames mean a child can never clobber a parent's locals. Measured effect on Hanoi: ~30 → ~18 cycles/node; Blinky wins both the call/return half *and* the operand half against a 14 MHz 65C02.

---

# 8. Interrupts — NMI, IRQ, BRK

Two hardware request lines and one software trap, 6502-shaped: interrupt entry **is** a
forced `BRK` — the flow GAL jams the BRK pattern onto the decode inputs in place of the fetched instruction — and the **B** bit in the pushed word is what distinguishes
software BRK (B = 1) from hardware IRQ/NMI (B = 0).

## Semantics

| Event             | Trigger                    | Masked by I?                                         | Pushes                           | PC ←                                |
| ----------------- | -------------------------- | ---------------------------------------------------- | -------------------------------- | ----------------------------------- |
| **NMI**           | edge-latched request line  | no (also overrides CLK_RUN — wakes a HALTed machine) | `{PC, N,Z,C}` + `{BP, I, B=0}`   | NMI vector                          |
| **IRQ**           | level-sampled request line | yes                                                  | `{PC, N,Z,C}` + `{BP, I, B=0}`   | IRQ/BRK vector                      |
| **`BRK`**         | I=0 instruction            | no                                                   | `{PC+1, N,Z,C}` + `{BP, I, B=1}` | IRQ/BRK vector                      |
| **`RTI`**         | I=0 instruction            | —                                                    | pops both lanes                  | saved PC; BP, N/Z/C, I all restored |
| **`SEI` / `CLI`** | I=0 instructions           | —                                                    | —                                | (I ← 1 / I ← 0)                     |

- **Hardware entry pushes raw PC** — the displaced instruction has not executed and
  re-executes on return. **BRK pushes PC+1** — it *has* executed; return resumes after
  it.
- **Entry sets I** (both NMI and IRQ, 6502-style), so the handler runs with further
  IRQs masked; RTI restores the prior I from the stack. Nested IRQs are opt-in via
  `CLI` inside the handler; NMI nests regardless.
- **I = 1 on reset** (interrupts disabled until the init stub does `CLI`) — the
  22V10's asynchronous-reset product term provides this for free.
- **Priority:** NMI over IRQ; one request is taken per inserted cycle.
- Because entry is one inserted cycle and RTI is one cycle, an interrupt round-trip is
  **2 cycles** — against the 6502's 13 (7 entry + 6 RTI) — and BP rides the push
  automatically, so a handler can use `ENTER`/locals with zero extra convention. The
  front panel shows the entry as one settled state.

## Vectors

The reset/vector page sits at the base of I-memory; each vector slot holds a single
`JUMP` (one word, full reach):

| Address | Slot             |
| ------- | ---------------- |
| 0x0000  | reset entry      |
| 0x0001  | IRQ / BRK vector |
| 0x0002  | NMI vector       |

Forcing a vector onto NEXTPC is tied address lines (bit 0 or bit 1 high, rest low)
behind one 3-state enable — effectively free.

## Startup sequence

Vector slots hold **instructions, not addresses** — one word is a full-reach `JUMP`, so
no vector-fetch cycle or reset sequencer exists (unlike the 6502, which reads its
vectors as data). Startup is therefore just the reset state plus ordinary execution:

1. `/RESET` asserts (Mega during development; DS1813 / boot-loader done flip-flop in
   stand-alone builds — released only once I-memory is ready). PC, DSP, RSP, and BP
   async-load **0**; the 22V10's async-reset term sets **I = 1** (IRQs masked).
2. On release, the first fetch is the instruction at 0x0000 — `JUMP init` — executed as
   an ordinary single-cycle instruction, hopping over the vector slots at 0x0001/0x0002.
3. The init stub runs with IRQs already masked (no SEI needed): `ENTER <base>` if low
   D-memory carries globals, set up handlers and ports, `CLI` when ready, fall into
   main. Main never returns — RET out of frame 0 is undefined — so it ends in HALT or a loop. NMI is live from the moment reset releases.

The PC clear *is* the reset vector.

## Hardware

| Block                | Part             | Role                                                                                                                                                                                                                                                                                                                                                   |
| -------------------- | ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Flow / interrupt GAL | **22V10**        | PLD 5 of the complement below. Wide inputs take opcode + I-bit + N/Z/C + the raw IRQ/NMI lines; the 16-term middle macrocells take `PCSEL` (fattest output) and the BRK-force enable; **registered macrocells hold the IRQ sampler, the NMI edge latch, and the I flag** (pin 1 = system clock), with the async-reset term setting I = 1 on reset. |
| BRK-force            | '257 ×2          | Jams the BRK opcode pattern onto the decode-input side of the IR on an interrupt-entry cycle.                                                                                                                                                                                                                                                          |
| Flag restore mux     | '157             | Second source (stack-read bits) into the N/Z/C flip-flops, load-enabled on RTI.                                                                                                                                                                                                                                                                        |
| Vector driver        | 1 buffer enable  | Tied lines onto the NEXTPC bus.                                                                                                                                                                                                                                                                                                                        |
| PC-lane data-in      | (existing '257s) | Carries the raw-PC source alongside PC+1 and TOS.                                                                                                                                                                                                                                                                                                      |

**Net: ~4–5 ICs; the flow PLD is one of the five 22V10s below.** The lane spare bits are what make
this cheap: state save/restore needs no extra stack width, no extra cycle, and no extra
memory chip.

## PLD complement — 5× 22V10

All decode and control is **one device type**: five 22V10s. Ten macrocells and an 8–16
product-term budget per output remove the term squeeze everywhere, and the wider fabric
absorbs the discrete glue — the strobe '00 and the '191 polarity inverters disappear into
the equations.

| PLD | Role | Outputs (~) | Inputs |
| --- | --- | --- | --- |
| 1 — ALU / shift | `ALU_S3..S0`, `ALU_M`, `ALU_Cn`, `C_SRC`, `TOS_M1`, `TOS_M0` (9) | I-bit + opcode + sub-op + **C** (13 — one spare macrocell pin serves as the 13th input) |
| 2 — stack-top / D-mem | `TOS_SRC2..0`, `NOS_LD`, `NOS_SRC`, `SDIN_SEL` (TUCK), `NZ_WE`, `C_WE`, `DMEM_CS`, `DMEM_WE` (10) | I-bit + opcode + sub-op (12) |
| 3 — pointers / strobes | `DSP_EN`, `DSP_UD`, `RSP_EN`, `RSP_UD`, `RDIN_SEL1..0`, plus the **phase-gated `/WE` strobes emitted directly** (9–10) | I-bit + opcode + sub-op + **CLK** as a combinational input |
| 4 — BP / frame / I-O | `BP_LD`, `BP_SRC`, `DADDR_S1..0`, `IO_RD`, `IO_WR`, `IOADDR_SEL`, `CLK_RUN` (8) | I-bit + opcode + sub-op + `NMI_PEND` (the HALT-wake override) |
| 5 — flow / interrupts (registered) | `PCSEL1..0`, `BSEL` (offset-mux select), `BRK_FORCE`, `VEC_EN`, `IRQ_PEND` (reg), `NMI_PEND` (reg), `I` (reg) (8) | pin 1 = CLK; I-bit + opcode + N, Z, C + raw IRQ, NMI (13 — two unused macrocell pins serve as inputs) |

Input budgeting is the design rule: a 22V10 offers 12 dedicated inputs (11 when pin 1 is
the clock for registered outputs), and each unused macrocell pin adds one more. Pure
decode PLDs need exactly the 12-line instruction field; the two over-budget devices (1
and 5) run ≤9 outputs and reclaim macrocell pins as inputs. `ALU_Cn` lives on PLD 1 —
not the flow PLD — because it needs the sub-op lines (ADC vs SUB) plus C, and PLD 1
already has both. The registered pending/I states feed back internally, costing no pins.

The **N/Z/C flip-flops stay discrete ('74s)** deliberately: absorbing them would need
result bit 7, A=B, Cn+4, Q0, Q7, and the three stack-read bits as inputs — ~11 pins for
3 outputs, an input footprint that packs with nothing and would force a sixth device.

The derived-strobe pattern (/CS and /WE derived from pointer signals rather than decoded
— the DSTK/RSTK template) remains the standing technique for reclaiming outputs.
BlinkyJED, TTLSim, and the WinCUPL cross-check chain all support the 22V10, so every
device verifies the same way: BlinkyJED compile, fuse-for-fuse cross-check, JED import
into the sim, full IR sweep, JEDTester on silicon — one `GalDef`, one stock line.

---

# 9. Memory & I/O

| Space        | Size                       | Notes                                                                                                                 |
| ------------ | -------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| I-memory     | 8K × 16                    | 2× 8-bit devices in parallel (28C64 EEPROMs). Harvard, fetch parallel with data access. Vector page at 0x0000–0x0002. |
| D-memory     | 8K × 8                     | One 62256 / CY7C199-class part, 8K used. Address from the 3:1 D-address mux (super-op / TOS / BP-relative).           |
| Data stack   | 256 × 8                    | Single-port; /CS, /WE derived from DSP; TUCK write-select '257 on data-in.                                            |
| Return stack | 256 × 32 (2× 16-bit lanes) | Single-port; /CS, /WE derived from RSP per §7.                                                                        |
| I/O          | 256 ports × 8              | Z80-style separate space; `IORQ` distinguishes I/O from D-mem cycles.                                                 |

## I/O

Reads in both forms (`IN` stack-form takes the port from TOS, `( port -- value )`,
ΔDSP 0; `IN #port` immediate); writes immediate-form only (`OUT #port` pops TOS). The port-number path is a 2:1 mux (TOS vs IR[7:0]). Decode only the bits current devices need; unused high bits alias harmlessly. The 8×8 direct-mapped LED matrix (8× '374 row latches at ports 0–7, DUP…OUT idiom to broadcast) is the centerpiece demo, plus a '244 switch port and a '374 status port — ~2 decoder chips, ~10 port chips. Peripherals that raise IRQ (a timer, a UART-class port) wire onto the level-sampled IRQ line; a panic button or single-shot debug trigger suits NMI.

---

# 10. Front Panel

| Field             | LEDs | Meaning                                                                      |
| ----------------- | ---- | ---------------------------------------------------------------------------- |
| PC                | 13   | position in program                                                          |
| IR                | 16   | current instruction, fully visible                                           |
| TOS               | 8    | top of data stack — the "accumulator" in spirit; the shifter display         |
| NOS               | 8    | next on stack                                                                |
| DSP               | 8    | data-stack depth                                                             |
| RTOS              | 13   | top of return stack (where we'll return to)                                  |
| RSP               | 8    | return-stack depth                                                           |
| BP                | 13   | current frame top — recursion depth becomes *visible* as BP climbs and falls |
| N / Z / C / I     | 4    | ALU flags + interrupt mask                                                   |
| IRQ / NMI pending | 2    | the synchronizer outputs — an interrupt is visible *before* it is taken      |

Plus clock/run/halt/step controls. Running Hanoi, the BP LEDs breathe with recursion
depth while RSP mirrors it; single-stepping into an interrupt shows the pending LED,
then the one-cycle entry as a settled state — displays no accumulator machine can offer.

---

# 11. Chip Count Estimate

Core CPU and memory subsystem (all-HC baseline; see Sourcing):

| Block                                                                                                                                                     | Chips      |
| --------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------- |
| ALU (2× '181 + Cn ripple inverter)                                                                                                                        | 2–3        |
| TOS (2× '194) + fill mux ('153) + NOS ('374)                                                                                                              | 4          |
| TUCK write-select ('257 ×2 on stack data-in)                                                                                                              | 2          |
| Data stack SRAM + DSP ('191 ×2)                                                                                                                           | 3          |
| Return stack SRAM ×4 (two 16-bit lanes) + RSP ('191 ×2) + RSP−1 decrementer ('283 ×2) + address mux ('157 ×2) + PC-lane data-in muxes | ~12        |
| PC register ('377 ×2) + PC adder ('283 ×4) + offset mux/sign-extend ('157 ×2)                                                                             | 8          |
| NEXTPC 3-state bus drivers + vector enable                                                                                                                | 3          |
| BP register ('377 ×2) + source mux ('157 ×4) + BP adder ('283 ×4)                                                                                         | 10         |
| D-address 3:1 mux (13-bit)                                                                                                                                | ~4         |
| Instruction register ('374 ×2) + BRK-force ('257 ×2)                                                                                                      | 4          |
| Flag flip-flops + RTI restore mux                                                                                                                         | 3          |
| Decode / control (**22V10 ×5**)                                                                                                                           | 5          |
| I-memory (2× 8K×8) + D-memory (1)                                                                                                                         | 3          |
| I/O decode ('138s) + port-number mux ('157)                                                                                                               | 3–4        |
| Bus drivers, clock/reset/single-step                                                                                                                      | 4–5        |
| **Core total**                                                                                                                                            | **~70–73** |

Optional peripherals (matrix, switches, status) add ~10. The hardware breakpoint ('688
comparator ×2 for the 13-bit PC + '00 + '74, proven on the W = 4 board) adds 4.

---

# 12. Sourcing Notes (June 2026)

Stock tracked in `ChipInventory.md` (all-HC baseline; "in transit" = ordered, counted
available).

- **22V10** is the single PLD type machine-wide (ATF22V10C class); the GAL16V8 stock
  stays with the W = 4 board.
- **'191** for all stack pointers (HC169 unobtainable). Direction sense: '191 D//U
  LOW = up (absorbed in the PLD equations); count enable is active-low /CTEN; load is
  asynchronous — which is what makes the /LOAD-low reset idiom work.
- **'181.** Primary 74HC181; 74LS181 backup only, behind an HCT fence at **every** '181
  output boundary (HCT04 ripple inverter, HCT374 latches on /F, HCT245 buses, HCT flag taps). All-HC build needs no fence.
- **'374.** HCT374 is the drop-in for every '374 slot (HC374 unobtainable).

---

# Deliberately Excluded

- **`FREE` / `LEAVE`** — RET's automatic BP restore already frees the frame.
- **Flat register file ('670 ×2)** — redundant beside frame locals; additive later if a
  hot non-recursive loop ever wants it.
- **`PICK` / `ROLL`** — not single-cycle on the single-port stack; frame locals do the
  job better.
- **Dual-port stack SRAM** — large chip-count hit for a fraction of the win frame
  locals already capture.
- **V flag** — not adopted. The 22V10 removes the pin-budget blocker, so signed
  branches (BVS/BVC) are feasible; they remain excluded until a workload wants them.
- **Popping CMP `( a b -- )`** — forbidden −2; the non-destructive CMP covers the need.
- **Stack-form STORE / OUT** — forbidden −2 or dangling-operand −1; writes are
  immediate/super-op form only.
- **Interrupt priority encoder / multiple IRQ vectors** — one IRQ line, one vector; the handler polls device status ports. Additional request lines cost 22V10 inputs the
  budget doesn't owe.
