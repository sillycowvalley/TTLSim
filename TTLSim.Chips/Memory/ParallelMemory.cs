using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Memory;

/// <summary>
/// Generic asynchronous parallel memory: one behavioural engine for the whole
/// 28C-series EEPROM family (28C256/128/64/16), the pin-compatible 62-series
/// SRAM, the 24-pin 6116 SRAM, and the narrow 4-bit 2114 SRAM. The parts differ
/// only in configuration, supplied at construction:
///
///   • address width  -- the number of address nets (size = 2^n)
///   • data width      -- the number of I/O nets (8 for byte-wide parts, 4 for
///                         the nibble-wide 2114)
///   • writable        -- false for EEPROM/ROM (programmed out of band, /WE
///                         ignored at runtime); true for SRAM (latches the bus
///                         on the /WE rising edge)
///   • /OE present?    -- some parts (the 2114) have no output-enable pin; pass
///                         a null /OE net and outputs drive whenever /CE is LOW
///                         and /WE is HIGH
///   • initialContents -- the program image for an EEPROM; null (blank) for SRAM
///   • accessTimePs    -- address/enable-valid to data-valid, per speed grade
///   • pinNumbers      -- physical pins parallel to the net order below, so every
///                         family shares this class with only a different pin list
///
/// Net order (built by the factory, matched by pinNumbers):
///   address A0(LSB)..A(n-1), data I/O0..I/O(w-1), then /CE, [/OE], /WE.
///   The /OE slot is present only when the part has an output-enable pin.
///
/// Three-state I/O: the bus is driven only when /CE LOW, /OE LOW (or absent),
/// /WE HIGH (read). During a write (/CE LOW, /WE LOW) the outputs float so the
/// external writer owns the bus; the addressed cell is captured on the /WE rising
/// edge. Deselected (/CE HIGH) the outputs float. Inputs map Unknown/HighZ to 0.
/// </summary>
public sealed class ParallelMemory : IChip
{
    private readonly int addressLines;
    private readonly int dataWidth;
    private readonly int addrBase;
    private readonly int dataBase;
    private readonly int ceIdx;
    private readonly int oeIdx;       // -1 when the part has no /OE pin
    private readonly int weIdx;
    private readonly bool hasOe;

    private readonly bool writable;
    private readonly long accessTimePs;
    private readonly byte[] contents;

    private readonly Net[] nets;
    private readonly Driver[] dataDrivers;
    private Signal prevWe = Signal.Unknown;

    private readonly ILogger logger;
    private readonly string label;

    public ParallelMemory(
        Net[] address, Net[] data,
        Net ceN, Net? oeN, Net weN,
        bool writable,
        byte[]? initialContents,
        long accessTimePs,
        int[] pinNumbers,
        string label,
        ILogger? logger = null)
    {
        if (data.Length is < 1 or > 8)
            throw new ArgumentException("data width must be 1..8", nameof(data));
        if (address.Length is < 1 or > 24)
            throw new ArgumentException("implausible address width", nameof(address));

        addressLines = address.Length;
        dataWidth = data.Length;
        hasOe = oeN is not null;

        addrBase = 0;
        dataBase = addressLines;
        ceIdx = addressLines + dataWidth;
        oeIdx = hasOe ? ceIdx + 1 : -1;
        weIdx = hasOe ? ceIdx + 2 : ceIdx + 1;

        this.writable = writable;
        this.accessTimePs = accessTimePs;

        int size = 1 << addressLines;
        contents = new byte[size];
        if (initialContents is not null)
            Array.Copy(initialContents, contents, Math.Min(initialContents.Length, size));

        int netCount = addressLines + dataWidth + (hasOe ? 3 : 2);
        nets = new Net[netCount];
        Array.Copy(address, 0, nets, addrBase, addressLines);
        Array.Copy(data, 0, nets, dataBase, dataWidth);
        nets[ceIdx] = ceN;
        if (hasOe) nets[oeIdx] = oeN!;
        nets[weIdx] = weN;

        dataDrivers = new Driver[dataWidth];
        for (int i = 0; i < dataWidth; i++)
            dataDrivers[i] = new Driver(data[i], DriveStrength.Strong);

        if (pinNumbers.Length != nets.Length)
            throw new ArgumentException("pinNumbers must match the net count", nameof(pinNumbers));
        PinNumbers = pinNumbers;

        this.label = label;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public IReadOnlyList<int> PinNumbers { get; }

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        prevWe = nets[weIdx].Value;
        DriveOutputs(scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        // Our own data outputs changing is not an input event. (During a write
        // the writer owns the bus and we capture on the /WE edge, so we never
        // need to react to data-line transitions directly.)
        if (pinIndex >= dataBase && pinIndex < dataBase + dataWidth) return;

        if (pinIndex == weIdx) HandleWeChange(scheduler);
        DriveOutputs(scheduler);
    }

    private void HandleWeChange(IScheduler scheduler)
    {
        Signal we = nets[weIdx].Value;
        bool rising = prevWe == Signal.Low && we == Signal.High;
        prevWe = we;

        // Capture the bus on the /WE rising edge (write-cycle end), SRAM-style,
        // provided the chip is selected and this part is writable.
        if (writable && rising && nets[ceIdx].Value == Signal.Low)
        {
            int addr = ReadAddress();
            byte v = ReadDataBus();
            contents[addr] = v;
            logger.LogDebug("{Label} write [{Addr:X4}] = {Val:X2}", label, addr, v);
        }
    }

    private void DriveOutputs(IScheduler scheduler)
    {
        bool selected = nets[ceIdx].Value == Signal.Low;
        bool oeAsserted = !hasOe || nets[oeIdx].Value == Signal.Low;
        bool reading = selected
            && oeAsserted
            && nets[weIdx].Value != Signal.Low;   // not mid-write

        if (!reading)
        {
            // Deselected, mid-write, or output-disabled: release the bus.
            for (int i = 0; i < dataWidth; i++)
                scheduler.Schedule(accessTimePs, dataDrivers[i], Signal.HighZ);
            return;
        }

        int addr = ReadAddress();
        byte value = contents[addr];
        logger.LogDebug("{Label} read [{Addr:X4}] = {Val:X2}", label, addr, value);

        for (int i = 0; i < dataWidth; i++)
            scheduler.Schedule(accessTimePs, dataDrivers[i],
                ((value >> i) & 1) != 0 ? Signal.High : Signal.Low);
    }

    private int ReadAddress()
    {
        int addr = 0;
        for (int i = 0; i < addressLines; i++)
            if (nets[addrBase + i].Value == Signal.High) addr |= 1 << i;
        return addr;
    }

    private byte ReadDataBus()
    {
        int v = 0;
        for (int i = 0; i < dataWidth; i++)
            if (nets[dataBase + i].Value == Signal.High) v |= 1 << i;
        return (byte)v;
    }
}