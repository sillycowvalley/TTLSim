# Teensy 4.1 + 5 V Adapter Board — Reference for Writing Test-Harness Sketches

Rev 2, 2026-07. Working notes for building Arduino sketches that use the
Teensy 4.1 **5 V Adapter board** (74HCT245 drive / 74LVC245 read per port,
/OE-steered) as a test harness for 5 V TTL circuits.

**Status:** the '245 architecture is validated on breadboard (Level245Loop
campaign, one port pair, results in §4). The PCB is in fab; §9 is the
bring-up plan for when it arrives. The previous TXS0108E-based adapter is
retired — its failure modes are kept in §10 for historical diagnosis only.
`AdapterPorts.h` is now **Teensy-only**; the Arduino Mega branch has been
removed from the shared header. `MegaDirect_Reference.md` describes a
separate legacy toolchain and no longer shares code with this one.

---

## 1. The Hardware Stack

| Layer | Part | Speed / limit |
|---|---|---|
| CPU | NXP i.MX RT1062, ARM Cortex-M7 @ 600 MHz | ~1.7 ns per instruction cycle |
| GPIO | RT1062 native ports (GPIO6–9, CPU bus) | Single-cycle store/load with `digitalWriteFast`/`digitalReadFast` on constant pins |
| Level shift | 74HCT245 (drive, 5 V) + 74LVC245 (read, 3.3 V) | ~15 ns pair propagation; strong push-pull both directions; **no longer the bottleneck** |
| Cabling | Ribbon to device under test | ~50–70 pF/m; keep short (§6) |
| Host link | Native USB 2.0 High Speed | ~20–25 MB/s real throughput |

Speed hierarchy: **CPU ≫ GPIO ≫ '245 pair ≫ any retro/TTL bus.** The
practical vector rate of a write-settle-read-compare harness loop is
~250–350 ns/vector — software cost, not silicon. A 1–8 MHz classic bus is
orders of magnitude inside the envelope.

The defining change from the TXS era: **both directions are strong
push-pull with a hard DIR pin.** No auto-direction arbitration, no weak
static keeper forming dividers with external resistor networks, no one-shot
edge accelerators for crosstalk to re-fire. The entire TXS failure class —
phantom transitions, keeper/divider mid-threshold nodes, glitch
re-triggering — is gone by construction and confirmed gone by measurement
(§4).

---

## 2. Level-Translation Architecture

Each port is a 74HCT245 (Vcc 5 V) for the drive direction and a 74LVC245
(Vcc 3.3 V) for the read direction, /OE-steered so exactly one is active.

Why this pairing works — the threshold arithmetic:

| Direction | Part | Why it's correct |
|---|---|---|
| Teensy 3.3 V → DUT 5 V | HCT245 at 5 V | VIH = 2.0 V, so a 3.3 V input is comfortably high; output is full 5 V push-pull |
| DUT 5 V → Teensy 3.3 V | LVC245 at 3.3 V | Inputs are 5 V-tolerant (with Ioff: 5 V on inputs while its rail is down is safe); output is strong 3.3 V push-pull into a 3.3 V Teensy input with full margin |

What must **never** be done: an LVC output into a 5 V HC input (VIH 3.15 V —
marginal) or LVC on the 5 V rail (absolute max 3.6 V).

Direction control on the board:

- **Ports A, B, E** — direction fixed by jumpers J8 / J12 / J15 (fitted =
  port drives the DUT, parked = port reads).
- **Ports C, D** — runtime direction on PIN_DIR0 / PIN_DIR1 (high = HCT
  drives, low = LVC reads). 10 k pulldowns on the OE nets mean **C and D
  power up reading** — the safe state, DUT never contended at boot or
  during flashing.
- The sketch must match the jumpers; print the assumed configuration in the
  boot banner.

'245 DIP-20 pinout trap: **the B pins descend** — 18..11 = B1..B8. Also
/OE is pin 19 next to Vcc at pin 20; tying /OE to 20 instead of GND
tri-states the chip silently. Both have cost bench time.

---

## 3. Software Architecture

Every sketch = `AdapterPorts.h` (shared, unchanged) + per-project `.ino` in
the same sketch folder. **The header is the single source of truth** for
the pin mapping and every port-level access primitive. If a sketch finds
itself writing GPIO plumbing, the plumbing belongs in the header.

### What the header provides

- Five virtual 8-bit ports **A–E**. A–D are data ports; **E is the
  strobe/sense port, PE0 = CLK by convention**. PE6/PE7 are shared with the
  buttons via jumpers J6/J7.
