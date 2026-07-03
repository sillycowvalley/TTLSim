# Blinky — Incremental Build Plan

> Staged construction of the Blinky TTL CPU per `Blinky_TTL_CPU.md` (master reference).
> One machine, one board. There is no bring-up board and no half-scale variant: the
> increments below are **population and PLD-equation stages of the final machine**, not
> throwaway hardware.

## The no-rework rules

Everything hard to change is final from day one; everything cheap to change carries the
incrementality.

1. **All five 22V10 pinouts are final at stage 0**, exactly per the master doc's PLD
   complement table. A stage never moves a signal to a different pin — it only revises
   equations behind pins already assigned. Every revision goes through the full chain:
   BlinkyJED compile → WinCUPL fuse-for-fuse cross-check → JED into TTLSim → full IR
   sweep → JEDTester on silicon → fit.
2. **Structural buses are built full-width at first population.** NEXTPC (13-bit
   3-state, four drivers), the BRK-force '257 pair in the IR decode path, the PC-lane
   spare-bit (N/Z/C) wiring, and the 3:1 D-address mux all exist physically from the
   stage that first touches them, with not-yet-live inputs strapped inactive. Retrofitting
   a mux into a bus, or splicing a jam stage into the IR path, is rework; an unpopulated
   socket or a grounded strap is not.
3. **Every chip has its socket at layout time.** A stage populates sockets and burns
   PLDs; it never cuts traces.
4. **Each stage ends with a regression pass**: every prior stage's test program re-runs
   unmodified. New EEPROM image per stage, cumulative test suite.
5. **Only re-burn PLDs whose equations changed.** The stage tables below list exactly
   which of the five devices each stage revises.

Reset during development is Mega-driven `/RESET` (DS1813 in the stand-alone build).
Programs live in the two 28C64 EEPROMs — Harvard, no loader, the PC clear is the reset
vector.

---

## Stage 0 — Infrastructure

**Populate:** power distribution; clock oscillator + divider chain (including the 2×
tap the quarter-cycle WRPH provision will want — one gate held in reserve, not fitted);
run/halt/single-step controls; Mega `/RESET` header; all five 22V10 sockets; the NEXTPC
'139 PCSEL decoder; the hardware breakpoint ('688 ×2 on the 13-bit PC + '00 + '74).

**Verify:** clean clock at demo and MHz rates, single-step produces exactly one edge,
reset asserts/releases cleanly, breakpoint comparator toggles its FF on a jumpered
address match. No CPU exists yet — this stage is scope-and-meter work.

The breakpoint goes in first deliberately: it is already proven, and every later stage's
debugging is cheaper with halt-on-address available from the first fetch.

---

## Stage 1 — Fetch and absolute flow

The call/return spine, PC lane only.

**Populate:**

| Block | Parts |
| --- | --- |
| I-memory | 2× 28C64 |
| IR | '374 ×2, **plus the BRK-force '257 ×2 in the decode path** (BRK_FORCE held deasserted by PLD 5 until stage 8 — pass-through) |
| PC | '377 ×2 (13 of 16 bits) |
| PC adder | '283 ×4; offset mux '157 ×2 (B = constant +1 this stage) |
| NEXTPC drivers | adder / IR[12:0] / RTOS 3-state buffers ('139 Y0–Y2); vector buffer socket empty, Y3 unused |
| Return stack, PC lane | SRAM ×2 (16-bit lane; bits 15:13 data-in grounded until stage 5), RSP '191 ×2, RSP−1 decrementer '283 ×2, address mux '157 ×2, PC-lane data-in '257s (PC+1 path live; raw-PC and TOS inputs grounded) |
| PLDs | **3, 4, 5** — initial JEDs |

BP-lane SRAM sockets stay empty; BP-lane data-in nets are wired (rule 2) but undriven.

**Instructions live:** NOP, HALT, JUMP, CALL, RET.

**PLD equations this stage:** PLD 5 — PCSEL (00/01/10 only; interrupt macrocells hold
reset state, BRK_FORCE never asserts). PLD 3 — RSP_UD, /RSP_EN, /RSTK_CS, /RSTK_WE with
the ratified strobe form `/WE = !(push · /CLK)`, push qualified by RSP_EN. PLD 4 —
RDIN_SEL pinned to 00 (PC+1), everything else inert.

**Test program:** straight-line NOPs → JUMP forward/back → CALL/RET one level → nested
CALL to depth 8 → worst-case back-to-back CALL→RET. Watch on the panel: PC, IR, RTOS,
RSP. Pass criteria: RSP cycles up/down by single counts, RTOS always shows the true
return address (RSP−1 read), zero spurious writes under worst-case packing, HALT
freezes a settled state, breakpoint halts on a mid-program address.

---

## Stage 2 — Relative branch datapath, condition strapped

Verify the second-biggest new flow block in isolation, before flags exist.

**Populate:** the sign-extend side of the offset mux (IR[7:0] sign-extended onto the
adder B input). No new registers.

**Strap:** PLD 4's N, Z, C input pins to a 3-switch DIP header (final flag-FF outputs
connect here at stage 5 — the header sits in that net by design).

