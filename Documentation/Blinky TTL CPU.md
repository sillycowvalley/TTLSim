# TTL Blinky CPU — Design Summary

A minimal-but-honest TTL CPU aimed at "blinking lights" aesthetics: every LED on the front panel means something, every clock tick is one instruction, and the architecture is interesting enough to be more than a toy.

## Design Philosophy

- **Single-cycle execution** — one clock tick = one instruction, no exceptions.
- **Front-panel honesty** — every visible LED reflects real machine state in real time. Single-stepping shows complete, settled states.
- **Minimum chip count without architectural compromise** — roughly 25–30 TTL packages plus memory.
- **Stack machine, not register machine** — operands are implicit, control logic stays small, the front panel becomes more informative.

## Top-Level Architecture

**Stack machine, Harvard memory, dual data/return stacks.**

| Bus / Width | Size | Notes |
|---|---|---|
| Data bus (D) | 8 bits | Main data memory and ALU width |
| Instruction bus (I) | 16 bits | Fixed-width instructions, single-cycle fetch |
| Address bus | 8 bits | 256 bytes each of I-memory and D-memory |
| Data stack | 8 bits wide | TOS+NOS in registers, rest in stack memory |
| Return stack | 8 bits wide | Separate memory, accessed independently |

Four memories total: instruction ROM, data RAM, data stack, return stack. Each on its own bus — all four can be accessed in the same clock cycle. That's the architectural luxury that makes single-cycle execution feasible on TTL.

## Why a Stack Machine

On a register machine, an instruction must encode source and destination registers — those bits come out of the opcode budget. On a stack machine, operands are implicit: ALU input A is always TOS, input B is always NOS, result goes to TOS. No operand routing, no register file decode, no mux trees feeding the ALU.

Concrete benefits:

- **Simpler ALU plumbing.** ALU inputs are wired directly to TOS and NOS registers. No operand mux.
- **More opcode space.** With 16-bit instructions and no register fields, the entire encoding is available for opcode and immediate.
- **Better front panel.** TOS and NOS LEDs show live computation. Stack depth LEDs show program state. Registers, by contrast, are static — they don't "flow."
- **Forth-friendly.** A small Forth interpreter is the natural software stack and writes itself onto this hardware.

Historical precedents: Novix NC4016, MuP21, F21, Burroughs B5000 (in spirit), and every Forth machine ever built.

## Why Harvard

Separate instruction and data buses eliminate fetch/execute contention. On a TTL machine where you already have separate ROM and RAM chips, Harvard is essentially free — it just acknowledges the physical reality of the memory system.

Wins:
- No bus arbitration logic.
- Instruction fetch happens in parallel with data memory access.
- Doubles effective address space (256 bytes of code AND 256 bytes of data, not 256 total).
- Enables true single-cycle execution.

Cost: can't execute code from RAM. For this machine, irrelevant — programs live in EEPROM.

## Why 16-Bit Fixed Instructions

The competing option was 8-bit variable-length (denser code, more programs fit in memory). Rejected because:

- **Single-cycle execution is the whole aesthetic.** Variable-length means some instructions are two cycles, which breaks the "one tick = one instruction" property and makes single-stepping ambiguous.
- **Code density doesn't bind at this scale.** 256 bytes of program memory = 128 fixed-width instructions, which is plenty for blinky programs (Larson scanner, Fibonacci, sieve, tiny Forth core all fit comfortably).
- **No fetch state machine.** The control unit becomes: latch ROM output on clock edge, decode combinationally, done.
- **Full address reach in one instruction.** `BRANCH 0x42`, `LOAD 0x80`, `CALL 0xC0` — all single-word, single-cycle. No high-byte/low-byte sequences.

Cost: one extra EEPROM chip on the I-memory side. Worth it.

## Instruction Encoding Sketch

16 bits, with a generous budget thanks to implicit operands:

```
 15  14  13  12  11  10   9   8   7   6   5   4   3   2   1   0
+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
| I |        opcode (7 bits)    |     immediate / address (8b)  |
+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
```

- **Bit 15 (I):** "has immediate" flag.
- **If I=0:** 15 bits of pure opcode space (way more than needed). Used for ALU ops, stack manipulation, returns, and other operand-less instructions.
- **If I=1:** 7-bit opcode + 8-bit immediate or absolute address. Used for PUSH-immediate, branches, calls, direct loads/stores.