- Pins are `constexpr uint8_t` named `PIN_PA0`..`PIN_PE7`. The `P` prefix
  is mandatory: the core defines `PIN_A0`-style macros for analog pins.
- Ribbon colours: **bit 0 = white .. bit 7 = red** within each port.
- `PORT_A_PINS[8]`..`PORT_E_PINS[8]` tables + `setPortMode(table, mode)`.
- **Unrolled fast accessors** `readPortA()`..`readPortE()`,
  `writePortA(v)`..`writePortE(v)`. Compile-time-constant pins fold each
  bit to a single GPIO register operation. **Never convert these to
  loops** — a variable pin argument defeats the fast path entirely.
- **Banked I/O**: `PortBanks`, `buildPortBanks()`, `bankedWritePort()`,
  `bankedReadPort()` — see §5 for what it is and is not for.
- `samplePorts()` → ports A–D in one `uint32_t`.
- Runtime direction for C/D: `initPortDirections()` (call first in
  `setup()`; matches the pulldown power-up state), `portDrive('C'|'D')`,
  `portRelease('C'|'D')`. During `portDrive` the DUT-side bus is briefly
  undefined (~µs) — fit the port's SIP pulls (RN3/RN4 via J3/J4) when it
  talks to a tri-state DUT bus.
- Buttons **SW0/SW1** (note: zero-based, matching PA0..PE7) on Teensy
  40/41, momentary to GND, no board pull-ups → `SW_PIN_MODE` is
  `INPUT_PULLUP`. SW0 = start/advance, SW1 = abort/exit. 1 k legs make a
  jumper/sketch mismatch harmless. `sw0Pressed()`, `sw1Pressed()`.
- Banner helpers `printPortPins()`, `printPortBanks()` so every sketch
  reports its wiring the same way. Checking the printed pins against the
  loom has caught more faults than staring at schematics.

### Sketch conventions (unchanged and still earning their keep)

- `setup()`: direction per port. DUT outputs → `INPUT`; tri-state/shared
  rails → `INPUT_PULLUP` (fine now — no TXS keeper to divide against);
  harness-driven → `OUTPUT`.
- `loop()`: non-blocking, `millis()`-based, no `delay()`. Read Serial every
  pass, act immediately; long tests run in bounded chunks per pass so
  commands (especially stop) work mid-run.
- Boot banner: `PLATFORM_NAME`, the port pin tables, and every setting whose
  default matters (`settleNs`, I/O path, assumed jumper configuration).
  **All runtime settings reset on re-flash**; a silently wrong default has
  burned this project more than once.
- Bounded startup wait: `while (!Serial && millis() < 3000) { }`.
- Change-detection reporting, not continuous dumping.
- `F(...)` on string literals is no longer required (no AVR target) but is
  harmless and keeps old habits valid.

### Arduino build pitfall — sketch-level types in signatures

The Arduino build inserts generated function prototypes **above** the point
where a sketch-level type is declared. Any sketch function whose signature
mentions a sketch-defined enum or struct fails with *"variable or field
declared void"*. Convention: **mode values are plain `uint8_t` constants,
never an enum**, and sketch functions take only built-in types. Types
defined in `AdapterPorts.h` (e.g. `PortBanks`) are safe in signatures — the
include sits above the generated prototypes.

---

## 4. Measured Facts — Level245Loop Campaign (breadboard, 2026-07)

One port pair (A drive → HCT245 → 5 V bus → LVC245 → C read), wired direct
to the Teensy, verified loopback. Kept here so none of it is re-derived.

| Fact | Number |
|---|---|
| Hammer (0x00↔0xFF, all 8 lines simultaneously) | **17,000,000 vectors clean** banked, 10,000,000+ clean unrolled, zero errors |
| Same stress through the old TXS path | up to ~90% failure per event |
| Vector rate, unrolled accessors, settleNs=50 | ~335 ns/vector (hammer), 256 ns/vector (measured with read consumed) |
| Vector rate, banked I/O, same conditions | ~290 / 258 ns/vector — **within 1–2% of unrolled** |
| Banked vs unrolled agreement | all 256 patterns, all four write/read combinations ('V') |
| Settle requirement of the '245 pair + breadboard | **non-zero but below software resolution** (§5) |
| GPIO bank partition of the validated mapping | Port A spans 3 banks, Port C sits in 1 |

Conclusion of record: **as a data-path level translator, the HCT245/LVC245
pair removes the TXS failure class entirely.** What it does *not* yet prove
is anything involving a clock — see §8.

---

