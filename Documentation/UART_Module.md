# UART Module — Design Summary

**DESIGN IN PROGRESS.** Decisions settled to date are recorded below; unresolved
choices are collected under "Open decisions" at the end. Targets the Blinky / Thumby
family as a shared peripheral — the serializer, FIFO, baud, and control logic are
identical on both machines; only the bus front-end differs (see "Bus interface").

## Design intent

A fixed-configuration asynchronous serial port (UART) for the TTL CPU family, built
from jellybean 74HC parts plus one or two GALs, in the project's curated-op / BlinkyJED
discipline. Deliberately 6850-*shaped* — status port, data port, poll-driven — but
without the 6850 itself and without its runtime configurability.

**The driving constraint is clock decoupling.** These machines run slow for
front-panel honesty (1 Hz to a few kHz). Serial bit timing therefore *cannot* be
derived from the CPU clock; it runs off its own 1.8432 MHz oscillator and proceeds
autonomously while the CPU sits at any speed, including single-step. The only
cross-domain touches are two narrow handshakes (TX byte-loaded, RX read-request),
both two-FF synchronized. Everything else in the module is self-contained.

## Fixed line parameters

Hardcoded — no control register, no runtime configuration. This is what collapses the
6850's complexity and shrinks the control GAL (no config decode, no parameter muxing,
no parity generator/checker, no variable bit-length counting).

| Parameter | Value |
|---|---|
| Format | 8N1 (8 data, no parity, 1 stop) |
| Baud | 115200 |
| Parity | none |
| Flow control | none (no CTS/DCD/RTS) — logic-level / Mega-rig link |

## Bus interface

The peripheral core is bus-agnostic; it presents a status register, a read-data port,
and a write-data port. Attachment differs by machine:

- **Thumby** — memory-mapped (the ISA has no IN/OUT). A 2-word block reached by
  `LDR`/`STR`. An address decoder (`'138`) asserts the module chip-select during the
  class-10 MEM cycle; the low address bit selects status vs data. The 16-bit bus
  zero-extends the 8-bit UART data into the low byte; the upper byte reads 0.
- **Blinky** — Z80-style separate I/O space via `IN`/`OUT`, qualified by `IORQ`, port
  number decoded by `'138`. 8-bit bus is a natural fit. No zero-extension needed.

## Register / port map

Two locations. Read and write at the data location are distinct cycles, so one address
serves both directions (6850 idiom).

| Offset | Read | Write |
|---|---|---|
| `+0` STATUS | flag byte (see below) | — (reserved; control register only if ever needed) |
| `+1` DATA | pop one byte from RX FIFO | hand one byte to TX |

### Status byte

FIFO-derived where possible. Packed into the low bits; on Thumby the upper byte reads 0.

| Bit | Name | Meaning | Clear |
|---|---|---|---|
| RDRF | RX data available | RX FIFO not empty | implicit — deasserts when FIFO drains empty |
| TDRE | TX ready | TX serializer not busy (and holding empty) | deasserts on data write, reasserts at stop-bit completion |
| OVRN | overrun | a byte arrived while the FIFO was full and was dropped | sticky until read |
| FE | framing error | a stop bit was not high | sticky (provisional — see Open decisions) |

No PE (no parity). No modem-status bits.

## Baud generation

**115200 × 16 = 1,843,200 Hz = 1.8432 MHz, exact.** A 1.8432 MHz can oscillator (`X`
designator) *is* the 16× oversample clock — no division is needed to *generate* it.
This is the reason 1.8432 MHz is the canonical UART crystal: standard rates fall out
with zero remainder.

"Divider ICs" therefore refers only to the ÷16 oversample→bit counting that the RX and
TX FSMs run against. Those counters are pushed **outboard of the GAL** to preserve
macrocells (a 22V10 has only 10 macrocells; a 4-bit counter would consume 4).

- **1× 74HC393** (dual 4-bit binary counter). One half = RX phase counter, other half
  = TX bit-timing counter; both mod-16 by natural 4-bit rollover.
- The GAL decodes the count to strobes: sample RX at count 8 (mid-bit), bit-complete
  at rollover (count 15→0).
- RX phase alignment uses the '393 async master-reset, asserted by an
  oversample-synchronous start signal so count 8 lands mid start-bit.

**Alternative:** two `74HC161`s for *synchronous* RX phase alignment (sync clear/load
instead of async) — marginally cleaner jitter, costs a chip and a few wires. Not
expected to be needed; start with the '393.

