# R181 — 16-Bit Register-Only RISC

A three-address, fully microcoded 16-bit CPU with no RAM: sixteen 16-bit
registers, a 16-bit-wide program EEPROM read as data ROM for tables, and
16-bit I/O ports. Built from three pre-existing assets plus ~27 new ICs:

- **Register File Module** — 16 registers × 16 bits, two read ports
  (populated: 32 × '670, all jumpers 16R)
- **Blinky Clock Module v3** — clock, T counter (T0–T15), FETCH, halt, reset
- **4 × 74181** — the ALU, ripple carry across four slices

Design theses:

1. **The PC is r15 in the register file.** No counter chips, no MAR. Jumps
   are writes to r15; PC increment is the ALU computing A plus 1. The
   machine is **word-addressed**: one instruction or literal per 16-bit
   memory word, so fetch is a single state and 16-bit immediates need no
   byte assembly.
2. **The clock module is the sequencer.** T0–T15 from H3; microcode ends
   each instruction with /TRST. Instructions may use up to 16 states; the
   longest defined (LSL #14) uses exactly 16.
3. **Everything is microcode**, including reset, conditional branching,
   variable-length shifts, and the multiply step.

---

## 1. Registers and conventions

| Register | Role |
|---|---|
| r0–r12 | General purpose |
| r13 | **Microcode temporary** — trashed by branches-taken, DJNZ-taken, LD, and LDW; also the implicit multiplier register for MSTEP |
| r14 | Link register by convention (JSR r14, rn / RET = MOV r15, r14) |
| r15 | Program counter (word address) |

**Flags** (2 bits in an HC377, updated selectively, recirculated otherwise):

| Flag | Stored form | Source |
|---|---|---|
| Z | **/ZF** — the '688 chain's pin level (0 = result was zero) | Cascaded HC688 pair watching the bus |
| C | **CF** — the literal Cn+4 pin level of slice 3 (0 = carry, active-high data convention) | ALU carry chain |

Flags are stored as *pin levels*; all logical-sense inversion lives in the
microcode images (branch condition pages, CSEL chaining). Storing CF as the
pin level means multi-precision chaining (ADC/SBC, ROLC, MSTEP) wires the
flag straight back into Cn with no inverters.

After reset: Z = 1 (set), C undefined.

---

## 2. Datapath

One 16-bit data bus, BUS[15:0], with 10 k pulldowns (2 × 8 SIP). An undriven
bus or undriven upper byte reads zero — used for reset and zero-extension.

**Bus drivers** (at most one owner of any bit per state):

| Driver | Enabled by | Bits |
|---|---|---|
| Program EEPROM pair | MEMOE | 15:0 |
| ALU F via 2 × HCT373 | FOE | 15:0 |
| IMM '541 (inputs = IR[7:0]) | IMMEN | 7:0 |
| SEXT '541 (all inputs = IR[7]) | SEXT | 15:8 |
| IN port (2 × HC541) | INEN | 15:0 |

IMMEN alone = zero-extended imm8 (pulldowns supply the top). IMMEN + SEXT =
sign-extended imm8.

**Bus listeners:**

| Listener | Captures |
|---|---|
| Register file D[15:0] | During CLK-low when WE (module's own write gating) |
| IR (2 × HC377) | Falling CLK edge, /CE = /FETCH |
| Zero detect (2 × HC688 cascaded) | Combinational; result latched into /ZF when FLGZ |
| OUT port (2 × HC377 → LEDs etc.) | Rising CLK edge when /OUTSTB |

**Fixed combinational wiring:**

- QA[15:0] → 181 A inputs; QB[15:0] → 181 B inputs **and** program-memory
  address A[14:0] (QB15 unused; the 32K-word space mirrors)
- Slice-to-slice ripple Cn+4 → Cn (a '182 footprint is provided; at ~120 ns
  worst-case ripple it is optional at the design clock)
- 181 F[15:0] → HCT373 pair inputs

Both Q ports feed only combinational inputs, per the register-file rule.

### The F-stage: HCT373, transparent-high

The ALU output reaches the bus through two HCT373s with **LE = CLK,
/OE = FOE**. During CLK-high the latch is transparent — F flows to the bus
and settles. At the falling edge it freezes, and stays frozen across the
entire low phase while the register file commits the write.

This is what makes read-modify-write legal. Every writeback that reads its
own destination (PC increment, LSL, INC, MSTEP) would otherwise form a
combinational loop through the module's documented write-through: the write
window opens, Q follows D, the ALU recomputes, D changes, repeat. The '373
cuts the loop *and* satisfies the write contract to the letter — D is stable
before CLK falls and held until it rises. (The '373 also performs the
LS→HC level translation if the ALUs are LS181s.)

Consequence: consecutive RMW states are safe — state N's write completes in
N's low phase; state N+1 reads the new value. Shift microcode relies on this.

### Register-address field muxes

WADDR, AADDR and BADDR each come from a pair of HC153s (4-bit, 4-way),
steered by 2-bit microcode fields:

| Select | ASEL | BSEL | WSEL |
|---|---|---|---|
| 0 | rd = IR[11:8] | rt/rn = IR[3:0] | rd = IR[11:8] |
| 1 | rs = IR[7:4] | r13 (1101) | r13 (1101) |
| 2 | r13 (1101) | r15 (1111) | r15 (1111) |
| 3 | r15 (1111) | (0) | (spare) |

This is why one microword serves all sixteen registers: the ROM routes
fields, it doesn't decode them.

### Carry-in select and flag update

Cn source, via one HC153 section (CSEL):

| CSEL | Cn pin | Meaning |
|---|---|---|
| 0 | 1 | No carry |
| 1 | 0 | Inject carry (plus-1, true subtract) |
| 2 | CF | Chain the stored carry (ADC, SBC, ROLC, MSTEP) |
| 3 | 1 | Reserved |

There are no CLC/SEC instructions — every microstate picks its own carry
source, which is strictly more powerful.

Z and C update independently: the flag '377 clocks every rising edge, and
one HCT153 (HCT: its C input is an LS-level Cn+4 pin) selects new-value or
recirculate per bit, steered by FLGZ and FLGC. Arithmetic updates both;
logic ops update Z and **preserve C**; moves, loads and I/O touch neither.

---

## 3. Instruction formats and sequencing

Three formats, one word each (LDW takes a second word as its operand):

```
ALU / register:   [op:4][rd:4][rs:4][rt:4]
Immediate/branch: [op:4][rd:4][ imm8  ]        (branches: rd field unused)
Shift:            [op:4][rd:4][n:4][----]
Escape:           [1111][rd:4][sub:4][rn:4]
```

Every instruction begins:

| T | Name | Action |
|---|---|---|
| T0 | FETCH | BSEL = r15, MEMOE; mem[PC] → BUS; IR captures at the falling edge |
| T1 | PCINC | r15 ← r15 + 1 (ASEL = WSEL = r15, A plus 1, FOE, WE) |
| T2… | EXEC | Instruction-specific, /TRST in the final state |

### Microcode discipline rules

1. **All T = 0 words identical.** IR changes at T0's falling edge, so the
   back half of T0 runs under the new instruction's T = 0 entry. Every
   {IR, T=0} location holds μFETCH.
2. **Transparent-field pages identical.** IR[7:4] is a ROM address input.
   For opcodes where those bits are an rs field or immediate bits rather
   than microcode steering (all ALU3 ops, MOVI, branches, DJNZ), all 16
   pages of that opcode must be programmed identically. Only shifts (n) and
   the escape group (sub) may differ across IR[7:4].
3. **16-state ceiling.** /TRST must fire by T15. LSL/ROLC cap n at 14 for
   this reason.

---

## 4. Microcode ROM

Three 28C256s in parallel — address map (15 bits, exactly full):

| ROM address | Signal |
|---|---|
| A0–A3 | T[3:0] (H3) |
| A4–A7 | IR[7:4] (sub-opcode / shift count / rs / imm low nibble) |
| A8–A11 | IR[15:12] (major opcode) |
| A12 | /ZF (stored pin level) |
| A13 | CF (stored pin level) |
| A14 | RST (H2.3) |

### Control word (24 bits, exactly full)

**ROM 0 — operand routing**

| Bit | Signal |
|---|---|
| 0–1 | ASEL |
| 2–3 | BSEL |
| 4–5 | WSEL |
| 6 | WE |
| 7 | IMMEN |

**ROM 1 — ALU**

| Bit | Signal |
|---|---|
| 0–3 | S0–S3 |
| 4 | M (1 = logic) |
| 5–6 | CSEL |
| 7 | SEXT |

**ROM 2 — system** (stored in pin polarity; tables below use the active sense)

| Bit | Signal |
|---|---|
| 0 | /MEMOE |
| 1 | /FOE ('373 output enable) |
| 2 | FLGZ (1 = latch Z this state) |
| 3 | FLGC (1 = latch C this state) |
| 4 | /INEN |
| 5 | /OUTSTB |
| 6 | /TRST (single driver, direct to H3.7) |
| 7 | HREQ (NPN pulling /HALTREQ — pull-low only, shares the panel button's net) |

### 181 function codes used (active-high data)

| Operation | M | S3–S0 | F |
|---|---|---|---|
| Pass A | 1 | 1111 | A |
| Pass B | 1 | 1010 | B |
| NOT B | 1 | 0101 | /B |
| AND | 1 | 1011 | A·B |
| OR | 1 | 1110 | A + B |
| XOR | 1 | 0110 | A ⊕ B |
| A plus 1 | 0 | 0000 (Cn = 0) | A PLUS 1 |
| A plus B | 0 | 1001 | A PLUS B (PLUS 1 if Cn = 0) |
| A minus B | 0 | 0110 (Cn = 0) | A MINUS B (minus 1 more if Cn = 1) |
| A minus 1 | 0 | 1111 (Cn = 1) | A MINUS 1 |
| A plus A | 0 | 1100 | shift left (PLUS 1 if Cn = 0 — rotate-in) |

---

## 5. Microword reference

Every distinct control word. Register selects are shown as the mux source
they route; fields not listed are inactive (WE = 0, no drivers, no flag
update, no TRST).

| # | Word | Routing | ALU | System | Purpose |
|---|---|---|---|---|---|
| 1 | **μFETCH** | BSEL=r15 | — | MEMOE | mem[PC] → BUS; IR captures at the fall (hardware) |
| 2 | **μPCINC** | ASEL=r15, WSEL=r15, WE | S=0000, M=0, CSEL=inject | FOE | r15 ← r15 + 1 |
| 3 | **μALU3** | ASEL=rs, BSEL=rt, WSEL=rd, WE | per op — see §6 table | FOE, FLGZ, FLGC per op, TRST | rd ← rs OP rt |
| 4 | **μSHIFT** | ASEL=rd, WSEL=rd, WE | S=1100, M=0, CSEL=none (LSL) or CF (ROLC) | FOE, FLGZ, FLGC; TRST on final state | rd ← rd + rd (+ C); C ← bit 15 out |
| 5 | **μMOVI** | WSEL=rd, WE, IMMEN | — | TRST | rd ← zext(imm8) |
| 6 | **μBSTAGE** | WSEL=r13, WE, IMMEN | SEXT | — | r13 ← sext(imm8) |
| 7 | **μBADD** | ASEL=r15, BSEL=r13, WSEL=r15, WE | S=1001, M=0, CSEL=none | FOE, TRST | r15 ← r15 + r13 (branch lands) |
| 8 | **μEND** | — | — | TRST | Ends instruction (fall-through path) |
| 9 | **μDEC·F** | ASEL=rd, WSEL=rd, WE | S=1111, M=0, CSEL=none | FOE, FLGZ | rd ← rd − 1; Z latched, **C preserved** (DJNZ T2) |
| 10 | **μMOV** | BSEL=rn, WSEL=rd, WE | S=1010, M=1 | FOE, TRST | rd ← rn |
| 11 | **μMVN** | BSEL=rn, WSEL=rd, WE | S=0101, M=1 | FOE, FLGZ, TRST | rd ← /rn |
| 12 | **μINCA** | ASEL=rd, WSEL=rd, WE | S=0000, M=0, CSEL=inject | FOE, FLGZ, FLGC, TRST | rd ← rd + 1 (also NEG's second state) |
| 13 | **μDECA** | ASEL=rd, WSEL=rd, WE | S=1111, M=0, CSEL=none | FOE, FLGZ, FLGC, TRST | rd ← rd − 1 |
| 14 | **μCMP** | ASEL=rd, BSEL=rn | S=0110, M=0, CSEL=inject | FOE, FLGZ, FLGC, TRST | flags of rd − rn, no write |
| 15 | **μLDSTAGE** | BSEL=rn, WSEL=r13, WE | — | MEMOE | r13 ← mem[rn] |
| 16 | **μLDWSTAGE** | BSEL=r15, WSEL=r13, WE | — | MEMOE | r13 ← mem[PC] (literal word) |
| 17 | **μPASS13** | ASEL=r13, WSEL=rd, WE | S=1111, M=1 | FOE, TRST | rd ← r13 (destage) |
| 18 | **μLINK** | ASEL=r15, WSEL=rd, WE | S=1111, M=1 | FOE | rd ← r15 (return address) |
| 19 | **μJTGT** | BSEL=rn, WSEL=r15, WE | S=1010, M=1 | FOE, TRST | r15 ← rn |
| 20 | **μMS1** | ASEL=r13, WSEL=r13, WE | S=1100, M=0, CSEL=none | FOE, FLGC | r13 ← r13 + r13; C ← multiplier MSB |
| 21 | **μMS2** | ASEL=rd, WSEL=rd, WE | S=1100, M=0, CSEL=none | FOE | rd ← rd + rd (product ×2, C preserved) |
| 22 | **μMS3** | ASEL=rd, BSEL=rn, WSEL=rd, WE | S=1001, M=0, CSEL=none | FOE, TRST | rd ← rd + rn (conditional add) |
| 23 | **μOUT** | BSEL=rn | S=1010, M=1 | FOE, OUTSTB, TRST | OUT latch ← rn at the closing rising edge |
| 24 | **μIN** | WSEL=rd, WE | — | INEN, TRST | rd ← IN port |
| 25 | **μHLT** | — | — | HREQ, TRST | Pull /HALTREQ; clock freezes here, resumes past it |
| 26 | **μRESET** | WSEL=r15, WE | — | FLGZ | All drivers off → BUS = 0x0000 via pulldowns → r15 ← 0, Z ← set. Fills the **entire** RST = 1 half regardless of IR/T/flags. |

Word 1 fills all {any IR, T=0, RST=0} locations; word 2 all T=1 locations;
word 26 the whole RST=1 half. Words 3–25 populate T ≥ 2 per instruction.

---

## 6. Instruction reference

Cycle counts include the two-state fetch/PCINC overhead.

### ALU class — `[op][rd][rs][rt]`, rd ← rs OP rt, 3 cycles

T0 μFETCH · T1 μPCINC · T2 μALU3 with:

| Op | Mnemonic | M | S | CSEL | FLGZ | FLGC |
|---|---|---|---|---|---|---|
| 0000 | ADD rd,rs,rt | 0 | 1001 | none | 1 | 1 |
| 0001 | ADC rd,rs,rt | 0 | 1001 | CF | 1 | 1 |
| 0010 | SUB rd,rs,rt | 0 | 0110 | inject | 1 | 1 |
| 0011 | SBC rd,rs,rt | 0 | 0110 | CF | 1 | 1 |
| 0100 | AND rd,rs,rt | 1 | 1011 | — | 1 | 0 |
| 0101 | OR rd,rs,rt | 1 | 1110 | — | 1 | 0 |
| 0110 | XOR rd,rs,rt | 1 | 0110 | — | 1 | 0 |

CF is stored as the Cn+4 pin level, so CSEL=CF chains ADC/SBC/multi-precision
sequences with zero glue: ADD then ADC then ADC… and SUB then SBC then SBC…
just work. Logic ops preserve C.

### Shifts — `[op][rd][n][----]`, 2 + n cycles, n = 1…14

| Op | Mnemonic | Effect |
|---|---|---|
| 0111 | LSL rd,#n | n × (rd ← rd + rd); C = last bit out |
| 1000 | ROLC rd,#n | n × (rd ← rd + rd + C); 17-bit rotate through C |

T0 μFETCH · T1 μPCINC · T2…T(1+n) μSHIFT (TRST on the last). The shift
count is IR[7:4], a ROM address input — the microcode image *is* the
variable length. n = 0 and n = 15 are programmed as μEND at T2 (NOP); the
assembler splits larger shifts. LSR is synthesised: `ROLC #(17−k)` with the
first state's CSEL forced to inject-0 in the image — so `LSR rd,#k` for
k ≥ 3 assembles to one instruction; k = 1, 2 need two.

### Immediate and branches — `[op][rd][imm8]`

| Op | Mnemonic | Effect | Cycles |
|---|---|---|---|
| 1001 | MOVI rd,#imm8 | rd ← zext(imm8) | 3 |
| 1010 | BEQ ±off | if Z: PC ← PC + sext(off) | 3 nt / 4 taken |
| 1011 | BNE ±off | if /Z | 3 / 4 |
| 1100 | BCS ±off | if C | 3 / 4 |
| 1101 | BCC ±off | if /C | 3 / 4 |
| 1110 | DJNZ rd,±off | rd ← rd − 1; if rd ≠ 0 branch | 4 fall-through / 5 taken |

Offsets are **word** offsets relative to the *following* instruction
(PC has already incremented). Range −128…+127 words.

Per-T steps:

| T | MOVI | Bcc (false) | Bcc (true) | DJNZ |
|---|---|---|---|---|
| T0 | μFETCH | μFETCH | μFETCH | μFETCH |
| T1 | μPCINC | μPCINC | μPCINC | μPCINC |
| T2 | μMOVI | μEND | μBSTAGE | μDEC·F (latch Z, preserve C) |
| T3 | | | μBADD | Z: μEND · /Z: μBSTAGE |
| T4 | | | | μBADD |

Bcc's T2 and DJNZ's T3 are steered by the flag address bits — the ROM *is*
the condition evaluator. DJNZ reads the Z latched one edge earlier at the
end of its own T2, and deliberately leaves C alone so it can drive
multi-precision loops. Taken branches trash r13.

### Escape group — `[1111][rd][sub][rn]`

| sub | Mnemonic | Effect | Flags | Cycles | T2 | T3 | T4 |
|---|---|---|---|---|---|---|---|
| 0000 | MOV rd,rn | rd ← rn | — | 3 | μMOV | | |
| 0001 | MVN rd,rn | rd ← /rn | Z | 3 | μMVN | | |
| 0010 | NEG rd,rn | rd ← −rn | Z, C | 4 | μMVN (no TRST) | μINCA | |
| 0011 | INC rd | rd ← rd + 1 | Z, C | 3 | μINCA | | |
| 0100 | DEC rd | rd ← rd − 1 | Z, C | 3 | μDECA | | |
| 0101 | CMP rd,rn | flags of rd − rn | Z, C | 3 | μCMP | | |
| 0110 | LD rd,(rn) | rd ← mem[rn] | — | 4 | μLDSTAGE | μPASS13 | |
| 0111 | LDW rd | rd ← next word | — | 5 | μLDWSTAGE | μPCINC | μPASS13 |
| 1000 | JSR rd,rn | rd ← return, PC ← rn | — | 4 | μLINK | μJTGT | |
| 1001 | MSTEP rd,rn | one multiply step | C consumed, Z undefined | 5 | μMS1 | μMS2 | C: μMS3 · /C: μEND |
| 1010 | OUT rn | OUT latch ← rn | — | 3 | μOUT | | |
| 1011 | IN rd | rd ← IN port | — | 3 | μIN | | |
| 1100 | HLT | freeze clock; resumes at next instruction | — | 3 | μHLT | | |
| 1101–1111 | — | reserved (μEND) | — | 3 | μEND | | |

Notes:

- **LD/LDW stage through r13** then destage with pass-A. This deliberately
  breaks the *other* combinational loop — the memory-address path — so
  `LD r15,(rn)` (computed jump through a ROM table) and `LDW r15` (absolute
  jump: the target is simply the next word) are fully legal, no assembler
  landmines.
- **JSR**: rd captures r15, which already points past the instruction, so
  the link is exactly the return address. Convention `JSR r14, rn`;
  RET = `MOV r15, r14`.
- **MSTEP** (MSB-first shift-add; multiplier in r13, multiplicand rn,
  product rd): μMS1 shifts r13 left, its MSB landing in C; μMS2 doubles the
  product; if C, μMS3 adds the multiplicand. A 16×16→16 multiply is
  `MOVI/LDW r13, m` then **16 straight-line MSTEPs** — ~80 cycles, every
  step panel-single-steppable. Nothing else touches r13 mid-sequence.
- **HLT** asserts /HALTREQ as soon as its T2 word settles; the module
  freezes the clock asynchronously. Resuming (NEXT CYCLE / RUN) completes
  T2, /TRST fires, execution continues — a software breakpoint that
  co-operates with the panel.

---

## 7. Programming idioms (life without RAM)

| Want | Write |
|---|---|
| 16-bit constant | `LDW rd` + literal word inline — one instruction |
| Absolute jump | `LDW r15` + target word |
| Call / return | `JSR r14, rn` / `MOV r15, r14`; nested calls save r14 into a spare register first (caller-saves — 13 free registers goes surprisingly far) |
| Data tables | Assemble into program ROM; `LD rd,(rn)` with computed rn — fonts, sine tables, jump tables |
| Loop | `MOVI r1,#n` … body … `DJNZ r1, loop` |
| Right shift | `LSR rd,#k` → assembler emits the ROLC #(17−k) form |
| NOP | any reserved escape, or `MOV r0, r0` |
| Bit test | AND/CMP + BEQ/BNE, masks from MOVI/LDW |

---

## 8. Chip list

| Qty | Part | Role |
|---|---|---|
| 4 | 74181 | ALU (LS or CD74HC; ripple carry) |
| (1) | 74182 | Optional look-ahead — footprint provided |
| 2 | HCT373 | F-stage: transparent CLK-high, latched CLK-low, /OE = FOE |
| 2 | HC377 | IR (clock = /CLK via HC04, /CE = /FETCH) |
| 1 | HC377 | Flags (/ZF, CF; 6 spare bits) |
| 1 | HCT153 | Flag update/recirculate mux (HCT — Cn+4 is an LS-level input) |
| 1 | HC153 | Cn source mux (CSEL) |
| 6 | HC153 | AADDR / BADDR / WADDR field muxes |
| 2 | HC688 | 16-bit zero detect, cascaded via /G |
| 2 | HC541 | Immediate drivers (IMM low byte, SEXT high byte) |
| 3 | 28C256 | Microcode (24-bit control word) |
| 2 | 28C256 | Program memory, 32K × 16 |
| 1 | HC04 | IR clock inversion (spares free) |
| 2 | HC377 | OUT port (optional but traditional — LEDs) |
| 2 | HC541 | IN port |

**27 core, 31 with I/O**, plus 16 bus pulldowns and the /HALTREQ NPN.
/TRST and all port enables drive directly from ROM 2 in pin polarity — the
glue budget is one inverter.

---

## 9. Clock module hookup

| Pin | Connection |
|---|---|
| H2.2 CLK | Register file CLK, '373 LE, IR clock (inverted), flag/OUT '377 clocks |
| H2.3 RST | Microcode ROM A14 |
| H2.6 /HALTREQ | NPN collector (μHLT), shared with the panel button |
| H3.2–5 T0–T3 | Microcode ROM A0–A3 |
| H3.6 FETCH | IR /CE (as /FETCH) |
| H3.7 /TRST | ROM 2 bit 6, direct (single driver) |

Multi-cycle integration per the module doc. Power-up lands in STEP MODE;
reset is entirely the A14 mechanism — the supervisor holds RST across two
edges, T clears, μRESET writes 0x0000 into r15 each cycle, and release
lands in T0 fetching from word address 0. The panel's T LEDs directly show
microstate — a 14-state shift is watchable.

---

## 10. Timing

The register file's obligation: D settles within CLK-high; the ≥100 ns low
phase belongs to the write. Worst datasheet-maxima paths from CLK rise:

| Path | Budget |
|---|---|
| Execute: T/IR/flags → μROM (150) → '153 (25) → Q cross-bank (70) → 181 ripple (~120) → '373 transparent (20) | ~385 ns |
| Fetch: μROM (150) → '153 (25) → QB (70) → program EEPROM (150) → IR setup (20) | ~415 ns |

≈450 ns high + 100 ns low ⇒ **~1.8 MHz ceiling; call it 1 MHz with margin**,
using the long-high asymmetric duty the clock module recommends. Stuffing
the '182 pulls the execute path below the fetch path if it ever matters.
All control outputs change only off the T/IR/flag transitions at CLK rise,
so WE, WADDR and the mux selects are stable across every low phase — the
write contract holds by construction.

---

## 11. Bring-up sketch

1. Clock module + microcode ROMs + IR, program sockets empty: pulldowns
   feed IR 0x0000 = `ADD r0, r0, r0`. Single-step; watch T run 0-1-2 and
   /TRST fire on the panel.
2. Add register file + ALU + F-stage. Burn `MOVI r1, 0x55` · `HLT`; step
   it, confirm the halt lands and r15 marches (scope QB during fetches).
3. `LDW r2` + literal, `ADD r3, r1, r2`, `CMP` / `BEQ` — exercises
   immediates, three-address ops, and the flag path.
4. Add the OUT port: `MOVI r1, 0` · loop: `OUT r1` · `INC r1` ·
   `LDW r15` + loop — switch to RUN and watch a 16-bit counter on LEDs.
5. Graduation: `MOVI r13, 200` · `LDW r1` + 300 · 16 × `MSTEP r2, r1` ·
   `OUT r2` — 60,000 computed the hard way, one panel step at a time.
