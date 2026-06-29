# Mini Blinky — Return-Stack Module (W = 4)

The hardware that makes `CALL` and `RET` execute on the 4-bit Blinky PC. It is a
SRAM-backed return stack: a hardware stack pointer addresses a small RAM that holds
return addresses, so calls nest to the full depth of the pointer (16 deep at W = 4)
rather than a single level.

This document describes the design as built and verified in TTLSim. It supersedes the
earlier `return_stack_circuit.svg` layout, which read the stack at the raw pointer value
and used a direction-gated clock — both of which are corrected here.

---

## Chip complement

| Ref | Part | Role |
|---|---|---|
| U9 | 74'191 | Return-stack pointer (RSP) — 4-bit synchronous up/down counter |
| U13 | 74'283 | Decrementer — forms `RSP − 1` for the pop read address |
| U14 | 74'157 | Address mux — selects `RSP` (write) or `RSP − 1` (read) |
| U10 | 2114 | 1K×4 SRAM, used 16 deep — the return stack itself |
| U11 | 74'257 | RET-data-in mux (3-state) — drives `PC+1` onto the RTOS bus on a push |
| U12 | 74'00 | Strobe / inverter glue — derives `/WE`, `WRPH`, and two polarity inverts |

Control comes from `GAL3_POINTERS` (U8): `RSP_EN` (pin 17, active-low), `RSP_UD`
(pin 16, HIGH = up), `RDIN_SEL` (pin 15). The system supplies `CLK` (1 Hz, rising-edge)
and `RESET` (active-high). `PC+1` is tapped from the existing `'283` "+1" adder (U3);
the `RTOS` bus returns to the NEXTPC mux (U5/U6) on a read.

---

## Data flow

```
            RSP[0:3]                          R_ADDR[0:3]
  U9 '191  ─────────►  U13 '283 (RSP-1) ──►  U14 '157  ──►  U10 2114
  (RSP)    └────────────────────────────►   (addr mux)      (stack RAM)
                                              ▲                  │ I/O
                                          RSP_UD              RTOS[0:3]
                                                            ┌─────┴─────┐
                                                  on write  │           │ on read
                                              U11 '257 ─────┘           └──► NPC mux ──► PC
                                            (PC+1 in)
```

- The RSP value fans out two ways: directly (`= RSP`) and through the `'283`
  decrementer (`= RSP − 1`).
- The `'157` picks one of those as the SRAM address, on `RSP_UD`.
- The 2114 I/O is a single shared 4-bit bus (`RTOS`). On a write the `'257` drives it
  with `PC+1`; on a read the 2114 drives it out to the NEXTPC mux.

---

## CALL / RET semantics

`CALL n` pushes the return address `PC+1` onto the stack and jumps to `n`. `RET` pops
the stack and loads the popped address into the PC. The pointer convention is
**next-free slot**: RSP points one above the live top-of-stack.

| Cycle | `RSP_EN` | `RSP_UD` | SRAM address | SRAM op | Pointer |
|---|---|---|---|---|---|
| **CALL** (push) | asserted | up | `RSP` | write `PC+1` → `stk[RSP]` | `RSP++` |
| **RET** (pop) | asserted | down | `RSP − 1` | read `stk[RSP−1]` → PC | `RSP−−` |
| anything else | deasserted | — | — | none (bus high-Z) | hold |

`PC+1` is the pre-incremented PC, already present every cycle on the adder outputs, so
a CALL needs no extra arithmetic for the return address.

---

## Addressing: write at RSP, read at RSP − 1

This is the heart of the design and the fix over the earlier revision.

Because the pointer marks the next-free slot, the live top-of-stack always sits at
`RSP − 1`. A pop must therefore read `stk[RSP − 1]`, not `stk[RSP]`. The earlier
revision addressed the SRAM with the raw pointer on both reads and writes, so `RET`
read the empty slot above the top, fetched garbage, and vectored the PC to 0.

The fix is purely spatial, on one clock edge:

- **U13 '283** forms `RSP − 1` continuously: `A = RSP`, `B = 1111`, `Cin = 0`, so
  `A + 15 = A − 1 (mod 16)`. The carry-out is unused.
- **U14 '157** drives the SRAM address from `RSP` when `RSP_UD = up` (a write) and from
  `RSP − 1` when `RSP_UD = down` (a read). Its strobe `/G` is tied low (always enabled).