**PLDs revised:** **4** — final BSEL equations: `taken = (sel = 00) OR (flag XOR IR[8])`
with IR[10:9] selecting the flag. These equations are already their stage-5 final form;
only the flag *sources* are switches instead of flip-flops.

**Instructions live:** BRA, BEQ, BNE, BCS, BCC, BMI, BPL.

**Test program:** BRA forward, BRA backward (loop), each conditional tested **both
taken and not-taken** by toggling the strapped flag switch mid-single-step — the strap
is a feature: both polarities of all six conditions and both inverse-pair members get
exercised before a single '181 exists. Verify offset 0 falls through, offset −128 and
+127 reach correctly, and the far-conditional idiom (BNE +1 / JUMP) lands.

Pass criteria: taken branch = PC+1+offset, not-taken = PC+1, in every case; conditional
branches never leave PCSEL = 00 (BSEL alone decides).

---

## Stage 3 — Literal path, TOS, NOS

First data-side state.

**Populate:**

| Block | Parts |
| --- | --- |
| TOS | '194 ×2 + '153 fill mux (serial-pin wiring per master doc: DSR high-'194, DSL low-'194, Q7/Q0 end taps) |
| NOS | '374 |
| PLDs | **1, 2** — initial JEDs |

**Instructions live:** PUSH #imm, SWAP — plus, optionally, the five shifts *without
flag capture* (SHL/SHR/ASR/ROL/ROR are '194-mode + fill-mux operations; the '181s and
flag FFs play no part in the data movement). C simply isn't latched yet.

**PLD equations this stage:** PLD 1 — TOS_M1..0 (hold / shift / load), fill-mux
don't-cares confirmed. PLD 2 — TOS_SRC (010 = IR[7:0] and 001 = NOS paths live; ALU,
D-mem, I/O, RTOS inputs strapped), NOS_LD/NOS_SRC for the SWAP exchange.

**Test program:** PUSH walking values, SWAP round-trip, then the classic walking-bit:
PUSH #1, SHL in a loop, watch the bit traverse the TOS LEDs; ASR on 0x80 to confirm
sign fill; ROL/ROR wrap. This is also where the split of IR[7:0] onto the data path vs
IR[12:0] onto NEXTPC is physically confirmed — the same IR bits feed both, on different
buses.

---

## Stage 4 — Data stack and the STK family

**Populate:**

| Block | Parts |
| --- | --- |
| Data stack | SRAM, DSP '191 ×2 |
| TUCK write-select | '257 ×2 on stack data-in (NOS vs TOS) |
| Return-stack extras | TOS zero-extend input to the PC-lane data-in '257s goes live |

**PLDs revised:** **3** — DSP_UD, /DSP_EN, /DSTK_WE (same phase-gated strobe form),
/DSTK_CS wired from /DSP_EN (no pin). **2** — NOS_SRC pop path (stack[DSP−1] read),
SDIN_SEL for TUCK, TOS_SRC 101 (RTOS[7:0]). **4** — RDIN_SEL 10 (>R). The pop-bit
optimization (op[11] = 1 ⇔ ΔDSP −1 in families 2/3/4) lands here as the DSP_UD cube.

**Instructions live:** DUP, OVER, TUCK, DROP, NIP, >R, R>, R@.

**Test program:** push a known sequence deep enough to spill through NOS into SRAM,
pop it back, verify LIFO order on TOS/NOS/DSP LEDs. Exercise each of the eight ops
individually, then OVER/TUCK/NIP in combination (the Improvement-1 set). >R / R>
round-trip data through the return stack's PC lane; R@ must read without RSP moving
(/RSTK_CS asserted, /RSP_EN not — the one opcode where select and count-enable differ).

**Mandatory regressions:** the two historical return-stack failure modes, re-aimed at
the data stack — (a) pop reads RSP/DSP−1, not the raw pointer; (b) no spurious write
during the first half-cycle while the IR settles. Same silicon pattern, same failure
modes; any failure isolates to wiring.

