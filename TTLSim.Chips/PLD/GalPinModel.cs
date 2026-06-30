using System;
using System.Collections.Generic;
using TTLSim.Core;

namespace TTLSim.Chips.Pld;

/// <summary>
/// The role a GAL pin plays under the currently loaded fuse map. A GAL is
/// whatever its fuses say it is: the same physical part is a different pinout
/// per program, so this is derived from the JEDEC map rather than from a fixed
/// symbol.
///
///   Input         -- a dedicated array input (must be driven; floating-checked)
///   Output        -- a programmed OLMC that drives its pin
///   Clock         -- pin 1 in registered mode (mode 3) only
///   OutputEnable  -- the /OE pin in registered mode (mode 3) only
///   Unused        -- an output-capable OLMC that is unprogrammed (drives nothing)
/// </summary>
public enum GalPinRole
{
    Input,
    Output,
    Clock,
    OutputEnable,
    Unused
}

/// <summary>One pin's derived role and display label under a loaded fuse map.</summary>
public readonly record struct GalPin(int Number, GalPinRole Role, string Label);

/// <summary>
/// Derives each GAL pin's function and direction from the fuse map, so the
/// symbol, the DRC, and the simulator all read from the same source of truth.
/// The logic engine (<see cref="Gal"/>) already decides drive from the fuses;
/// this is the pin/presentation model catching up to that.
///
/// Mode is selected by the SYN/AC0 fuses:
///   SYN=1 AC0=0 -> simple (mode 1)    pin 1 / OE pin are plain inputs
///   SYN=1 AC0=1 -> complex (mode 2)   pin 1 / OE pin are plain inputs
///   SYN=0 AC0=1 -> registered (mode 3) pin 1 = CLK, OE pin = /OE
///
/// For an OLMC pin (modes 1/2): AC1 = 1 makes it a dedicated array input,
/// AC1 = 0 makes it output-capable. The two mode-2-forcing special pins can
/// never be inputs. An output-capable cell with a programmed (blown) block
/// drives ("OUT"); an erased block is unprogrammed ("NC").
/// </summary>
public static class GalPinModel
{
    /// <summary>
    /// Derive pin roles/labels from a part number and the JEDEC program text.
    /// Returns null when there is no program, the part isn't a known GAL, or
    /// the map is malformed -- callers then fall back to the static symbol.
    /// </summary>
    public static IReadOnlyList<GalPin>? TryDerive(string partNumber, string? programJedec)
    {
        if (string.IsNullOrWhiteSpace(programJedec)) return null;

        GalDevice? device = GalDevice.ForPartNumber(partNumber);
        if (device is null) return null;

        bool[] fuses;
        try
        {
            bool[] parsed = JedecFuseMap.Parse(programJedec).Fuses;
            fuses = new bool[device.FuseCount];
            Array.Copy(parsed, fuses, Math.Min(parsed.Length, fuses.Length));
        }
        catch (FormatException)
        {
            return null;
        }

        return Derive(device, fuses);
    }

    /// <summary>
    /// Derive pin roles/labels from a parsed fuse array. The fuse map is
    /// authoritative; power and ground pins are not returned (the caller keeps
    /// those from the static definition).
    /// </summary>
    public static IReadOnlyList<GalPin> Derive(GalDevice device, bool[] fuses)
    {
        bool syn = device.SynFuse < fuses.Length && fuses[device.SynFuse];
        bool ac0 = device.Ac0Fuse < fuses.Length && fuses[device.Ac0Fuse];
        bool registered = !syn && ac0;   // mode 3

        var pins = new List<GalPin>();

        // Pin 1: clock in registered mode, a plain input otherwise.
        pins.Add(new GalPin(
            device.ClockPin,
            registered ? GalPinRole.Clock : GalPinRole.Input,
            registered ? "CLK" : "IN"));

        // OE pin: /OE in registered mode, a plain input otherwise.
        pins.Add(new GalPin(
            device.OePin,
            registered ? GalPinRole.OutputEnable : GalPinRole.Input,
            registered ? "/OE" : "IN"));

        // Dedicated input pins are always inputs in every mode.
        foreach (int p in device.DedicatedInputPins)
            pins.Add(new GalPin(p, GalPinRole.Input, "IN"));

        // OLMC pins: direction from the mode plus the per-OLMC AC1 bit.
        for (int o = 0; o < device.OlmcCount; o++)
        {
            int pin = device.OlmcOutputPins[o];

            // AC1 is indexed by the macrocell's position from the first OLMC
            // pin (pin 19 -> 7, pin 12 -> 0 on a 16V8), and the AC1 bits run
            // in reverse: AC1[7 - olmc].
            int olmc = pin - device.FirstOlmcPin;
            int ac1Addr = device.Ac1FuseBase + (7 - olmc);
            bool ac1 = ac1Addr >= 0 && ac1Addr < fuses.Length && fuses[ac1Addr];

            bool special = Array.IndexOf(device.SpecialPins, pin) >= 0;

            // Modes 1/2: AC1 = 1 makes a non-special cell a dedicated input.
            // Mode 3 OLMCs are always output-capable (registered or combinational).
            if (!registered && ac1 && !special)
            {
                pins.Add(new GalPin(pin, GalPinRole.Input, "IN"));
                continue;
            }

            // Output-capable. It drives only if its product-term block is
            // programmed (has a blown fuse); an erased block is unconfigured.
            bool drives = BlockHasBlownFuse(device, fuses, o);
            pins.Add(new GalPin(
                pin,
                drives ? GalPinRole.Output : GalPinRole.Unused,
                drives ? "OUT" : "NC"));
        }

        return pins;
    }

    // An OLMC drives its pin only if some fuse in its row block is blown. An
    // all-intact block is the erased/unconfigured state. Mirrors the same test
    // the Gal evaluator uses to decide whether to create a driver.
    private static bool BlockHasBlownFuse(GalDevice device, bool[] fuses, int olmcArrayIndex)
    {
        int firstRow = olmcArrayIndex * device.ProductTermsPerOlmc;
        int start = firstRow * device.Cols;
        int end = start + device.ProductTermsPerOlmc * device.Cols;
        for (int i = start; i < end && i < fuses.Length; i++)
            if (fuses[i]) return true;
        return false;
    }
}