### Opcode Families (rough allocation)

| Family | Examples | Notes |
|---|---|---|
| ALU | ADD, ADC, SUB, SBC, AND, OR, XOR, NOT, SHL, SHR, NEG, CMP, TST | TOS,NOS → TOS. **ALU ops set N, Z, C flags as a side effect.** ADC/SBC read C for multi-byte arithmetic. CMP/TST set flags without writing TOS (see below). |
| Stack | DUP, DROP, SWAP, OVER, NIP, TUCK, ROT | Data stack manipulation. Does not affect flags. |
| Return stack | >R, R>, R@ | Move between stacks. Does not affect flags. |
| Memory (stack form) | FETCH, STORE | Address from TOS (Forth-style). Does not affect flags. |
| Memory (immediate form) | LOAD #addr, STORE #addr | Address from instruction's low byte. Does not affect flags. |
| I/O (stack form) | IN, OUT | Port number from TOS. Does not affect flags. |
| I/O (immediate form) | IN #port, OUT #port | Port number from instruction's low byte. Does not affect flags. |
| Control | RET, NOP, HALT | Operand-less. Does not affect flags. |
| Immediate | PUSH #imm, BRANCH addr, CALL addr, BEQ addr, BNE addr, BCC addr, BCS addr, BMI addr, BPL addr | Use the immediate field. Conditional branches test the N/Z/C flags set by the most recent ALU op. |

Both stack and immediate forms are supported for memory and I/O. The stack form is required when the address or port number is computed at run time (e.g., looping over the 8×8 matrix rows); the immediate form is denser for fixed accesses (one instruction instead of `PUSH #addr` + access). Cost is one extra opcode per variant — negligible given the encoding budget.

Most opcodes are operand-less (one cycle, no immediate). Branches, calls, pushes of arbitrary constants, and immediate-form memory/I/O accesses use the immediate field — still one cycle, because the 16-bit instruction already contains everything needed.

### CALL / RET semantics

CALL pushes PC+1 onto the return stack and sets PC to the immediate address. PC+1 is the pre-incremented PC value — the PC is a '161 counter that increments every cycle anyway, so PC+1 is available on the counter's outputs *before* the next clock edge, with no adder needed. Wire that into the return-stack write port.

RET sets PC ← RTOS (no increment) and pops the return stack.

## Register Set

Not registers in the traditional sense — these are the architecturally visible state elements:

