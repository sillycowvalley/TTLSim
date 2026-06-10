using System.Drawing;

namespace TTLSim.UI.Model;

/// <summary>
/// Colour applied to a single <see cref="Connection"/> when rendered. The
/// palette mirrors the standard set of breadboard jumper-wire colours,
/// extended with cooler/pastel tones for variety so larger schematics can
/// keep many distinct nets visually separate. Shades are tuned to read
/// well on a white canvas alongside black gate outlines, and to remain
/// distinct from the magenta selection highlight.
/// </summary>
public enum WireColor
{
    Black,
    Grey,
    Brown,
    Red,
    Orange,
    Yellow,
    Olive,
    Green,
    Teal,
    Cyan,
    Blue,
    Navy,
    Purple,
    Pink,
    White,
    Tan
}

/// <summary>
/// Conversion from the logical <see cref="WireColor"/> to a concrete
/// <see cref="Color"/> used at paint time. Keeping this mapping in one
/// place means tweaking a shade is a one-line edit.
/// </summary>
public static class WireColors
{
    public static Color ToColor(this WireColor color) => color switch
    {
        WireColor.Black => Color.FromArgb(30, 30, 30),
        WireColor.Grey => Color.FromArgb(130, 130, 130),
        WireColor.Brown => Color.FromArgb(130, 75, 40),
        WireColor.Red => Color.FromArgb(200, 50, 50),
        WireColor.Orange => Color.FromArgb(230, 130, 30),
        WireColor.Yellow => Color.FromArgb(220, 180, 40),
        WireColor.Olive => Color.FromArgb(160, 160, 60),
        WireColor.Green => Color.FromArgb(50, 150, 70),
        WireColor.Teal => Color.FromArgb(40, 150, 150),
        WireColor.Cyan => Color.FromArgb(80, 180, 220),
        WireColor.Blue => Color.FromArgb(40, 90, 200),
        WireColor.Navy => Color.FromArgb(30, 50, 110),
        WireColor.Purple => Color.FromArgb(130, 70, 180),
        WireColor.Pink => Color.FromArgb(230, 130, 170),
        WireColor.White => Color.FromArgb(240, 240, 240),
        WireColor.Tan => Color.FromArgb(200, 170, 130),
        _ => Color.FromArgb(30, 30, 30)
    };
}