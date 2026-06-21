# Blinky / TTL Video Roadmap

A backlog of video ideas for the TTL CPU series, grouped by format, with
dependencies and a suggested release order. The series voice is established by
the clock, breakpoint, and UART scripts: terse, incremental, teach-then-use,
ending on a PCB.

## Status so far

- **Clock Module** — build — script done.
- **Breakpoint Card** — build — script done.
- **UART Module** — build — script done.

---

## A. The CPU, one instruction at a time

A sub-series. Each video plugs into the existing clock + breakpoint boards and
adds exactly one capability, watched and single-stepped as it comes up.

- **PC + IR — the fetch loop.** Counter plus instruction register, clocked by
  the clock module, watched by the breakpoint. The first "instruction" is just
  *advance* — proves fetch before anything executes.
- **Unconditional branch.** Load the PC from the IR. Control flow with nothing
  else attached.
- **The data stack.** TOS/NOS registers, the stack pointer, push and pop — the
  thing that makes it a stack machine.
- **The ALU.** The '181 plus the decode GALs; arithmetic, logic, and the flags
  falling out.
- **Conditional branches.** BEQ / BCS / BMI off those flags — now it loops and
  decides.
- **Memory — LOAD / STORE.** The single-port stack-RAM constraint on screen,
  finally exercised by code.
- **I/O — IN / OUT.** Wire the UART front-end in and have the machine print.
  Pays off the UART video.
- **The return stack — CALL / RET.** The return-stack module; push the PC, pop
  it back. The capstone of the arc.
- **First program.** Assemble something real, burn it, single-step it on the
  breakpoint, then let it run.

## B. More standalone modules

Same modular format, each its own board.

- **Program memory & loading** — burned EEPROM vs the Mega bootloader; how a
  program actually gets in.
- **The data-routing board** — selector-per-sink, the "no shared tri-state
  bus" topology, the '151 / '157 / '153 / '257 muxes. Half circuit, half
  concept.
- **Front-panel / state display** — latches and LED drivers showing PC, TOS,
  flags; the honesty of a slow machine you can watch.
- **Mini Blinky, the 4-bit testbed** — why you build a nibble-wide machine
  first, and what it de-risks.

## C. TTLSim (software walkthroughs)

- **TTLSim tour** — draw, wire, simulate; the pitch for the whole tool.
- **How the engine works** — four-state signals, driver strengths, propagation
  delays. The part most logic sims skip.
- **Adding a chip to the library** — and the parallel-list registration trap,
  framed as the cautionary tale.
- **Layers, annotations, and the schematic style guide** — making captures
  that read like the SVGs in these videos.
- **TTLSim → EasyEDA → PCB, end to end** — the wire-per-net export, the round
  trip to a real board.

## D. The GAL toolchain (our tools)

A genuinely uncommon topic — strong differentiator.

- **BlinkyJED** — logic equations to a JEDEC fuse map, and the fuse-for-fuse
  verification against WinCUPL. The verification-first ethos is the story.
- **The Arduino GAL tester** — exhaustively proving a GAL over every input
  before it goes near the board.
- **Burn & verify on the T48** — the physical workflow, including the UES
  gotcha.
- **A real GAL, start to finish** — take the ALU decode from equations through
  tester through burn into the running machine.

## E. Software toolchain

- **BlinkyASM** — writing and assembling a program for the stack machine.
- **Shifty** — the Thumby language, once that machine firms up.

## F. Concept / explainer

Short, evergreen, referenceable from the build videos ("as covered in…").

- **Reading a 74-series datasheet** — the function-table literacy the chip
  teaches already use, as its own lesson.
- **Tri-state, /OE, and bus contention** — and why this machine deliberately
  has no shared bus.
- **Clock domains, metastability, and the two-FF synchronizer** — generalising
  the UART and breakpoint themes.
- **Async assert, sync release** — the reset pattern, in isolation.
- **LS vs HC vs HCT** — the family-boundary level gotcha that bites everyone
  once.
- **The '181 and its carry polarity** — the Cn / Cn+4 inversion that needs an
  inverter between stages.
- **Why a dual-stack machine** — the architecture rationale; Forth-shaped,
  zero-operand.

## G. Thumby track (future)

Gated on ISA ratification.

- **Designing an ISA** — the decisions, the tradeoffs, what got cut.
- **The register file** — the '670 banks.
- **Carry lookahead in a GAL** — the GAL-182 work; a deep single-topic video.

---

## Dependencies

- I/O — IN / OUT  → needs the **UART module** filmed.
- Conditional branches  → needs **the ALU**.
- The ALU, and "A real GAL start to finish"  → lean on the **GAL toolchain**
  videos.
- First program  → needs most of the CPU arc, plus **BlinkyASM**.
- Engine-internals, library, export  → come after the **TTLSim tour**.
- Burn & verify, GAL tester  → come after **BlinkyJED**.
- Entire **Thumby track**, and **Shifty**  → gated on ISA ratification.
- Everything in the **CPU arc**  → builds on the already-filmed clock +
  breakpoint.

---

## Suggested release order

Interleaved so the channel isn't ten counters in a row — a build, a tool, and
an explainer rotate. Format tag in brackets. Dependencies above are respected.

1. **PC + IR — the fetch loop** — [CPU arc]
2. **Reading a 74-series datasheet** — [explainer] (supports every build video)
3. **TTLSim tour** — [tool]
4. **Unconditional branch** — [CPU arc]
5. **BlinkyJED — equations to fuse map** — [tool]
6. **Tri-state, /OE, and bus contention** — [explainer] (sets up data routing)
7. **The data stack** — [CPU arc]
8. **TTLSim → EasyEDA → PCB, end to end** — [tool]
9. **The ALU** — [CPU arc]
10. **The Arduino GAL tester** — [tool] (pairs with the ALU's decode GALs)
11. **Conditional branches** — [CPU arc] (needs the ALU, ✓ #9)
12. **Clock domains, metastability, the two-FF sync** — [explainer]
13. **Memory — LOAD / STORE** — [CPU arc]
14. **Adding a chip to the TTLSim library** — [tool]
15. **I/O — IN / OUT** — [CPU arc] (needs the UART, ✓ filmed)
16. **LS vs HC vs HCT** — [explainer]
17. **The return stack — CALL / RET** — [CPU arc] (capstone)
18. **A real GAL, start to finish** — [tool] (ties the toolchain to the
    running machine)
19. **First program** — [CPU arc]

Then, as the channel matures: the remaining **standalone modules** (program
memory, data-routing board, front-panel, Mini Blinky), the remaining
**explainers** (async/sync reset, the '181 carry, why a dual-stack machine),
**BlinkyASM**, and — once the ISA ratifies — the **Thumby track** and
**Shifty**.