**Oscillator contingency:** if a 1.8432 MHz can isn't sourced, use a 2ⁿ multiple and
divide back first — 3.6864 ÷2, 7.3728 ÷4, 14.7456 ÷8 (one '74 flop or '393 stages),
then proceed as above.

## TX path

**No TX buffer.** The CPU is the slow producer; back-pressure is the correct model —
poll TDRE, write when ready.

- **1× 74HC377** TX holding register; CPU loads it on a data write.
- **Serializer** (`'165` or `'299` — TBD) loads from the holding register when it goes
  idle, then shifts start + 8 data + stop at the baud tick.
- TDRE = serializer idle AND holding empty (effectively "not busy"): deasserts on
  write, reasserts when the stop bit completes.
- **Consequence:** no double-buffering means at least a poll-loop's gap between bytes.
  Fine for a console/log link and irrelevant when the CPU is the bottleneck by orders
  of magnitude. A one-byte holding stage could later close the gap if back-to-back
  framing is ever wanted — deferred.
- **Cross-domain:** the data write raises "byte loaded" in the CPU clock domain; the
  TX FSM consumes it in the baud domain. Two-FF synchronize that single pulse (or host
  the catching flop inside the GAL).

## RX path

A 256-byte hardware circular FIFO, so the drain only has to keep up *on average* —
bursts and slow/jittery service are absorbed up to 256 bytes. Without it, RX-ready
would mean "service within one byte-time or drop," which a 1 Hz or single-stepped CPU
cannot meet. Overflow is not a corner case here — it is the headline case: at 115200 a
halted CPU fills the FIFO in ~22 ms, so any line typed into a paused machine overflows
for certain. The FIFO must therefore protect itself.

Structurally a near-copy of the stack-memory block (256-deep single-port SRAM + two
pointers), but FIFO not LIFO, and both pointers increment-only — so plain counters,
not up/down '191s.

- **1× 256×8 SRAM**, single-port.
- **2× 8-bit increment-only pointers** — read pointer (RP) and write pointer (WP),
  each a `74HC393` (dual 4-bit, async clear for reset). 8-bit address over 256 bytes
  makes the wrap *free* — it is just counter rollover, no compare-and-reset, no modulo.
- **2× 74HC157** — 2:1 mux time-sharing the SRAM address bus between RP and WP. (The
  compare below reads the pointer outputs directly, not through this mux.)
- **Empty/full disambiguation** — see below.

### Overflow behavior (ratified): drop-newest

When the FIFO is full, the RX FSM **does not advance WP and discards the incoming
byte**, then sets OVRN sticky. Drop-*newest* (not overwrite-oldest) is chosen
deliberately: overwrite-oldest would have to advance RP from the baud domain as well as
the CPU domain — two writers on RP across two clock domains, the arbitration hazard the
whole design avoids. Drop-newest keeps RP single-writer (CPU side only) and costs
nothing but a reliable FULL signal, which the empty/full scheme below already produces.

Ignoring overflow entirely is rejected: on a wrap FIFO, letting WP lap RP makes the two
pointers equal again, so the *empty* detector reads empty while the buffer is actually
stuffed with overwritten data — silent whole-buffer corruption and a stalled reader,
not graceful degradation. (The 6502 software-ring precedent only survives unhandled
overflow because a 6850 underneath it is already overflow-safe; here the hardware FIFO
*is* the buffer and has no layer below to lean on.)

### Empty / full — the 9th wrap bit (ratified)

With 8-bit pointers, "totally empty" and "totally full" are indistinguishable: empty is
RP == WP, and a full buffer wraps WP back onto RP, giving RP == WP again. One extra bit
per pointer resolves it.

Make each pointer **9 bits**. The low 8 bits are the SRAM address (drive memory,
unchanged). The 9th (top) bit is a **wrap-parity** flag that toggles every time the
pointer rolls through 256 — it is the carry-out of the 8-bit pointer clocking one more
flip-flop. It is never an address bit; the SRAM never sees it.

- **EMPTY** = all 9 bits equal (same address *and* same lap parity).
- **FULL** = low 8 bits equal **and** the 9th bits *differ* (writer has lapped the
  reader exactly once → 256 ahead).

4-slot illustration (2-bit address + 1 wrap bit, written `wrap:addr`):

```
start:        RP=0:00  WP=0:00   all equal              -> EMPTY
write 1 byte: RP=0:00  WP=0:01   differ                 -> 1 queued
write 3 more: RP=0:00  WP=1:00   addr equal, wrap differ-> FULL
read 1 byte:  RP=0:01  WP=1:00   differ                 -> 3 queued
read 3 more:  RP=1:00  WP=1:00   all equal              -> EMPTY
```