---

## Stage 5 — ALU and flags

**Populate:**

| Block | Parts |
| --- | --- |
| ALU | '181 ×2 + the Cn ripple inverter ('04; HCT04 only if the LS-181 backup is ever fitted) |
| Flags | N/Z/C '74s + the '157 restore mux (RTI side dead until stage 8 — inputs grounded, load-enable never asserts) |
| Lane wiring | flag FF outputs onto PC-lane data-in bits 15:13 (always driven — replaces the stage-1 grounds); flag FF outputs onto the stage-2 strap header, **switches removed** |

**PLDs revised:** **1** — ALU_S3..0, ALU_M, /ALU_CN, C_SRC; C feedback in on pin 14
(ADC). **2** — NZ_WE, C_WE per-flag enables; TOS_SRC 000 (ALU F) live. **4** — no
equation change; its N/Z/C inputs now see real flags.

**Instructions live:** all 14 ALU sub-ops — ADD, ADC, SUB, XOR, NOT, TST, SHL, SHR
(now with C capture), AND, CMP, ASR, OR, ROL, ROR. Conditional branches now run against
live flags.

**Test program:**
- Arithmetic: ADD/SUB with and without carry/borrow; ADC chain summing two 16-bit
  values from PUSHed bytes (C survives the intervening stack ops — the per-flag-enable
  proof).
- Carry preservation: SUB setting C, then AND/OR/XOR/NOT/TST, then BCS — C must
  survive all five logic ops.
- CMP: non-destructive, both operands intact afterward, Z on equal, C on TOS ≥ NOS.
- Shifts: repeat stage 3's walking bit, now confirming C = the shifted-out bit and BCS
  reseeding the walk.
- The countdown loop: PUSH #N / SUB #1-equivalent / BNE — first fully self-checking
  program; wrong flag logic fails to terminate, visibly.

Because stage 2 verified the branch datapath exhaustively with strapped flags, any
failure here is in the flag capture, not the adder or BSEL.

---

## Stage 6 — Data memory: LOAD, STORE, FETCH

**Populate:**

| Block | Parts |
| --- | --- |
| D-memory | 62256 / CY7C199-class (8K used) |
| D-address mux | full 3:1, 13-bit (~4 chips) — super-op IR[12:0] and TOS-zero-extended inputs live; **BP-relative input grounded** until stage 7 |

**PLDs revised:** **2** — /DMEM_CS, TOS_SRC 011 (D-mem data out). **3** — /DMEM_WE
(phase-gated, same form as the stack strobes). **4** — DADDR_S1..0 (00 and 01 live).

**Instructions live:** LOAD addr, STORE addr (super-ops), FETCH (stack form, low-256
reach — exercises the TOS side of the address mux).

**Test program:** STORE/LOAD round-trip at low and high addresses (13-bit reach —
address above 0xFF proves the super-op path is genuinely wide); FETCH against an
address computed at run time; then a self-checking memory fill: write pattern i^0xFF
across a block in a stage-5 conditional loop, read back, compare with CMP/BNE, land on
a pass/fail HALT address the breakpoint distinguishes.

---

## Stage 7 — BP and call frames

**Populate:**

| Block | Parts |
| --- | --- |
| BP register | '377 ×2, async-load-zero on `/RESET` (same idiom as RSP) |
| BP source mux | '157 ×4 (ENTER adder vs saved-BP lane) |
| BP adder | '283 ×4 — computes BP+k (ENTER) and the BP-relative local address (sign-extended IR[7:0], same pattern as the branch offset) |
| Return stack, BP lane | SRAM ×2 into the stage-1 sockets; lane data-in already wired (BP bits 12:0 always driven; I and B bits grounded until stage 8) |
| D-address mux | BP-relative input replaces its ground strap |

**PLDs revised:** **4** — BP_LD (ENTER/RET/RTI only — R>/R@ must never load BP),
BP_SRC, DADDR_S 10. **3** — the BP-lane /WE joins the phase-gated strobe set (rides
/RSTK_WE — both lanes write as one unit).

**Instructions live:** ENTER k, LOCAL@ off, LOCAL! off. RET's semantics widen: pop
{PC, BP}.

**Test program, in order:**
1. **Regression first:** the entire stage-1 flow suite, unmodified. RET now pops both
   lanes through verified-then-widened hardware; plain nested CALL/RET must still pass
   before any frame op runs.
2. ENTER k / LOCAL! / LOCAL@ round-trip: write locals at negative offsets, read them
   back out of order — the random-access property.
