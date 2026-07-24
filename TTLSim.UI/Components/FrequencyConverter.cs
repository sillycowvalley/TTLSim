using System;
using System.ComponentModel;
using System.Globalization;

namespace TTLSim.UI.Components;

/// <summary>
/// PropertyGrid converter for a frequency held in hertz: displays "1 MHz" and
/// accepts free-form input ("1MHz", "10k", "1e6", "3.579545 MHz").
///
/// <para>Formatting and parsing themselves live on <see cref="ClockSource"/>
/// (<see cref="ClockSource.FormatFrequency"/> /
/// <see cref="ClockSource.ParseFrequency"/>), which remains the single
/// definition of what a frequency string means anywhere in the app. This class
/// was originally nested inside ClockSource and private; it was lifted out
/// verbatim so other frequency-bearing items -- the testbench's row rate --
/// present and accept exactly the same syntax rather than growing a private
/// near-copy each.</para>
/// </summary>
internal sealed class FrequencyConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(ITypeDescriptorContext? context,
        CultureInfo? culture, object value)
    {
        if (value is string s)
        {
            var parsed = ClockSource.ParseFrequency(s);
            if (parsed.HasValue) return parsed.Value;
            throw new FormatException($"'{s}' is not a valid frequency.");
        }
        return base.ConvertFrom(context, culture, value);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context,
        CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType == typeof(string) && value is double hz)
            return ClockSource.FormatFrequency(hz);
        return base.ConvertTo(context, culture, value, destinationType);
    }
}
