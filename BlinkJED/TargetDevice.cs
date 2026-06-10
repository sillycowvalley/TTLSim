using System;
using System.Collections.Generic;

namespace BlinkyJed;

/// <summary>
/// A programmable target. Carries the JEDEC fuse count plus the AND-array
/// geometry and the region base addresses the writer and mapper key off.
///
/// All constants below are the published GAL fuse-map layout (the same values
/// GALasm uses): 16V8 array 0..2047, XOR@2048, signature@2056, AC1@2120,
/// PT@2128, SYN@2192, AC0@2193 (2194 fuses); 20V8 shifts up for its wider array.
///
/// Scope: 16V8 and 20V8 only.
/// </summary>
internal abstract class TargetDevice
{
    public abstract string Name { get; }
    public abstract int FuseCount { get; }   // JEDEC QF
    public abstract int PinCount { get; }     // JEDEC QP (package pin count)

    // AND-array geometry.
    public abstract int Rows { get; }        // product-term rows
    public abstract int Columns { get; }     // fuses per row
    public int LogicSize => Rows * Columns;

    // Region base addresses (JEDEC fuse numbers).
    public abstract int XorAddr { get; }
    public abstract int SigAddr { get; }
    public abstract int Ac1Addr { get; }
    public abstract int PtAddr { get; }
    public abstract int SynAddr { get; }
    public abstract int Ac0Addr { get; }

    // Region sizes (shared by 16V8 and 20V8).
    public const int XorSize = 8;
    public const int SigSize = 64;
    public const int Ac1Size = 8;
    public const int PtSize = 64;

    public static TargetDevice? Resolve(string name, List<string> errors)
    {
        string normalized = (name ?? "").Trim().ToUpperInvariant().Replace(" ", "");

        if (normalized.Length == 0)
        {
            errors.Add("No target device given (-d) or found in the .pld header.");
            return null;
        }

        // Match the family by its part-number core. This accepts every CUPL/WinCUPL
        // spelling and architecture suffix that selects the same fuse map: bare names
        // (G16V8), vendor prefixes (GAL/ATF/P), and mode suffixes (S/A/AS/MS) such as
        // G16V8S, G20V8A, G20V8AS. The suffix only tells CUPL which mode to auto-pick;
        // BlinkyJED decides the mode itself, so the same fuse map applies either way.
        if (normalized.Contains("16V8"))
            return new Gal16V8();
        if (normalized.Contains("20V8"))
            return new Gal20V8();

        errors.Add($"Unsupported device: '{name}'. Only the 16V8 and 20V8 families are supported.");
        return null;
    }
}

internal sealed class Gal16V8 : TargetDevice
{
    public override string Name => "GAL16V8";
    public override int FuseCount => 2194;
    public override int PinCount => 20;
    public override int Rows => 64;
    public override int Columns => 32;
    public override int XorAddr => 2048;
    public override int SigAddr => 2056;
    public override int Ac1Addr => 2120;
    public override int PtAddr => 2128;
    public override int SynAddr => 2192;
    public override int Ac0Addr => 2193;
}

internal sealed class Gal20V8 : TargetDevice
{
    public override string Name => "GAL20V8";
    public override int FuseCount => 2706;
    public override int PinCount => 24;
    public override int Rows => 64;
    public override int Columns => 40;
    public override int XorAddr => 2560;
    public override int SigAddr => 2568;
    public override int Ac1Addr => 2632;
    public override int PtAddr => 2640;
    public override int SynAddr => 2704;
    public override int Ac0Addr => 2705;
}