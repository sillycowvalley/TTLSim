namespace TTLSim.Core;

/// <summary>
/// Four-state logic value for a net or pin.
/// </summary>
public enum Signal
{
    /// <summary>Logic 0, actively driven low.</summary>
    Low,

    /// <summary>Logic 1, actively driven high.</summary>
    High,

    /// <summary>High impedance — pin disconnected from rails (tri-state output disabled).</summary>
    HighZ,

    /// <summary>Indeterminate — net has conflicting drivers, or sampled from a floating net.</summary>
    Unknown
}