The `0:11 → 1:00` step on the 4th write is the mechanism: the address rolls `11→00` and
that rollover toggles the wrap bit.

#### Comparing the bits

The two conditions share "low 8 equal" and split only on the wrap bits, so they are
compared *separately*:

```
ADDR_EQ   = '688 P=Q          (low 8 bits match)
WRAP_DIFF = RPwrap XOR WPwrap  (one '86 gate)

EMPTY = ADDR_EQ AND NOT WRAP_DIFF
FULL  = ADDR_EQ AND     WRAP_DIFF
```

- The low-8 equality is the `74HC688` already required for not-empty (RDRF) — feed it
  RP[7:0] and WP[7:0] directly from the pointer outputs.
- The wrap comparison is a single **XOR** (`74HC86`): equal → 0, differ → 1.
- EMPTY and FULL are then one product term each, resolved in the RX GAL from `ADDR_EQ`
  and `WRAP_DIFF` — a couple of terms, not a 9-bit equality expansion. (Doing the full
  9-bit compare inside the GAL is rejected: 9-bit equality is product-term-hungry and
  would eat the RX macrocell budget the outboard parts exist to protect.)
- RDRF = NOT EMPTY.

Optionally, the '86 output can drive the '688's active-low enable `/G` to get one of
the conditions straight from the comparator, but the explicit `ADDR_EQ` + `WRAP_DIFF`
form is clearer to bring up and is what the GAL wants anyway.

**Bring-up caution:** `ADDR_EQ`, RPwrap, and WPwrap all live in the baud domain (the
whole FIFO is clocked there), and RPwrap changes only when the synchronized CPU read
advances RP. Sample/register EMPTY and FULL on the baud clock rather than reading them
combinationally across the CPU handshake, or a transient mismatch can be glimpsed the
instant RP increments.

### Clock-domain arbitration (the real work)

WP advances in the **baud domain** (RX FSM deposits a byte); RP advances in the **CPU
domain** (a data-read consumes one). A single-port SRAM and one comparator can't serve
both domains arbitrarily.

Resolution: clock the **entire FIFO** — both pointers, SRAM access, compare — in the
baud / oversample domain. Synchronize the CPU's read *request* into that domain with a
two-FF synchronizer. The baud-domain arbiter then drives RP onto the address bus in a
gap between RX writes, reads the byte, latches it to a CPU-side data register, and
bumps RP. Because the baud clock is vastly faster than the byte rate, idle address-bus
time always exists between writes, and the read completes in sub-µs baud-domain time —
long settled before the CPU's next (possibly 1-second-later) cycle.

Why not the alternatives: clocking the FIFO in the CPU domain leaves a byte that
arrives mid-tick with nowhere to land (the drop problem returns). A true dual-port SRAM
(IDT7130-class) removes arbitration entirely but is an exotic non-HC part that breaks
the jellybean aesthetic. Single-port + baud-domain arbiter stays on-brand.

## Control logic (GAL)

The fixed-function chips above do no decision-making; the GAL(s) own all sequencing,
clocked in the baud / oversample domain.

**RX FSM**
- Start-bit detection + qualification: idle-high falling edge, wait half a bit,
  re-sample — reject a glitch that isn't still low.
- Sample timing: bit-center strobes from the '393 count (half-bit offset to first
  sample, full-bit thereafter); clock the RX deserializer at each.
- Bit count, stop-bit framing check (high?), byte latch, RDRF/OVRN/FE generation.
- FIFO write with drop-newest on FULL; arbitration of CPU read-requests against RX
  writes.

**TX FSM**
- Load-vs-shift: load the serializer on (byte-written AND idle), then shift one bit per
  baud tick, inserting start and stop, counting to done, raising TDRE.

**Sizing — the open chip-count question.** RX (start-qualify + sample window + bit
count + framing + FIFO write + drop-newest + read arbitration) is product-term-hungry →
**22V10 class**, matching existing decode-GAL practice. TX is light and may ride along
in the same part or a spare `16V8`. Whether it is **one GAL or two** is decided by an
actual RX term count, not yet performed. Pushing the mod-16 counters to the '393 and
the wide compare to the '688/'86 is what gives RX a fighting chance of fitting one
22V10.

## Clock domains & synchronization (summary)

| Signal | From → To | Sync |
|---|---|---|
| TX "byte loaded" | CPU clk → baud | two-FF (or in-GAL flop) |
| RX read-request | CPU clk → baud | two-FF |
| EMPTY / FULL flags | within baud domain | register on baud clk |
| Everything else | within baud domain | none needed |