| Element | Width | Purpose |
|---|---|---|
| PC | 8 bits | Program counter (I-memory address) |
| IR | 16 bits | Instruction register (current instruction) |
| TOS | 8 bits | Top of data stack (in a '374 latch) |
| NOS | 8 bits | Next on data stack (in a '374 latch) |
| DSP | 8 bits | Data stack pointer |
| RTOS | 8 bits | Top of return stack |
| RSP | 8 bits | Return stack pointer |
| N flag | 1 bit | Negative — bit 7 of the most recent ALU result. Used by BMI/BPL for signed comparisons. |
| Z flag | 1 bit | Zero — set when the most recent ALU result was all zero. Used by BEQ/BNE. |
| C flag | 1 bit | Carry — carry-out of bit 7 from the most recent ALU op. Used by ADC/SBC for multi-byte arithmetic, and by BCC/BCS for unsigned comparisons. |

TOS and NOS in dedicated latches (not in stack memory) is non-negotiable — it lets ALU ops complete in one cycle without a memory access.

## Stack Pointer Behavior

Both DSP and RSP are free-running modulo-N counters with no overflow or underflow detection. This mirrors the 6502's stack design philosophy, adapted for a dual-stack machine — and it's strictly cleaner here than on the 6502.

Specification:

- Both SPs are '169 (4-bit synchronous up/down) counters, cascaded ×2 for 8 bits.
- The `/CLR` pin of each SP counter ties to the same system reset net as the PC's `/CLR` — power-on state is SP=0 for both stacks. Free, since the reset wire is already routed for the PC.
- No comparator, no range-check logic, no "stack full" or "stack empty" signal feeding the control unit.
- SP outputs go directly to the stack memory address pins.
- On overflow or underflow, the SP wraps silently (255 → 0 and vice versa).

Why this works:

- **Headroom.** Forth-shaped code rarely exceeds 4–5 deep on the data stack or ~8 deep on the return stack. A 256-deep stack has 30×+ headroom. Overflow in correct code is essentially impossible.
- **Visible failure.** When wraparound *does* happen (because of a buggy program), the DSP/RSP LEDs visibly roll over on the front panel. The bug is diagnostic, not silent — a strict improvement over the 6502, where overflow silently corrupts the bottom of the stack with no indication.
- **Return-stack wraparound is its own warning.** If RSP wraps and a subsequent RET pulls a nonsense address, the PC visibly jumps somewhere wrong and the machine starts executing garbage. Obviously broken, immediately visible.

What this buys:

- No overflow/underflow logic anywhere in the control unit.
- No fault or halt signals to wire up.
- No decisions about what to do on overflow (trap? halt? wrap?).
- Reset wiring is "tie all clear pins to one net" — no per-SP reset logic.

What this forecloses:

- No software-visible stack-overflow exceptions. Fine for blinky (no interrupts anyway). The "useful" v2 of this design might want real overflow detection on at least the return stack, since silent return-stack wraparound is hard to debug in larger programs.

### Stack Modification Per Cycle

Every instruction changes the data stack pointer by exactly **+1, 0, or −1**. No instruction shifts the stack by two in a single cycle. This is a deliberate constraint that simplifies the hardware substantially:

- DSP is a single up/down counter (two cascaded '169s), with one direction-control signal from the decoder.
- Stack memory is single-port SRAM — one read *or* one write per cycle, never both.
- TOS and NOS each move by at most one position per cycle, so their input muxes stay small.

By instruction class:

| ΔDSP | Instructions |
|---|---|
| +1 | `PUSH #imm`, `DUP`, `OVER`, `FETCH`, `LOAD #addr`, `IN #port`, `R>` |
| 0 | `NOT`, `NEG`, `SHL`, `SHR`, `TST`, `SWAP`, `ROT`, `NOP`, branches, stack-form `IN` |
| −1 | `ADD`, `ADC`, `SUB`, `SBC`, `AND`, `OR`, `XOR`, `CMP`, `DROP`, `NIP`, `>R`, `OUT #port`, `STORE #addr`, stack-form `STORE`, stack-form `OUT` |

#### How stack-form STORE and OUT stay at −1

Conventional Forth `STORE` is `( data addr -- )`, consuming both. That's a −2 instruction, which would require either dual-port stack memory or a banking scheme, plus DSP-by-2 logic.

Instead, stack-form `STORE` and `OUT` consume **only the address/port** (TOS) and write **NOS** to the target, leaving NOS in place:

- `STORE` (stack form): `( data addr -- data )`. Writes NOS to D-memory at TOS, pops TOS.
- `OUT` (stack form): `( data port -- data )`. Writes NOS to the I/O port at TOS, pops TOS.

Code that wants the value gone follows with `DROP`. Code that wants to reuse the value (e.g., for a comparison or a subsequent store to an adjacent address) doesn't. This idiom matches how RTX2000 and similar stack machines handle the same problem, and adds maybe a dozen extra `DROP`s across an entire blinky-sized program — negligible.

The immediate forms (`STORE #addr`, `OUT #port`) take their address/port from the IR rather than the stack, so they're naturally −1 — they consume only the data from TOS.

#### TOS / NOS update rules

With ΔDSP ∈ {+1, 0, −1}, each instruction picks one input for TOS and one for NOS from a small set of sources:

| Operation | TOS ← | NOS ← | Stack memory |
|---|---|---|---|
| Push (+1) | new value (immediate, fetched, ALU, etc.) | old TOS | write old NOS at DSP+1 |
| Unary (0) | ALU result of old TOS | unchanged | idle |
| `SWAP` (0) | old NOS | old TOS | idle |
| Binary ALU (−1) | ALU result of old TOS, old NOS | stack[DSP−1] read | idle (read only) |
| `DROP` (−1) | old NOS | stack[DSP−1] read | idle (read only) |
| Stack-form `STORE`/`OUT` (−1) | old NOS | stack[DSP−1] read | D-memory or I/O write of old NOS |

Every row uses at most one stack memory access (read *or* write), and TOS/NOS each have a 4-to-1 mux on their input, no more. That's the whole stack data path.

## I/O

Z80-style separate I/O address space, 8 bits wide, accessed via dedicated `IN` / `OUT` instructions. This keeps the 256-byte D-memory space available entirely for data, and the symmetric 8-bit address width matches every other address bus in the machine.

### Address space

- **8-bit port address**, logically 256 ports.
- **Decoded as much as needed.** Physical decoder chips only cover the bits required for current devices. Unused high bits alias — fine, since nothing lives at the aliased addresses. v1 typically decodes 3–4 bits (8–16 ports).
- **Reuses the D-address bus** during `IN`/`OUT` cycles. A dedicated `IORQ` control signal from the CPU tells the I/O decoder "this bus access is for you, not for D-memory."

### Instruction variants

Both stack-operand and immediate forms are supported, mirroring the memory access pattern:

| Form | Encoding | Port number from | Use case |
|---|---|---|---|
| `IN` / `OUT` | `I=0` operand-less | TOS | Computed port numbers (e.g., looping over matrix rows) |
| `IN #port` / `OUT #port` | `I=1` with immediate | Instruction's low byte | Fixed ports (status LEDs, switches) |

Stack form: `IN` replaces the port number on TOS with the input byte read from that port (net ΔDSP = 0). `OUT` writes NOS to the port whose number is on TOS, then pops TOS, leaving the data value on the stack (net ΔDSP = −1; follow with `DROP` if the value isn't wanted). See "Stack Modification Per Cycle" above for why `OUT` is shaped this way.

Immediate form: port number is the low byte of the instruction. `OUT #port` pops data from TOS and writes to the named port; `IN #port` reads from the named port and pushes onto the stack. Single cycle each.

### Port-number routing

The I/O decoder receives its 8-bit port number through a 2:1 mux:

- Mux input A: TOS (for stack-form `IN`/`OUT`).
- Mux input B: IR low byte (for immediate-form `IN #port`/`OUT #port`).
- Mux select: controlled by the instruction decoder (the `I` bit plus opcode pattern).

One '157 (8-bit 2:1 mux, two packages) handles this. Same pattern can be reused for memory addresses if both stack and immediate forms of `FETCH`/`STORE` and `LOAD`/`STORE` are wired through a shared address mux.

### Example: 8×8 LED matrix mapped as 8 sequential ports

Wire 8× '374 latches at ports 0x00–0x07, each driving one row of the matrix. Writing to port N sets the pattern for row N.

This mapping plays beautifully with the stack-form `OUT`:

```
\ Clear the matrix (all rows to 0)
0                  \ value to write, stays on stack across iterations
8 0 DO             \ loop counter 0..7
  I OUT            \ write value (NOS) to port I (TOS); pops port, keeps value
LOOP
DROP               \ discard the value we kept around
```

Because stack-form `OUT` preserves the data value, the same byte gets written to all 8 ports without re-pushing it each iteration.

Or for a Game of Life update, compute the next-generation byte for each row on the stack and `OUT` it to the appropriate port. The matrix is direct-mapped — no scanning multiplexer, no refresh logic, every LED reflects a real bit at all times. 64 LEDs, 8 latch chips, total honesty.

For fixed ports like a status LED byte or input switches, the immediate form is denser:

```
SWITCHES IN        \ read switches port directly
STATUS OUT         \ write TOS to status port directly
```

### Decoder chip count

- v1 build (8×8 matrix at ports 0x00–0x07, switches at 0x08, status LEDs at 0x09): one '138 decodes the low 3 bits for the matrix rows, another handles a couple of additional ports. **~2 chips for the I/O decode.**
- Plus the port hardware itself: 8× '374 for the matrix rows (8 chips), 1× '244 for switches input (1 chip), 1× '374 for status LEDs (1 chip). **10 chips for the actual ports.**

The matrix dominates the chip count, but it dominates the *experience* too — 64 directly-mapped LEDs is the centerpiece demo.

## Front Panel

The point of all this. Every architectural element gets LEDs:

- **PC (8 LEDs)** — where we are in the program.
- **IR (16 LEDs)** — current instruction, fully visible.
- **TOS (8 LEDs)** — top of data stack, the "accumulator" in spirit.
- **NOS (8 LEDs)** — next on stack, visible operand for the next ALU op.
- **DSP (8 LEDs)** — data stack depth.
- **RTOS (8 LEDs)** — top of return stack (where we'll return to).
- **RSP (8 LEDs)** — return stack depth.
- **Flag LEDs (3 LEDs: N, Z, C)** — set by every ALU op. Show the result of the most recent arithmetic or logical operation.
- **Clock / Run / Halt / Step controls** — slow clock (1 Hz to a few kHz), single-step button, run/halt switch.

Running Fibonacci, you watch the two top-of-stack LEDs do the Fibonacci dance while the depth stays constant. Running a Larson scanner, the output port LEDs sweep while the PC cycles through a tight loop. The machine *shows you what it's doing.*

## Chip Count Estimate

Core CPU and memory subsystem:

| Block | Chips |
|---|---|
| ALU (2× '181 cascaded for 8 bits, plus a '04 inverter on the Cn+4 → Cn ripple wire — see Flags note) | 2–3 |
| TOS + NOS latches ('374) | 2 |
| Data stack memory (256-deep SRAM) | 1 |
| Data stack pointer ('169 up/down counter, cascaded ×2 for 8 bits) | 2 |
| Return stack memory + pointer (1× SRAM + 2× '169) | 3 |
| PC ('161) | 1 |
| Instruction register (2× '374) | 2 |
| Decode / control ('138s, '139s, glue) | 3–4 |
| Bus drivers ('245s) | 2 |
| Address-source mux ('157, selects TOS vs. IR-low-byte for memory/IO address) | 2 |
| I-memory (2× 8-bit SRAM in parallel — loaded by Mega at boot; or 2× 8-bit EEPROM for stand-alone) | 2 |
| D-memory (1× SRAM) | 1 |
| I/O address decoder ('138s) | 2 |
| Clock / reset / single-step | 2–3 |
| **Core total** | **~27–32** |

Optional I/O peripherals (typical v1 build):

| Block | Chips |
|---|---|
| 8×8 LED matrix (8× '374 row latches, one per row) | 8 |
| Switch input port (1× '244 buffer) | 1 |
| Status LED output port (1× '374 latch) | 1 |
| **Peripheral total** | **10** |

The peripherals dominate the chip count once the matrix is in play, but they dominate the experience too — 64 direct-mapped LEDs is the centerpiece demo. A minimal build without the matrix (just switches + status LEDs) is ~2 peripheral chips.

Single perfboard, weekend or two of wiring, front panel that fits on a single piece of aluminum.

## Stack Depth

Both stacks are 256 deep, implemented in SRAM. The 8-bit stack pointers match the front-panel symmetry — DSP and RSP LEDs are the same width as every other address bus in the machine — and 256 is far deeper than any sane program will ever use, so wraparound is purely a diagnostic for bugs.

## What This Machine Can Run

- Larson scanner / chaser lights on an output port
- Count and display loops
- Fibonacci sequence
- Sieve of small primes
- Tiny Forth inner interpreter (~60–80 instructions)
- Simple game-of-life on an 8×8 LED matrix
- Bit-banged serial I/O for terminal connection (slow but works)

Not enough room for a full Forth — that's the "useful" version of this design, with 16-bit data and a 12-or-16-bit address bus. The blinky machine is the architectural prototype; the useful machine is the same shape scaled up.

## Development Rig

An Arduino Mega 2560 acts as the development companion, transforming the edit-compile-run loop from minutes (EEPROM swap) to seconds (USB upload). The Mega is *not* part of the CPU — the CPU's hardware interface is unchanged. The Mega just drives that interface from the outside during development.

### What the Mega does

- **Loads program memory at boot.** I-memory becomes SRAM (not EEPROM). On reset, the Mega writes the program into the SRAM while the TTL CPU is held in reset. When loading completes, the Mega releases its drive on the I-memory buses and asserts run.
- **Generates the clock.** Programmable frequency from single-step (button press → one cycle) up to whatever the TTL can handle. Enables run-to-PC-value breakpoints by simply not generating the next clock pulse when the address bus matches a target.
- **Drives reset.** PC-controlled, so the host can reset and reload the TTL machine without touching it.
- **Spies on buses.** With ~25 pins free after the boot-load role, the Mega can sample the D-bus, D-address bus, flags, and selected stack-top lines on every clock cycle and stream a trace back to the PC over USB.

### Pin allocation (Mega 2560, 54 digital I/O)

| Role | Pins |
|---|---|
| I-memory address (write during boot, high-Z after) | 8 |
| I-memory data, low byte (write during boot, high-Z after) | 8 |
| I-memory data, high byte (write during boot, high-Z after) | 8 |
| I-memory write-enable | 1 |
| TTL clock output | 1 |
| TTL reset output | 1 |
| Spare for spy lines (D-bus, D-address, flags, etc.) | ~25 |

### Bus separation

The Mega's connections to the I-memory address and data buses go through 74HC245 transceivers. During boot, the Mega's `/OE` line to those transceivers is asserted; after boot it's deasserted and the TTL CPU's PC drives the address bus while the SRAM drives the data bus. Clean separation, no risk of the Mega glitching a fetch by accidentally reasserting a pin.

Cost: 3 extra '245 transceivers in the I-memory path. Worth it for reliability.

### What this unlocks

- **Instant program load.** Edit on PC, upload via USB, run. No EEPROM programmer, no chip swap.
- **PC-value breakpoints.** Mega watches the address bus, halts the clock at a target PC.
- **Watchpoints on D-memory accesses.** Mega watches the D-address bus, halts on read/write of a target address.
- **Execution traces.** Per-cycle sampling of whatever's wired to spy pins, logged to PC over USB.
- **Automated regression tests.** PC-side harness loads a program, runs N cycles, reads back state, compares to expected.
- **Variable clock speeds for demos.** 1 Hz for "watch the LEDs," 100 kHz for "show it running fast," anything in between.

### Why Mega over Uno

An Uno has only ~18 usable pins, which is not enough for the parallel data paths the boot-load role needs without shift-register tricks. A Mega has 54 pins — the boot loader uses ~27, leaving ~25 free for spy duties without any pin-multiplexing gymnastics. For an extra ~$10 over an Uno, the Mega eliminates a layer of design complexity in the development rig. Easy call.

## Open Decisions

- **Barrel shifter or single-bit shifts?** Single-bit is cheaper (one '194 or similar). Barrel shifter is ARM-flavored but costs real chips. Single-bit shifts are fine for blinky.
- **Flags.** Three flags: N (negative, bit 7 of result), Z (zero, all bits of result are zero), C (carry-out of bit 7). All three are written by every ALU op and only by ALU ops — stack, memory, I/O, and control instructions leave the flags alone, so a `CMP` or arithmetic op can be separated from its consuming branch by any number of non-ALU instructions. Cost: ~1.5 packages (one '74 dual D flip-flop holds two flags, half of a second holds the third) plus three front-panel LEDs. The Z flag rides for free on the '181's A=B output (wire-ANDed across the two cascaded ALU chips); N is bit 7 of the result; C is the Cn+4 output of the high '181 latched directly (active-HIGH when carry occurred, matching the BCS-when-carry-set semantics).

**Cn/Cn+4 polarity gotcha for the ripple wire.** In the active-high-operand convention this design uses, the '181's Cn *input* takes HIGH to mean "no carry-in" and LOW to mean "+1 carry-in" — the opposite polarity from the Cn+4 *output*, which is HIGH when carry occurred. Direct-wiring Cn+4 of the low '181 to Cn of the high '181 therefore inverts the carry meaning during ripple. A single '04 inverter slot on the cascade wire fixes it. (For subtract, the '181 internally complements B; the same inverter still gives the correct ripple — the *flag* polarity is what differs between add and subtract conventions, but the C flag is read off the high '181's Cn+4 directly, so software just learns that "carry set after SUB = no borrow", same as the 6502.)
- **Conditional execution granularity?** Six flag-based conditional branches (BEQ, BNE, BCC, BCS, BMI, BPL) plus unconditional BRANCH, in 6502 style. Comparisons are done by CMP (subtract, set flags, discard result) or TST (AND TOS with itself, set flags, leave stack unchanged), or fall out as side effects of plain arithmetic. The branch-decode logic is a single 8-way mux (six conditions plus always plus the unconditional case) — call it one '151 or '153.
- **Program loading.** During development, an Arduino Mega serves as both boot loader and debug controller — see the Development Rig section above. For a stand-alone deployment, two 8-bit EEPROMs in parallel (low byte + high byte of the 16-bit I-bus) replace the Mega + SRAM, with programs burned off-board and chips swapped via sockets.

## Summary

8-bit data, 8-bit address (everywhere — D-memory, I-memory, I/O), 16-bit fixed instructions, Harvard memory, dual stacks, single-cycle execution. Stack machine semantics keep the control logic small and the front panel meaningful. About 30 TTL chips for the core datapath, control, and memory, plus ~10 more for an 8×8 LED matrix and other peripherals. A genuine CPU with genuine character — and a front panel that actually shows you what's happening.
