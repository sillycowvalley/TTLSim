using TTLSim.Core;

namespace TTLSim.Chips.Displays;

/// <summary>
/// Common-anode 7-segment display. A passive listener -- never schedules
/// anything. The UI reads Segments and Dp each frame to render the display.
///
/// Lighting rule: common pin = High (anode at VCC) AND segment pin = Low
/// (cathode pulled low, current flows, LED lit).
/// </summary>
public sealed class SevenSegCa : IChip
{
    // Indices: 0..6 = a..g, 7 = dp, 8 = common.
    private const int SegmentCount = 7;
    private const int IndexDp = 7;
    private const int IndexCommon = 8;

    private readonly Net[] nets;
    private readonly bool[] segments = new bool[SegmentCount];
    private bool dp;

    public SevenSegCa(Net a, Net b, Net c, Net d, Net e, Net f, Net g, Net dp, Net common)
    {
        nets = new[] { a, b, c, d, e, f, g, dp, common };
    }

    public IReadOnlyList<int> PinNumbers { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    public IReadOnlyList<Net> Nets => nets;

    /// <summary>Current state of segments a..g. Indexed 0=a, 1=b, ..., 6=g.</summary>
    public IReadOnlyList<bool> Segments => segments;

    /// <summary>Current state of the decimal point.</summary>
    public bool Dp => dp;

    public void Initialize(IScheduler scheduler) => Recompute();

    public void OnInputChanged(int pinIndex, IScheduler scheduler) => Recompute();

    private void Recompute()
    {
        bool poweredAnode = nets[IndexCommon].Value == Signal.High;

        for (int i = 0; i < SegmentCount; i++)
            segments[i] = poweredAnode && nets[i].Value == Signal.Low;

        dp = poweredAnode && nets[IndexDp].Value == Signal.Low;
    }
}