using TTLSim.Core;

namespace TTLSim.Chips.Sources;

/// <summary>
/// Stimulus source driving a <see cref="TestbenchProgram"/> onto a set of pins.
/// One pin per program column; row k is applied at tick k * rowPeriod.
///
/// <para>SCHEDULING. Unlike <see cref="ClockSource"/>, which schedules one edge
/// at a time and self-wakes when its own net changes, this source schedules its
/// whole program during <see cref="Initialize"/>. That is deliberate: a
/// self-waking design stalls the moment a row happens to change nothing (no net
/// transition, so no callback, so no next row), and a testbench that silently
/// stops partway through is a far worse failure than a few extra queued events.
/// Only CHANGES are scheduled -- a cell equal to the previous row's value on the
/// same column emits nothing -- so the event count is the number of transitions
/// in the file, not rows x columns.</para>
///
/// <para>END OF PROGRAM: hold. After the last row's changes there is nothing
/// further queued, so every driver simply retains its final value for the rest
/// of the run. A column whose last cell is Z stays released.</para>
///
/// <para>The source ignores input changes entirely. Its pins are outputs; the
/// program is time-driven, so nothing the circuit does can advance, delay or
/// perturb it. A column left on H or L while the circuit drives the same net is
/// a genuine contention and is reported by the runtime fault detector -- write
/// Z on that column for the cycles the circuit owns the net.</para>
/// </summary>
public sealed class TestbenchSource : IChip
{
    private readonly Net[] nets;
    private readonly int[] pins;
    private readonly Driver[] drivers;
    private readonly TestbenchProgram program;
    private readonly long rowPeriodPs;

    /// <summary>
    /// <paramref name="columnNets"/> and <paramref name="pinNumbers"/> are in
    /// PROGRAM COLUMN ORDER: entry i belongs to column i of
    /// <paramref name="program"/>. A column whose pin is unwired gets a
    /// stand-in net from the caller and drives nothing real.
    /// </summary>
    public TestbenchSource(
        TestbenchProgram program,
        IReadOnlyList<int> pinNumbers,
        IReadOnlyList<Net> columnNets,
        long rowPeriodPicoseconds)
    {
        if (pinNumbers.Count != program.ColumnCount)
            throw new ArgumentException(
                $"Testbench has {program.ColumnCount} column(s) but {pinNumbers.Count} pin number(s).",
                nameof(pinNumbers));
        if (columnNets.Count != program.ColumnCount)
            throw new ArgumentException(
                $"Testbench has {program.ColumnCount} column(s) but {columnNets.Count} net(s).",
                nameof(columnNets));

        this.program = program;
        rowPeriodPs = rowPeriodPicoseconds > 0 ? rowPeriodPicoseconds : 1;

        pins = new int[pinNumbers.Count];
        for (int i = 0; i < pinNumbers.Count; i++) pins[i] = pinNumbers[i];

        nets = new Net[columnNets.Count];
        drivers = new Driver[columnNets.Count];
        for (int i = 0; i < columnNets.Count; i++)
        {
            nets[i] = columnNets[i];
            drivers[i] = new Driver(nets[i], DriveStrength.Strong);
        }
    }

    public IReadOnlyList<int> PinNumbers => pins;

    public IReadOnlyList<Net> Nets => nets;

    /// <summary>Number of rows in the loaded program -- the run length, in rows.</summary>
    public int RowCount => program.RowCount;

    /// <summary>Simulated time (ps) at which row <paramref name="row"/> is applied.</summary>
    public long TimeOfRow(int row) => row * rowPeriodPs;

    public void Initialize(IScheduler scheduler)
    {
        // IScheduler.Schedule takes a DELAY from the current tick, and IChip
        // documents Initialize as running at tick 0 -- so a delay of
        // row * rowPeriod here IS the absolute time of that row. Everything
        // below depends on that; scheduling the program from anywhere other
        // than Initialize would need the delays rebased against "now".
        //
        // Row 0 lands at tick 0 in full: every column is scheduled, because
        // there is no previous row for a value to be unchanged from.
        for (int c = 0; c < program.ColumnCount; c++)
            scheduler.Schedule(0, drivers[c], program[0, c]);

        // Later rows emit only what changed on that column.
        for (int row = 1; row < program.RowCount; row++)
        {
            long at = row * rowPeriodPs;
            for (int c = 0; c < program.ColumnCount; c++)
            {
                Signal value = program[row, c];
                if (value == program[row - 1, c]) continue;
                scheduler.Schedule(at, drivers[c], value);
            }
        }
    }

    /// <summary>
    /// Ignored. Every pin is an output and the program is driven by simulated
    /// time alone, so there is no input to react to -- and reacting to our own
    /// output transitions would only re-drive values already scheduled.
    /// </summary>
    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
    }
}