## CPU drain headroom

At 4 MHz the CPU is nowhere near the bottleneck; the FIFO drain rate does not set baud.
Bare poll-and-drain loop (read status, test ready, branch if empty, read data, loop):

| Machine | cycles/byte | µs/byte @4 MHz | bytes/s | 8N1 baud ceiling |
|---|---|---|---|---|
| Blinky (single-cycle) | 6 | 1.5 | ~667 k | ~6.7 Mbit/s |
| Thumby (2-cyc op, 3-cyc LDR) | 12 | 3.0 | ~333 k | ~3.3 Mbit/s |

Blinky is ~2× faster per byte: single-cycle execution, and a status poll is a 1-cycle
`IN` vs Thumby's 3-cycle status `LDR`. Even Thumby's ~3.3 Mbaud ceiling is ~29× over
115200; a realistic per-byte handler (2–3× the bare loop) still leaves both far above
115200. The FIFO absorbs the rest.

Cycle counts are loop-shape-dependent illustrations, not ISA guarantees.

### Illustrative poll loops

Thumby (status read, shift RDRF into carry, branch, read data):
```
loop:   LDR   r0, [rUART, #0]      ; status
        MOV.LSR r0, r0, #RDRF_POS  ; ready bit -> carry
        BCC   loop                 ; empty, spin
        LDR   r1, [rUART, #1]      ; pop byte
        ...                        ; handle r1
        B     loop
```

Blinky (port poll, mask, branch, read):
```
loop:   IN    #STATUS
        PUSH  #RDRF_MASK
        AND
        BEQ   loop                 ; empty, spin
        IN    #DATA                ; pop byte
        ...                        ; handle
        BRANCH loop
```

## Provisional chip list

| Block | Part(s) | Qty |
|---|---|---|
| Oscillator (= 16× clock) | 1.8432 MHz can oscillator (`X`) | 1 |
| Baud/timing counters | 74HC393 (dual 4-bit, mod-16 ×2) | 1 |
| TX holding | 74HC377 | 1 |
| TX serializer | 74HC165 or 74HC299 (TBD) | 1 |
| RX deserializer | 74HC299 / 74HC595 (TBD) | 1 |
| RX FIFO memory | 256×8 single-port SRAM | 1 |
| RX FIFO pointers | 74HC393 (RP, WP — 8-bit each) | 2 |
| RX FIFO wrap bits | 74HC74 (the two 9th bits) | 1 |
| RX FIFO address mux | 74HC157 | 2 |
| RX FIFO low-8 compare | 74HC688 | 1 |
| RX FIFO wrap compare | 74HC86 (XOR) | 1 |
| Control FSM | GAL (22V10 RX; possibly + 16V8 TX) | 1–2 |
| Bus decode | 74HC138 | 1 (shared if possible) |
| Cross-domain sync | 74HC74 flops (or in-GAL) | 0–2 |

Rough total: ~14–16 packages plus the oscillator, before consolidation.

## Open decisions

1. **One GAL or two** — pending an RX product-term count. The deciding step.
2. **Framing-error granularity** — single sticky FE flag (recommended) vs per-byte FE
   by widening the FIFO SRAM to 9 bits.
3. **RX phase alignment** — async '393 master-reset (recommended) vs synchronous via
   two '161s.
4. **TX serializer part** — '165 vs '299.
5. **Optional control register** — keep STATUS-write reserved, or expose a control word
   if any runtime knob is ever wanted (currently: none).

## Ratified

- Fixed 8N1 / 115200; no control register.
- 6850-shaped two-port map (STATUS +0, bidirectional DATA +1).
- 1.8432 MHz oscillator *is* the 16× clock; '393 for mod-16 counting.
- No TX buffer; poll-TDRE back-pressure.
- 256-byte single-port circular RX FIFO, baud-domain clocked, CPU read-request
  two-FF synchronized.
- **9th-wrap-bit** empty/full scheme; compare via '688 (low 8) + '86 (wrap XOR),
  flags resolved in the RX GAL and registered on the baud clock.
- **Drop-newest** overflow behavior; OVRN sticky.

## Verification still owed

- Confirm `ChipInventory.md` stock: 1.8432 MHz (or 3.6864 MHz) can oscillator, spare
  '393s, a 256×8 SRAM, the '157/'688/'86/'74 counts.
- Confirm 74HC393 reset polarity and pinout against the TTLSim simulator model before
  committing the RX async-clear alignment.
