using System.Drawing;

namespace TTLSim.UI.Model;

/// <summary>
/// The shared named-colour palette used across the app -- wire colours on
/// <see cref="Connection"/> and the fill/border/text colours on cosmetic
/// items (rectangles, labels). The palette mirrors the standard set of
/// breadboard jumper-wire colours, extended with cooler/pastel tones so larger
/// schematics can keep many distinct nets visually separate. Shades are tuned
/// to read well on a white canvas alongside black gate outlines, and to remain
/// distinct from the magenta selection highlight.
///
/// Exposed as an enum so the property grid renders it as a plain dropdown.
/// Member names are the persisted form (the colour serialises as its name,
/// e.g. "Red"), so they must not be renamed -- that would orphan the colour in
/// every existing .ttlproj. "Grey" keeps its British spelling for that reason.
/// </summary>
public enum TTLColor
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
/// Conversion from the logical <see cref="TTLColor"/> to a concrete
/// <see cref="Color"/> used at paint time. Keeping this mapping in one
/// place means tweaking a shade is a one-line edit.
/// </summary>
public static class TTLColors
{
    public static Color ToColor(this TTLColor color) => color switch
    {
        TTLColor.Black => Color.FromArgb(30, 30, 30),
        TTLColor.Grey => Color.FromArgb(130, 130, 130),
        TTLColor.Brown => Color.FromArgb(130, 75, 40),
        TTLColor.Red => Color.FromArgb(150, 45, 45),
        TTLColor.Orange => Color.FromArgb(230, 130, 30),
        TTLColor.Yellow => Color.FromArgb(220, 180, 40),
        TTLColor.Olive => Color.FromArgb(160, 160, 60),
        TTLColor.Green => Color.FromArgb(50, 150, 70),
        TTLColor.Teal => Color.FromArgb(40, 150, 150),
        TTLColor.Cyan => Color.FromArgb(80, 180, 220),
        TTLColor.Blue => Color.FromArgb(40, 90, 200),
        TTLColor.Navy => Color.FromArgb(30, 50, 110),
        TTLColor.Purple => Color.FromArgb(130, 70, 180),
        TTLColor.Pink => Color.FromArgb(230, 130, 170),
        TTLColor.White => Color.FromArgb(240, 240, 240),
        TTLColor.Tan => Color.FromArgb(200, 170, 130),
        _ => Color.FromArgb(30, 30, 30)
    };
}