## 5. Timing, Settle, and the Two I/O Paths

### The settle requirement

A banked write followed **immediately** by a banked read fails every trial
— the sample lands mid-transition and slower channels return the *previous*
vector's value on that line (signature: walking-1 `0x08` reads back `0x0C`
after `0x04`; the spurious bits are stale, not random). The same pair
separated by `delayNanoseconds(0)` is clean, because the call itself costs
tens of ns of overhead (cycle-counter read plus compare loop) before it
delays anything. The pair's true requirement therefore lies inside that gap
— consistent with the datasheet sum of ~15 ns — and **cannot be resolved
from software on this part**.

Conventions that follow:

- **`settleNs = 50` (commanded) is the standard.** With call overhead that
  is ~70–90 ns elapsed, roughly 2–4× the requirement, at a cost of ~15% of
  vector time. Raise to 100 for extra headroom on long looms.
- **Never zero.** And never trust a tiny value: a nominal "2" works only
  because the call overhead is doing the job, and a core or compiler update
  could shrink that silently.
- Commanded ns **understates** elapsed time. Budget against elapsed.

### Unrolled vs banked — what each is for

They cost the same in real use (256 vs 258 ns/vector with the read
consumed and checksummed; 285 vs 290 in hammer runs). The difference is
**timing shape**, not speed:

- **Unrolled** (default): eight separate stores/loads. Bit 0 changes ~70 ns
  before bit 7; bit 0 is sampled well before bit 7. Fine for almost
  everything.
- **Banked**: every bit within a GPIO bank changes on **one** store and is
  sampled on **one** load. Two consequences:
  1. A genuinely simultaneous multi-line slew — what a real bus turnaround
     looks like, and the harshest coupling aggressor available. Use it when
     stressing a DUT with a clock or strobe in the loom.
  2. The shortest write-to-sample interval this rig can produce — **timing
     sweeps must run banked or they measure nothing** (the unrolled
     stagger alone exceeds what is being resolved; every row reads zero).

`PortBanks` costs ~4 KB each (256-entry set-mask table per bank); declare
only the ports a sketch actually banks. The partition is derived from the
pin tables at runtime, so it tracks the mapping automatically.

---

## 6. Cable Budget

Still real, but the failure modes changed. With strong push-pull at both
ends there are no one-shots to re-fire and no keeper to divide against;
what remains is ordinary capacitive loading (slower edges) and inductive
crosstalk between adjacent conductors during simultaneous slews.

| Length | Verdict |
|---|---|
| ≤ 15–30 cm | Comfortable. |
| ~35 cm | Fine for data; validated clean under 17 M simultaneous-slew vectors on the breadboard rig. |
| ≥ 1 m | Slowed edges; re-measure settle ('S' sweep, banked) before trusting full speed. |

Practices that stay: grounds in the ribbon alongside signals (ideally every
second conductor); route clock/strobe lines apart from operand groups with
their own ground return. Whether clock lines still need Schmitt
conditioning at the DUT end with this drive path is **an open question** —
see §8; do not assume either way.

---

## 7. Measurement Methodology — Traps That Cost This Project Time

Each of these produced a confidently wrong number during the campaign.
Written down so they are never rediscovered.

1. **Microbenchmarks that discard the read get optimised.** A throughput
   loop doing `(void)portRead()` let the compiler delete the banked path's
   bit-extraction arithmetic (elidable) while the unrolled path's eight
   volatile loads survived — reporting banked at 90 ns vs 151 when the true
   figures were within 1%. **Fold every read into a checksum and print it
   against the expected total.** The checksum also catches a measurement
   loop that is reading garbage (it did: a settle-free measure phase on the
   banked path produced a wrong checksum, correctly).
2. **`millis()` is useless for run timing.** A 10 ms reading is ±10%. Use
   `micros()` and report ns/vector.
3. **A sweep must pay identical overhead at every row.** Skipping the
   delay call when the commanded value is zero makes row 0 structurally
   different from every other row; the call overhead *appearing* between
   row 0 and row 1 looks exactly like a propagation cliff (it read as a
   crisp "10 ns requirement" for a while). Call `delayNanoseconds(n)` at
   every row including n = 0.
4. **When a microbenchmark and the real workload disagree, believe the
   workload.** The hammer figures (285 vs 290) were right all along.
5. **Sampled LED displays are not signal-integrity instruments.** The
   8-bit LED Thing (STM8, charlieplexed, ~1 µA floating sense inputs —
   electrically ideal for monitoring) aliases anything fast and misses
   runts entirely. Port readback is the instrument; LEDs are for parked
   patterns and slow chasers. Ground unused sense inputs (they float in
   mode 7).