3. Two-level nesting: caller's locals intact after the callee returns (automatic BP
   restore — the reason FREE/LEAVE don't exist).
4. **Acceptance: Towers of Hanoi.** The workload the subsystem exists for. Target
   ~18 cycles/node; BP LEDs breathing with recursion depth is the visual pass.

---

## Stage 8 — Interrupts

**Populate:**

| Block | Parts |
| --- | --- |
| Vector driver | the stage-1 empty buffer socket; address lines tied per spec (bit 1 = NMI_PEND, bit 0 = NMI_PENDB), enabled by the PCSEL '139's Y3 |
| Lane bits | I flag and B ( = bare IR[10]) onto the BP-lane data-in, replacing grounds; ILANE wire from the BP-lane SRAM to PLD 5 |
| Flag restore | the stage-5 '157's stack-read inputs connected; RTI load-enable live |
| Request lines | IRQ (level) and NMI (edge) headers; panel PEND LEDs |

**PLDs revised:** **5** — the registered macrocells go live: IBAR (I stored inverted so
async-reset = I masked), NMI_SYNC + NMI_PEND (true edge latch), IRQ_PEND, BRK_FORCE =
NMI_PEND # IRQ_PEND·IBAR, PCSEL 11. The stage-1 BRK-force '257s in the IR path finally
earn their keep — jamming INT (0x0300), not BRK. **4** — CLK_RUN (NMI overrides, waking
HALT), RDIN_SEL 01 (raw PC on hardware entry).

**Instructions live:** BRK, RTI, SEI, CLI.

**Bring-up order inside the stage — synchronous first:**
1. **BRK** — software-triggered, zero async hazard. Verifies the whole vector path,
   dual-lane state push (PC+1, flags, BP, I, B=1), and handler entry at 0x0001.
2. **RTI** — full restore: PC, BP, N/Z/C via the '157, I via ILANE. A BRK/RTI pair
   around a flag-scrambling handler must return with the pre-BRK flags intact.
3. **IRQ** — level line, maskability: assert with I=1 (nothing), CLI (taken), verify
   raw-PC push and B=0 — the displaced instruction re-executes on return.
4. **NMI** — edge pend (held line must not retrigger), priority over simultaneous IRQ,
   vector 0x0002, and the HALT wake.

Pass criterion for the whole stage: an interrupt round-trip in 2 cycles, visible as two
settled front-panel states in single-step — pending LED, entry, handler, RTI.

---

## Stage 9 — I/O and final integration

**Populate:** '138 port decode, port-number '157 (TOS vs IR[7:0]), the 8×8 LED matrix
(8× '374 row latches, ports 0–7), '244 switch port, '374 status port.

**PLDs revised:** **3** — /IO_WR (last phase-gated strobe). **4** — /IO_RD, IOADDR_SEL.
**2** — TOS_SRC 100 (I/O data in).

**Instructions live:** IN (stack form), IN #port, OUT #port.

**Test programs:** port round-trip (OUT then IN on a loopback); the DUP…OUT broadcast
idiom across the matrix rows; switches → compute → matrix. Then the full-suite
regression: every stage's program, one image. Wire a timer-class device onto IRQ and
the panic button onto NMI, and the demo program — Hanoi on the matrix, interruptible —
is the machine's acceptance test.

---

## Stage / PLD revision matrix

| Stage | 1 (ALU/shift) | 2 (stack-top/D-mem) | 3 (pointers/strobes) | 4 (BP/IO/branch) | 5 (flow/int) |
| --- | --- | --- | --- | --- | --- |
| 1 flow | — | — | initial | initial | initial |
| 2 branches | — | — | — | rev | — |
| 3 TOS/NOS | initial | initial | — | — | — |
| 4 data stack | — | rev | rev | rev | — |
| 5 ALU/flags | rev | rev | — | — | — |
| 6 D-mem | — | rev | rev | rev | — |
| 7 BP/frames | — | — | rev | rev | — |
| 8 interrupts | — | — | — | rev | rev |
| 9 I/O | — | rev | rev | rev | — |

PLD 5 is burned exactly twice — its interrupt state machine is all-or-nothing, and its
stage-1 form differs only in holding BRK_FORCE and PCSEL 11 inert. PLD 1 twice. The
strobe device (PLD 3) revises most often, as expected: every new memory adds a strobe.

## Assembler tracking

BlinkyASM stages alongside the hardware: each stage's image may use only that stage's
live mnemonics, and the assembler should reject later-stage mnemonics until enabled —
a mis-staged test program should fail at assembly, not on the bench.
