using System.Drawing;
using TTLSim.Core;

namespace TTLSim.UI.View;

/// <summary>
/// Net-state colour palette used while a simulation is running. Will move
/// into AppSettings once configuration & theming (doc §14) is implemented;
/// for now, hardcoded but in one place.
/// </summary>
public static class SignalColors
{
    public static readonly Color Low = Color.FromArgb(0x10, 0x10, 0x10);  // near-black (0V)
    public static readonly Color High = Color.FromArgb(0xDC, 0x2F, 0x2F);  // signal red (5V)
    public static readonly Color HighZ = Color.FromArgb(0x80, 0x80, 0x80);  // mid grey
    public static readonly Color Unknown = Color.FromArgb(0xFF, 0x8C, 0x00);  // bright orange

    public static Color For(Signal s) => s switch
    {
        Signal.High => High,
        Signal.Low => Low,
        Signal.HighZ => HighZ,
        _ => Unknown,
    };
}