6. **Structural fault vs timing fault, from the error pattern alone:**
   stale-previous-vector bits = sampling too early; incoherent values near
   threshold = mid-transition sampling; a fixed bit always wrong = wiring;
   an adjacent pair swapped = the descending B-pin run on a '245.

---

## 8. Open Item — the Phantom-Capture Question

The loopback campaign has **no clock and no register** in it, so the
original failure — coupled slew on data lines producing phantom capture
events in a '574 — is neither reproduced nor excluded for the new drive
path. It is expected to be gone (the aggressor coupling mechanism was
TXS-specific weak drive plus one-shots), but expectation is not
measurement.

The closing test, when wanted: a '574 on a 5 V port bus, clocked from PE0,
running the chase — slew all eight data lines N ns **after** the clock
edge via banked writes (the harsher aggressor), and look for exact
re-captures of the post-edge value. Exact re-captures = phantom clock.
That test ended the ALU campaign on the old path; it is the one that
certifies the new one. Until it runs, keep clock routing conservative
(§6) on any harness with a registered DUT.

---

## 9. PCB Bring-Up Plan (when the board arrives)

In order, stopping at the first failure:

1. **Power, no Teensy, no DUT**: rails, then DIR0/DIR1 nets read low
   (pulldowns) — every port must be in the read state.
2. **Chaser** on each port in its drive configuration — the traditional
   mapping verification. Any remap of the PCB vs the breadboard mapping
   gets caught here, before anything subtle.
3. **Level245Loop per loopback pair** (jumper one port's drive side to
   another's read side): `V`, then `M` (both checksums must print correct
   and the two figures should sit within a few %), then `W`/`E`, then `H`.
   Expect the breadboard numbers of §4 within reason; a PCB should be no
   worse than a breadboard.
4. **`S` sweep, banked**, per pair: expect clean at every row including
   zero commanded. Any row that fails on the PCB when the breadboard was
   clean is layout information — investigate before continuing.
5. **Runtime direction**: `portDrive`/`portRelease` on C and D against a
   tri-state bus with the SIP pulls fitted; confirm the turnaround window
   holds the defined level.
6. **Buttons and Port E** through their jumper positions.
7. Only then: the §8 chase rig.

Boot-banner discipline applies from step 2: read the printed pin tables and
settings before trusting any run.

---

## 10. Historical — TXS0108E Failure Modes (retired path)

Kept only for diagnosing old rigs or recognising these signatures if they
ever reappear. The TXS0108E held lines with a ~4 kΩ weak keeper between
one-shot edge accelerations. Consequences, all campaign-validated: LEDs
and resistor networks fought the keeper (10 k pull-up → ~1.6 V
mid-threshold node); ringing/crosstalk re-fired the one-shots (phantom
transitions); simultaneous 8-line slews through a 35 cm loom re-clocked
'574s up to ~90% per event; Schmitt clock conditioning ('HC14 + ~1 k /
100 pF, ≥100 k bias) took the worst case from 95% to ~1%. None of these
mechanisms exist on the '245 board.

---

## 11. Things That Have Bitten / Will Bite

- Naming any constant `PIN_A0`..`PIN_A9` → collides with core macros.
- Converting the unrolled accessors to loops → silently loses the
  single-instruction fast path.
- A sketch-level enum in a function signature → *"variable or field
  declared void"* from the prototype generator (§3).
- '245 B pins descend (18..11 = B1..B8); /OE (19) tied to Vcc (20) instead
  of GND tri-states the chip silently.
- `settleNs = 0`, or a tiny value that only works via `delayNanoseconds`
  call overhead (§5).
- Trusting a throughput number from a loop whose read is discarded (§7.1),
  or a run time from `millis()` (§7.2), or a sweep whose zero row skips the
  delay call (§7.3).
- Buttons are **SW0/SW1** (zero-based) with `INPUT_PULLUP`. Sketches
  written against the old SW1/SW2 naming fail to compile — which is the
  desired outcome; fix the sketch, not the header.
- Reading the LED Thing as evidence about fast events (§7.5).
- Forgetting that **all runtime settings reset on re-flash** — print them
  in the boot banner and read it.
- A DMM reading "5.00 V" says nothing about nanosecond transients. Wrong
  latched values at clock edges are a signal-integrity symptom even when
  every rail meters perfectly.
- Blaming the pin mapping for flaky bits → check wiring and the printed
  boot-banner tables first; the mapping is chaser-verified.
