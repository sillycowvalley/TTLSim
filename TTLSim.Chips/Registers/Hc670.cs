using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Registers;

/// <summary>
/// 74HC670 — 4×4 register file with 3-state outputs, 16-pin DIP.
///
/// Four 4-bit words with SEPARATE read and write ports: WB/WA address the
/// word being written, RB/RA the word being read, simultaneously and
/// independently (xB is the MSB of each address).
///
/// The write is a TRANSPARENT LATCH, not an edge: while /GW is LOW the
/// addressed word follows the D inputs continuously, and whatever is on D
/// when /GW rises is what stays latched. Changing the write address while
/// /GW is LOW immediately opens the newly addressed word to D as well.
/// The model reproduces this faithfully, because it IS the '670 write trap
/// the Blinky/Thumby boards gate against (/GW = WE ∧ CLK-low): an
/// ungated decode-/GW with a settling ALU behind it writes garbage, and
/// the simulator must be able to demonstrate that, not paper over it with
/// a convenient edge-triggered write.
///
/// The read port is combinational: with /GR LOW, Q1..Q4 drive the word
/// addressed by RB/RA (address-to-Q = the part's tPD); with /GR HIGH the
/// outputs are high-Z — the property that lets bank-stacked '670s share
/// port wires behind decode-guaranteed exclusive enables. Read-during-write
/// of the SAME word is transparent: Q follows D through the open latch,
/// as on the real part.
///
/// Pin map (from ChipPartDefinition.Ic74670):
///   D1=15 D2=1  D3=2  D4=3     data in (D1 the LSB)
///   WA=14 WB=13 /GW=12         write address (WB MSB) + write enable
///   RA=5  RB=4  /GR=11         read address (RB MSB) + output enable
///   Q1=10 Q2=9  Q3=7  Q4=6     data out, 3-state (Q1 the LSB)
///   VCC=16 GND=8               power (consumed by the build pipeline)
///
/// Inputs map Unknown/HighZ to Low (catalogue convention) — an unresolved
/// /GW or /GR therefore reads as asserted. TTL011 surfaces genuinely
/// floating pins at design time.
/// </summary>
public sealed class Hc670 : IChip
{
    public const long PropagationDelayPs = 45_000;

    // Indices into nets[] -- the order PinNumbers is declared in below
    // (physical pin order, power pins 16/8 excluded).
    private const int IndexD2 = 0;   // D2  (pin 1)
    private const int IndexD3 = 1;   // D3  (pin 2)
    private const int IndexD4 = 2;   // D4  (pin 3)
    private const int IndexRb = 3;   // RB  (pin 4)
    private const int IndexRa = 4;   // RA  (pin 5)
    private const int IndexQ4 = 5;   // Q4  (pin 6)  output
    private const int IndexQ3 = 6;   // Q3  (pin 7)  output
    private const int IndexQ2 = 7;   // Q2  (pin 9)  output
    private const int IndexQ1 = 8;   // Q1  (pin 10) output
    private const int IndexGr = 9;   // /GR (pin 11)
    private const int IndexGw = 10;  // /GW (pin 12)
    private const int IndexWb = 11;  // WB  (pin 13)
    private const int IndexWa = 12;  // WA  (pin 14)
    private const int IndexD1 = 13;  // D1  (pin 15)

    private readonly Net[] nets;
    private readonly bool[] isOutput;

    // qDrivers[i] drives Q(i+1) -- logical bit order, Q1 the LSB.
    private readonly Driver[] qDrivers = new Driver[4];
    private readonly long delayPs;

    /// <summary>The four 4-bit words (low nibble of each entry).</summary>
    private readonly int[] words = new int[4];

    private readonly ILogger logger;
    private readonly string label;

    public Hc670(
        Net d1, Net d2, Net d3, Net d4,
        Net wa, Net wb, Net gwN,
        Net ra, Net rb, Net grN,
        Net q1, Net q2, Net q3, Net q4,
        string label = "670",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below (physical pin order).
        nets = new[]
        {
            d2, d3, d4,      // pins 1..3
            rb, ra,          // pins 4..5
            q4, q3,          // pins 6..7
            q2, q1,          // pins 9..10
            grN, gwN,        // pins 11..12
            wb, wa,          // pins 13..14
            d1               // pin 15
        };

        isOutput = new bool[nets.Length];
        int[] qIdx = { IndexQ1, IndexQ2, IndexQ3, IndexQ4 };
        for (int i = 0; i < 4; i++)
        {
            qDrivers[i] = new Driver(nets[qIdx[i]], DriveStrength.Strong);
            isOutput[qIdx[i]] = true;
        }

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.delayPs = delayPs;
    }

    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        // Power-up contents are undefined on the real part; the simulator
        // starts at 0 (house convention, cf. the '173). If /GW is already
        // asserted the addressed word opens to D immediately.
        for (int i = 0; i < 4; i++) words[i] = 0;
        ApplyTransparentWrite();
        EmitOutputs(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Reacting to our own output change would just recompute the same
        // value. Every genuine input can affect the write latch or the read
        // port, so recompute both -- cheap, and read-during-write
        // transparency falls out for free.
        if (isOutput[pinIndex]) return;
        ApplyTransparentWrite();
        EmitOutputs(scheduler);
    }

    /// <summary>
    /// The transparent write: while /GW is LOW the word addressed by WB/WA
    /// follows the D inputs. Called on every input change, so D edges,
    /// write-address moves, and /GW itself all land -- including the hazard
    /// cases the physical clock-low gating discipline exists to prevent.
    /// </summary>
    private void ApplyTransparentWrite()
    {
        if (nets[IndexGw].Value != Signal.Low) return;

        int addr = (Bit(IndexWb) << 1) | Bit(IndexWa);
        int data = ReadDataInputs();
        if (words[addr] != data)
        {
            logger.LogDebug("{Label} /GW low: word {Addr} {Old:X1} -> {New:X1}",
                label, addr, words[addr], data);
            words[addr] = data;
        }
    }

    private int ReadDataInputs()
    {
        int v = 0;
        if (nets[IndexD1].Value == Signal.High) v |= 1;
        if (nets[IndexD2].Value == Signal.High) v |= 2;
        if (nets[IndexD3].Value == Signal.High) v |= 4;
        if (nets[IndexD4].Value == Signal.High) v |= 8;
        return v;
    }

    private int Bit(int index) => nets[index].Value == Signal.High ? 1 : 0;

    /// <summary>
    /// Drive Q1..Q4 from the word addressed by RB/RA when /GR is LOW;
    /// high-Z otherwise (the bank-stacking share).
    /// </summary>
    private void EmitOutputs(IScheduler scheduler)
    {
        bool enabled = nets[IndexGr].Value == Signal.Low;
        int word = words[(Bit(IndexRb) << 1) | Bit(IndexRa)];

        for (int i = 0; i < 4; i++)
        {
            Signal s = enabled
                ? (((word >> i) & 1) != 0 ? Signal.High : Signal.Low)
                : Signal.HighZ;
            scheduler.Schedule(delayPs, qDrivers[i], s);
        }
    }
}
