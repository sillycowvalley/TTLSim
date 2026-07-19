# Addy CPU — v1 vs v2 Comparison & Micro-Module Strategy

## Summary

v2 is a superset of v1. The instruction word format, register file interface,
clock module interface, ROM fetch path, IR, operand registers, zero detectors,
output register, address steering, and all control principles carry forward
unchanged. What changes is confined to the ALU, the flag register (extended
from 2 to 4 bits), the GAL equations (additions, not replacements), and the
addition of data memory infrastructure.

---

## Module-by-Module Status

| Micro-Module | v1 Parts | v2 Parts | Status |
|---|---|---|---|
| **Clock / Sequencer** | Blinky Clock v3 (external board) | same | ✅ Unchanged — shared board |
| **Register File** | Register File Module, Thumby 8×16 | same | ✅ Unchanged — shared board |
| **Program ROM** | 2× AT28C256 | same | ✅ Unchanged |
| **Instruction Register (IR)** | 2× 74HC377 | same | ✅ Unchanged |
| **A Operand Register** | 2× 74HC574 | same | ✅ Unchanged |
| **B Operand Register** | 2× 74HC574 | same | ✅ Unchanged |
| **Immediate Buffers** | 3× 74HC541 (imm8, imm-high, input) | same | ✅ Unchanged |
| **Zero Detect** | 2× 74HC688 | same | ✅ Unchanged |
| **Address Steering** | 1× 74HC157 + 2× 74HC32 | same | ✅ Unchanged |
| **Output Register + LEDs** | 2× 74HC377 | same | ✅ Unchanged |
| **ALU** | 4× 74HC283 + 4× 74HC86 | 4× 74LS181 + 1× 74LS182 + 4× 74HCT541 (fence) | ❌ Replaced |
| **Flag Register** | 1× 74HC74 + 1× 74HC157 (2 bits used) | same ICs, 4 bits used (Z/C/N/V) | ⚠️ Extended (same ICs, new equations) |
| **GAL1 (sequencer / ALU ctrl)** | GAL22V10 | GAL22V10 | ⚠️ Extended equations |
| **GAL2 (bus-source enables)** | GAL22V10 | GAL22V10 | ⚠️ Extended equations |
| **Data SRAM** | — | 2× 62256 (32K×8) | 🆕 New |
| **Memory Address Register (MAR)** | — | 2× 74HC374 | 🆕 New |
| **Data Bus Buffer** | — | 1× 74HCT245 | 🆕 New |
| **Memory Decode (A15)** | — | 1× 74HC138 or spare GAL term | 🆕 New |

---

## The Three Change Areas

### 1. ALU — Complete Replacement

| Aspect | v1 | v2 |
|---|---|---|
| Parts | 4× '283 + 4× '86 (8 ICs) | 4× '181 + 1× '182 + 4× HCT541 fence (9 ICs) |
| Operations | Add, Subtract only | Add, Subtract, AND, OR, XOR, NOT, Pass |
| Carry propagation | Ripple (~100 ns) | Lookahead via '182 (~35 ns) |
| Level compatibility | Pure HC | LS output → HCT fence → HC rest of machine |
| Clock ceiling | ~3 MHz | ~5–8 MHz |

The '181 is only available in 74LS, not 74HC. The HCT541 fence is mandatory
to translate LS VOH (≥ 2.5 V) to CMOS levels (≥ 4.4 V) before the HC
register file. HC outputs feeding LS inputs in the other direction needs no
buffering — HC VOH comfortably satisfies LS VIH.

The v1 rationale (avoid '181 because it's LS) is documented in v1 and stands
for v1. v2 explicitly accepts the LS mix because logic ops are now required.

### 2. Flag Register — Same ICs, Extended Use

The '74 + '157 combination already exists in v1 with two sections spare. v2
uses those spare sections for N and V.

| Flag | v1 | v2 |
|---|---|---|
| Z | ✅ latched | ✅ latched (unchanged) |
| C | ✅ latched (stored, not consumed) | ✅ latched + consumed by ADDC/SUBC/MOVC/MOVNC |
| N | — | ✅ result bit 15 |
| V | — | ✅ Cn15 XOR Cout (one '86 gate) |

No new ICs required for the flag register itself. The V derivation needs one
gate, which can come from a spare '86 or '32 section.

### 3. GAL Equations — Additive Only

All v1 equations carry forward verbatim. v2 adds:

**GAL1 additions:**
- M, S3–S0 (5 outputs) for '181 function select — these consume the 5 spare
  OLMCs identified in v1's GAL budget
- /TRST suppression extended for LD/ST 4-state timing
- CIN_SEL: route C flag into carry-in for ADDC/SUBC
- FLAGEN timing: unchanged

**GAL2 additions:**
- MAR_CLK, SRAM_OE, SRAM_WE, DBUS_OE — all new outputs for memory control
- ROM_CE = /A15, RAM_CE = A15 — memory decode (can share with '138 or spare term)

Neither GAL needs to grow beyond a GAL22V10; the v1 design deliberately left
headroom, and the v2 additions were designed to fit.

---

## ISA Compatibility

v2 is fully backward-compatible with v1. Every v1 opcode has the same
encoding and behaviour. A v1 ROM image runs on a v2 board without
modification, with one caveat: programs that assumed 0x0000–0xFFFF was all
ROM must have data sections moved to RAM (0x8000–0xFFFF). Programs with no
data memory are unaffected.

The new N and V flags are set silently by v1 arithmetic but never consumed
by v1 code — harmless.

New opcodes (AND, OR, XOR, ADDC, SUBC, LD, ST, MOVC, MOVNC, MOVN) occupy
spare slots that were undefined in v1.

---

## Micro-Module Build Strategy

Modules that can be built once and used on both v1 and v2 boards without any
change:

| Module | Reuse Value |
|---|---|
| Program ROM (AT28C256 pair + socket board) | High — same pinout, same timing, same fetch circuit |
| Instruction Register (2× HC377) | High — identical in every respect |
| Operand Registers A and B (4× HC574) | High — identical |
| Immediate Buffers (3× HC541) | High — identical |
| Zero Detect (2× HC688) | High — identical inputs and outputs |
| Address Steering (HC157 + HC32) | High — identical |
| Output Register + LEDs (2× HC377) | High — identical |
| Clock Module (Blinky v3) | Already a separate board — shared directly |
| Register File (Thumby config) | Already a separate board — shared directly |

Modules that differ between v1 and v2 and should be treated as separate
plug-in sub-assemblies:

| Module | v1 Version | v2 Version |
|---|---|---|
| ALU | '283 + '86 | '181 + '182 + HCT541 fence |
| Memory subsystem | ROM only (no MAR, no SRAM, no data bus) | ROM + MAR + SRAM + HCT245 + A15 decode |
| GAL pair | v1 equations | v2 equations (same devices, reflashed) |
| Flag register | 2-bit (Z/C) | 4-bit (Z/C/N/V) — same ICs, updated tie-offs |

---

## Net IC Count

| Board | IC Count |
|---|---|
| v1 CPU board | ~30 ICs |
| v2 CPU board | ~37 ICs |
| Delta | +7 ICs (remove 8 ALU ICs, add ~15 new) |

The shared boards (Clock Module, Register File) add to both totals equally
and are not counted above.
