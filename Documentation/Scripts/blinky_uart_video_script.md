# Blinky UART Module — Video Script

Terse, incremental. Each section adds one piece. Spoken lines in plain text,
on-screen cues in brackets. The through-line: the core is the same on every
machine — only the bus front-end changes. Chips new to the series get taught
where they first do their job; chips from earlier videos get a one-line
callback.

---

## Cold open

This is the serial port for Blinky — and for any TTL CPU I build after it. It lets a terminal talk to the machine at a hundred and fifteen thousand baud while the machine itself crawls along at one hertz. The trick is that it doesn't care how slow the CPU is. And the hard ninety percent of it bolts onto any of these machines unchanged — only the last inch, where it meets the bus, gets reshaped. Piece at a time.

[Finished card, terminal echoing characters, then fade to a bare oscillator]

---

## 1. The whole problem — clock decoupling

Start with why this is hard. These machines run *slow*, on purpose — one hertz to a few kilohertz, slow enough to watch on a panel. You cannot make a hundred-and-fifteen-thousand-baud bit stream out of a one-hertz clock. So the UART doesn't try. It runs entirely on its own clock, autonomously, and only shakes hands with the CPU twice — once to hand off a byte to send, once to ask for a byte received. Everything else is self-contained.

[Two clock traces: CPU crawling, UART racing, side by side]

---

## 2. The heartbeat — and why it's free

One can oscillator: one-point-eight-four-three-two megahertz. That number isn't arbitrary — a hundred and fifteen thousand two hundred, times sixteen, is *exactly* that. Which is why it's the canonical UART crystal: every standard baud rate falls out with no remainder. So I don't divide it down to make baud — the oscillator already *is* the sixteen-times oversample clock.

[Scope the oscillator]

Turning that into bit timing is a 74HC393's job — and this is a chip worth knowing, because it shows up twice in this module. It's two independent four-bit binary counters in one package: clock it, and the four outputs step zero through fifteen and roll straight back to zero. That rollover is the whole trick — count sixteen oversample ticks and the carry *is* one bit-time: no compare, no reload, just natural four-bit overflow. One half of the chip times the receiver's sampling, the other half times the transmitter's bits. It also has an asynchronous clear, which we'll lean on to line the receiver up. Pushing this counting out onto a jellybean counter is deliberate — it keeps it out of the programmable logic, where every bit would cost a scarce macrocell.