With this, `RET` reads the true top-of-stack and the PC latches it on the same edge that
also decrements the pointer — no separate decremented-address path racing the clock.

---

## Clocking

The RSP runs **straight off the system clock**. Direction is set by the `'157` mux
select, never by gating or muxing the counter's clock.

This matters for back-to-back stack operations. An earlier attempt derived the RSP clock
as `XNOR(RSP_UD, CLK)` to make a pop count half a cycle early. That gated clock produced
a runt pulse every time `RSP_UD` flipped at a clock edge (the signal is combinational off
the IR, which changes *on* the edge), double-clocking the counter. The decrementer
approach removes the need for any clock trick: a `CALL → RET` with no intervening hold
cycle counts `up` then `down`, once each, cleanly.

---

## Strobe derivation (U12 '00)

The 2114 controls are derived from the pointer signals, not separately decoded.

| Gate | Function | Drives |
|---|---|---|
| 1 | `/WE = NAND(RSP_UD, WRPH)` | 2114 `/WE`, and `'257` `/OE` |
| 4 | `WRPH = NAND(CLK, CLK) = /CLK` | gate-1's `WRPH` input |
| 3 | `/RSP_UD = NAND(RSP_UD, RSP_UD)` | `'191` `D//U` |
| 2 | `/RESET = NAND(RESET, RESET)` | `'191` `/LOAD` |

`/CS` is **not** gated in the glue — `RSP_EN` drives the 2114 `/CS` directly (both
active-low), so the RAM is selected on exactly the push/pop cycles and its bus is high-Z
on every idle cycle.

`/WE` asserts only on a push (`RSP_UD` high) **and** only while `WRPH = /CLK` is high —
i.e. the settled second half of the cycle. Pinning the write to the settled half is what
keeps it glitch-free: at the start of a cycle the IR is still resolving and `RSP_UD` can
twitch, which with a full-clock-high window would punch a runt pulse through to `/WE` and
spuriously write. Confining the window to `/CLK` keeps that twitch off the strobe.

The `'257` (3-state) drives `PC+1` onto the shared `RTOS` bus only while `/OE = /WE` is
low — i.e. during the push write. On a read it is released and the 2114 drives the bus
into the NEXTPC mux. At W = 4 there is no separate `RTOS` latch; `RTOS` is simply the
2114 read at the addressed cell.

---

## Polarity and reset glue

- **Direction polarity.** `GAL3` defines `RSP_UD` HIGH = up, but the `'191` `D//U` is
  LOW = up. Gate 3 of the `'00` inverts it. Gate 1 uses `RSP_UD` in its native sense for
  `/WE`, so both polarities are satisfied from the same signal.
- **Reset.** The `'191` has no clear pin; reset is a load-of-zero. Its parallel inputs
  `A/B/C/D` are tied to 0, and `/LOAD` is driven by `/RESET` from gate 2 (the active-high
  `RESET` inverted), on the same reset event that clears the PC. Power-on state is
  `RSP = 0`.

---

## Timing note

With `WRPH = /CLK`, the write pulse deasserts at the cycle-ending edge rather than
comfortably before it. It still commits to the correct slot because the address change
lags that edge by the counter-plus-mux propagation, while `/WE` rises after just the
single glue NAND — so `/WE` is high before the address moves. The margin is enormous at
1 Hz. Only if the clock were pushed fast enough that those delays became comparable would
a properly bounded write pulse (narrower than the half-cycle) be needed.

A second, latent point: `RSP_UD` is a documented don't-care when the stack is not
counting, so strictly the write ought to be qualified by `RSP_EN` as well. In the current
instruction set `RSP_UD` only goes high on `CALL`, so it is safe as-is; adding `>R` or
other stack-writing ops would make that qualification worth adding.

---

## Validation

Verified in TTLSim against `calls.hex` (build: 0 errors, 0 warnings):

- Nested `CALL`/`RET` returns to the correct addresses on every pass.
- RSP unwinds cleanly `0 → 1 → 2 → 1 → 0` — single counter edges, no double-counts.
- The only SRAM writes are the legitimate pushes; zero spurious writes.
- Correct with the subroutines packed back-to-back (no NOP padding between the inner
  `CALL` and the `RET`) — the worst case for counter and strobe timing — which the
  decrementer design passes where the gated-clock attempt did not.
