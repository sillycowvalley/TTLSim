# Fastest True-DIP Classic 8-Bit CPUs

Fastest through-hole **DIP-packaged** part for each classic 8-bit family, ranked by
approximate MIPS. "True DIP" excludes PLCC/QFP (even when socketed), which is what
removes the fast Z180 grades and the eZ80 entirely.

| Part | Family | DIP pkg | Max clk (DIP) | MIPS/MHz | ~MIPS |
|---|---|---|---|---|---|
| WDC **W65C02S** | 6502 | DIP-40 | 14 MHz | 0.43 | ~6 |
| WDC **W65C816S** | 65816 | DIP-40 | 14 MHz | 0.43&nbsp;\* | ~6 |
| Zilog **Z84C0020** | Z80 | DIP-40 | 20 MHz | 0.15 | ~3 |
| Zilog **Z8S180** | Z180 | DIP-64 | ~10 MHz | 0.20 | ~2 |
| Hitachi **HD63C09E** | 6809 | DIP-40 | 3 MHz | 0.22&nbsp;† | ~0.7 |
| Intel **8080A-1** | 8080 | DIP-40 | 3 MHz | 0.14 | ~0.4 |
| Motorola **MC68B09E** | 6809 | DIP-40 | 2 MHz | 0.20 | ~0.4 |
| Motorola **MC68B00** | 6800 | DIP-40 | 2 MHz | 0.18 | ~0.35 |
| *Blinky (discrete, not DIP)* | — | n/a | 10 MHz | **1.0** | **~10** |

\* 65C816 native mode does 16-bit work per instruction, so effective throughput runs above the instruction-count MIPS shown.
† HD63C09E in native 6309 mode runs meaningfully higher per clock than this 6809-emulation figure.

## Notes

### MIPS/MHz is the whole story
The per-MHz column isolates architecture from clock speed. The ~3× gap between the 6502
(0.43) and the Z80 (0.15) is exactly why a 14 MHz 65C02 beats a 20 MHz Z80: the Z80 has
to out-clock the 6502 by 3× just to tie, and in true DIP it can't.

### The two Zilogs reorder once per-MHz is explicit
The 20 MHz DIP Z80 (~3) edges the true-DIP-capped ~10 MHz Z180 (~2), even though the Z180
is more efficient per clock (0.20 vs 0.15). The Z180's efficiency can't recover from
losing half its clock — its fast 20/33 MHz grades ship only in QFP/PLCC. The only true-DIP
Z180 part is the 8 MHz Z80180 (64-pin DIP); ~10 MHz is the realistic DIP ceiling.

### 8080A caveats
- **Per clock it's ~the Z80** (0.14 vs 0.15). The Z80 kept the 8080's efficiency and just
  added clock headroom — the 8080A isn't beaten by a per-instruction deficit, it's beaten
  by topping out at 2–3 MHz where the CMOS Z80 reaches 20. Same family, ~7× the clock ceiling.
- **By far the most painful row to build.** Unlike every other part here (single +5 V rail,
  single-phase clock), the 8080A needs **three supplies (+5, −5, +12 V)** and a **two-phase
  clock**, so in practice it drags along the 8224 clock generator and 8228 system controller
  just to run. Largest single reason the Z80 displaced it.

### Availability & packaging
- **In production:** WDC W65C02S, W65C816S (both DIP-40, 14 MHz).
- **NOS / limited:** Zilog Z84C00 (40-pin DIP is EOL, new-old-stock plentiful), Z8S180/Z80180
  DIP-64, HD63C09E, MC68B09E, MC68B00, 8080A-1.
- **Excluded — not true DIP:** eZ80 (LQFP-only, ~50 MHz / ~80 MIPS); the fast Z180 grades
  (20 MHz QFP, 33 MHz PLCC/QFP).

### The Blinky line
Single-cycle execution means one instruction per clock **by construction** — 1.0 MIPS/MHz,
more than double the best classic 8-bit ISA. That's why 10 MHz of discrete logic
out-throughputs a 20 MHz Z80 or a 14 MHz 65C02 on raw instruction count: not clock,
efficiency. The classic parts win on integration, clock ceiling, and being a single chip;
the single-cycle datapath wins on work-per-tick.

### Caveats on the numbers
MIPS/MHz values are architecture-typical estimates for **ranking, not benchmarking**.
Cross-ISA comparison is soft: a Z80/Z180 instruction does more than a 6502 one, so on
memory-move or 16-bit workloads the ordering can shift. Peak-clock and typical-code figures
also differ; these lean toward typical mixed code.