[Walk the '393 once a diagram exists: the 4-bit count, the rollover carry, the async clear]

---

## 3. Two ports, no knobs

The format is welded shut: eight bits, no parity, one stop, fixed baud. No configuration register, nothing to set up. Most of a real 6850's silicon is *runtime* flexibility — baud divisors, parity modes, word lengths — and throwing all of that away collapses the control logic to almost nothing. What's left is shaped like a 6850 and used like one: a status port and a data port, polled. Read status to see whether a byte arrived or the transmitter's free; read data to pop a received byte, write data to send one. Read and write hit the same address on different cycles, so one location serves both directions.

[Show the two-register map, poll-loop pseudocode]

---

## 4. Sending — the easy half

Transmit is the easy half, and it's where the chip that *is* a UART turns up: the shift register. Teach it once and it serves both directions. To send, you load a byte in parallel and clock it out one bit at a time — parallel-in, serial-out. To receive, you run it the other way — serial-in, parallel-out. Load, then shift on each baud tick: that's the heart of a serial port.

On the transmit side a '377 catches the byte the CPU writes — that's the '273 from the clock video, but the version *with* a load-enable, so it only grabs the bus on an actual write — and the serializer shifts it out as start bit, eight data, stop bit. (Whether that serializer is a '165 or a '299 is still an open choice; the behaviour is identical either way.) There's deliberately *no* send buffer: the CPU is the slow one here, so back-pressure is the honest model — poll the ready flag, write when it's clear. A buffer would only paper over a gap that doesn't matter when the processor is the bottleneck by four orders of magnitude.

[Bytes shifting out on the TX line]
[Walk the shift register once a diagram exists: parallel load, then serial shift, both directions]

---

## 5. Receiving — why it needs a deep buffer

Receive is where the work is. The bits arrive on that same shift register run the other way — serial-in, parallel-out, assembling one byte at a time — but then each byte has to go *somewhere*, and that's the hard part. At this baud a character lands roughly every ninety microseconds. A CPU at one hertz — or frozen on a breakpoint — has no hope of reading that fast: a halted machine fills with a full screen of text in about twenty milliseconds. So "ready" can't mean "service me within one byte or I drop it." The receiver needs a deep buffer that protects itself — a two-hundred-and-fifty-six-byte hardware circular FIFO. Type into a stopped machine and every character up to two hundred and fifty-six is caught and waiting.

[Type a burst at a paused CPU, FIFO fill indicator climbing]

---

## 6. The FIFO — the stack block, turned into a queue

It's the stack memory all over again — a two-fifty-six-byte SRAM and two pointers — but a queue instead of a stack, and both pointers only ever count *up*. And there's the '393 again: the write pointer for incoming bytes and the read pointer for the CPU's reads are just two more four-bit counters — the same chip we used for baud timing, doing a completely unrelated job. Eight bits of address over exactly two hundred and fifty-six bytes makes the ring wrap *free* — the same rollover trick as the bit counter, no compare, no modulo.

One detail that looks small and isn't: the SRAM is *single-port* — one address, one access at a time. So both pointers can't reach it at once; a pair of '157 muxes time-shares that single address bus between them. Hold onto that single-port constraint — it's the entire reason step nine exists.

[Diagram: SRAM, WP and RP chasing around the ring]

---

## 7. The classic trap — empty versus full

Here's the bug that bites everyone. Empty is read-pointer-equals-write-pointer. But fill the ring completely and the write pointer laps all the way back onto the read pointer — equal *again*. Empty and full look identical. The fix is one extra bit on each pointer — a '74 flip-flop holding a wrap flag that toggles every time the pointer laps the buffer. Then the '688 from the breakpoint video does the low-eight compare — the same identity comparator, just pointed at two pointers now instead of an address and a row of switches — and a single '86 XOR gate compares the two wrap bits. Same address and same wrap means empty; same address but *opposite* wrap means full. The memory never sees that ninth bit — it's bookkeeping, not an address.

[The 4-slot walk-through: addr rolls, wrap toggles, FULL lights]

---

## 8. When it's full — drop the newest

Full, and another byte arrives: the receiver drops *that* byte and sets a sticky overrun flag. It does not overwrite the oldest. Overwriting would mean advancing the read pointer from the fast clock as well as the CPU's clock — two writers on one pointer across two clock domains, which is exactly the hazard this whole design exists to avoid. Drop-newest keeps the read pointer owned by the CPU alone, and costs nothing but a reliable full signal, which we already have.

[Full buffer, incoming byte rejected, overrun flag latches]

---

## 9. Two clocks, one memory — the real engineering

The write pointer moves in the fast UART clock; the read pointer moves in the slow CPU clock. And the memory is single-port — it can't answer to both whenever they please. So the entire FIFO is clocked in the *fast* domain — pointers, memory, comparison, all of it. The CPU's read *request* is carried across with a two-flip-flop synchronizer, and the fast-side logic slips the read into a gap between incoming bytes. There's always a gap: the UART clock is thousands of times faster than bytes arrive. The byte is latched into a CPU-side register and settled long before the processor's next cycle — which might be a whole second away.

[Animate: read-request crossing the domain boundary, settling between RX writes]

---

## 10. The brain — one or two GALs

This is the first board in the series where a programmable chip does the heavy lifting, so it's worth a word. Everything so far has been fixed-function jellybeans — counters, registers, a comparator — each doing one unchangeable thing. The sequencing that ties them together is where a GAL earns its keep: rather than wire a dozen gates and flip-flops into a state machine, you write the logic equations and burn them into a single part.

All the decision-making lives in that GAL, clocked on the fast oscillator. The receiver state machine finds the falling edge of a start bit, waits half a bit and re-checks to reject a glitch, samples each bit dead-centre, checks the stop bit is high, writes the FIFO, and arbitrates the CPU's reads against incoming writes. The transmitter side just loads and shifts. The dumb work — the counting, the wide compare — was pushed out onto the '393 and the '688 precisely so this sequencing fits.

[GAL with the FSM states sketched around it]

---

## 11. The part that makes it universal

Everything up to here is identical on every machine I'll ever build. The *only* thing that changes from one CPU to the next is how the card hangs on the bus — and that's one decoder. A '138 is a three-to-eight decoder: feed it a few address lines and it pulls exactly one of eight outputs low, picking one device out of a row. Here it turns the CPU's address into this module's chip-select and the choice between the two ports. On Blinky that's a Z80-style I/O port — IN and OUT, qualified by an I/O-request line. On a memory-mapped machine like Thumby it's two addresses reached by load and store, the sixteen-bit bus just zero-extending the byte. Swap that front-end, keep the entire core. Design the hard part once; re-skin the bus per machine. That's the whole reuse story.

[Same card, two front-ends side by side: Blinky I/O vs Thumby memory-mapped]

---

## 12. The test — no CPU required

To bring it up I don't even need a processor. Tie transmit straight to receive — loopback — or hang it off a USB-serial cable to a laptop. Send characters in; watch them pile into the FIFO and the ready flag come up. Then drain them by hand, one read at a time, and watch the count fall without ever losing a character. The oscillator, FIFO, and serializers all prove out before a CPU is ever attached.

[Loopback jumper in, terminal typing, FIFO count rising then draining]

---

## 13. On a board

Off the breadboard now — oscillator, the FIFO memory and its pointers, the shift registers, the GAL, the wrap-bit comparison, all laid out on a PCB. The bus front-end sits on its own edge of the board, so the same card serves whatever machine you plug it into.

[Bare breadboard, then the finished card running the loopback test]

---

## Outro

So: one fast clock that owes nothing to the CPU, a two-hundred-and-fifty-six-byte FIFO that protects itself, a poll-driven two-port interface, and a bus front-end you swap per machine. It lets a terminal talk to any of these processors at full serial speed while they run slow enough to watch — and the bulk of it never changes from one CPU to the next.

Next, it meets a real machine: the front-end decoder wired to Blinky's I/O space, and a console talking to the stack machine while it single-steps.

[End card]
