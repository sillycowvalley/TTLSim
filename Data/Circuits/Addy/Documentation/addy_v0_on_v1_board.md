# Addy v0 on the Real Board — Population, Not a Redesign

Short answer to "will the Fetch & Control PCB host it": **yes, and better than
the single-GAL sketch — because on the real board you should not burn a v0 GAL
at all. Burn the v1 JEDECs.**

The reasoning is in *Why the v1 GALs already are the v0 GALs* below. Read that
first; the population table follows.

---

## Where the single-GAL v0 sketch does and does not match

| Aspect | Match? |
|---|---|
| Equations for WE, WSEL7, CIN, TRST, HALTREQ, AOE, IRCE | Term-for-term subsets of the documented v1 GAL1/GAL2 |
| Equation for `WE` | **Diverged** — mine omitted `·/IR13`, so it would write on the class-01 compare row that v1 reserves as no-write |
| Equation for `IMMOE` | **Diverged** — mine was `T2·c01`, broader than v1's `T2·[c01·/IR12 + LDI]`; it would enable imm8 on ADDIH, where v1 uses IMHOE |
| Pin assignment | **Invented.** Derived from the prose equations in the v1 design note, not from the U38/U39 sources or the schematic. Assume it is wrong. |
| Partitioning | **Wrong shape for the board.** The sketch collapses everything into one device; the board has two sockets, U38 ENABLES and U39 SEQUENCER, and the copper decides which output pin reaches which load. There is no trace from U39 to the A-register /OE. |

So the sketch is a valid *breadboard* machine and a valid statement of the
minimum logic. It is not something to drop into either socket on the PCB.

---

## Why the v1 GALs already are the v0 GALs

Every v1 control output that v0 does not use is gated on an opcode v0 never
executes:

| Output | v1 equation | Behaviour when the program only contains ADDI / LDI / HLT |
|---|---|---|
| SUBTRACT | `T2·[IR11·(c00 + c01·/IR12) + c10·IR13·IR12]·/RST` | never asserts — adder stays in add |
| FLAGEN | `T2·(c00 + c01)·/RST` | asserts, but drives only the flag mux select; harmless with flags unpopulated |
| ASEL_RS | `c00 + c10·/IR13·/IR12 + OUT` | never asserts — '157 holds AADDR = rd, exactly what v0 wants |
| BOE | `T2·c00·/IR12` | never asserts |
| IMHOE | `T2·ADDIH` | never asserts |
| INOE | `T2·IN` | never asserts |
| LEDCE | `T2·OUT·/RST` | never asserts |

Nothing needs deleting. **v0 is a population choice and a program, not a
different logic design.** That is also why the ISA compatibility discipline was
worth keeping: the v0 program is a v1 program that happens to use three
opcodes.

The practical consequence: the PCB can be fully assembled and the machine will
still come up on a v0 program. Under-populating is an option for staged
bring-up, not a requirement.

---

## Population for a staged v0 bring-up

**Must be populated** — everything in the fetch/increment loop, plus anything
in series:

| Block | Parts |
|---|---|
| Control | U38, U39 (v1 JEDECs) |
| Address steering | U40 WOR, U41 AOR, U42 ASEL |
| Instructions | U55/U56 ROM, U57/U58 IR |
| Adder | U51–U54 '283 |
| **XOR stage** | **U47–U50 '86 — these sit *in series* between the B bus and the adder's B inputs. Leaving them out floats the adder.** |
| A register | U61 AH, U64 AL |
| imm8 | U63 IML |
| Pulldowns | RN2, RN3 (A bus), RN4, RN5 (B bus) |

**May be left out for v0:**

U43/U44 '688 zero detect · U45 '74 flags · U46 '157 FMUX · U62 IMH '541 ·
U65/U66 B '574 · U59 IN '541 and its switch bank · U60 OUT '377 and D13–D20.

---

## Tie-offs required when those sockets are empty

This is the part that bites. GAL *inputs* sourced from unpopulated chips float,
and a floating GAL input is not a don't-care — it is an oscillating one.

| GAL input | Normally driven by | Tie to |
|---|---|---|
| Z (into U39) | U45 flags | GND |
| EQ_H, EQ_L (into U38) | U43/U44 '688 | GND or VCC — pick one and be consistent; Z_NEW is unused either way |

Also check that the B-register /OE and the OUT register /CE loads are absent
rather than floating: with U65/U66 and U60 out of their sockets those GAL
outputs simply drive nothing, which is fine.

If the PCB does not already carry a tie-off provision for Z / EQ_H / EQ_L, that
is worth adding before the boards are made — a pad-pair and a zero-ohm link per
signal, or a pulldown position. It costs nothing and removes the one class of
fault that would make a staged bring-up lie to you.

---

## The v0 program

Nothing here needs the assembler changed — these are v1 encodings.

```
0000  5000   LDI  r0, 0
0001  4001   ADDI r0, 1
0002  5701   LDI  r7, 1     ; jump back to 0001
```

Blank EEPROM reads 0xFFFF = HLT, so a half-burnt ROM stops rather than runs.

Forward relative jump and halt, once the counter loops:

```
0000  5000   LDI  r0, 0
0001  4001   ADDI r0, 1
0002  4703   ADDI r7, 3     ; PC+1+3 = 0006
0003  FFFF   HLT            ; must not be reached
0004  FFFF   HLT
0005  FFFF   HLT
0006  FFFF   HLT            ; lands here, HALT lights
```

Backward relative jumps need SUBI, which needs SUBTRACT and the '86s driven —
the '86s are already populated, so **SUBI works in v0 for free**. That was not
true of the single-GAL sketch. Add `SUBI` to the v0 instruction list on the
real board:

```
0000  500A   LDI  r0, 10
0001  4801   SUBI r0, 1
0002  47FE   ADDI r7, 254   ; wraps? no — use SUBI r7 for backward
```

Correct backward form: `SUBI r7, k+1` closes a k-instruction loop, per the v1
relative-jump rule. Flags are not needed for the unconditional case.

---

## What I need to say anything pin-exact

I built the sketch from the prose equations in the v1 design note. To produce
or check anything at pin level I need:

1. The actual U38 and U39 `.pld` sources.
2. The Fetch & Control `.ttlproj` — the copper, not the prose, is what fixes
   the pinout.
3. Confirmation of which layers are actually on that PCB. The designator map
   has Sequencer, ALU, Instructions, IO and Operands as separate layers; if
   Fetch & Control is only Sequencer + Instructions + Interfaces, then the
   adder, A register and imm8 buffer are on a second board and v0 cannot run on
   the Fetch & Control PCB alone regardless of GAL contents.
