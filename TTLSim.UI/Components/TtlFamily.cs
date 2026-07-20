namespace TTLSim.UI.Components;

/// <summary>
/// 74-series TTL logic family. Determines propagation delay etc. at
/// simulate-time; for now affects only the FullPartNumber display string
/// (74LS00 vs 7400 vs 74HC00, etc). LVC is the 3.3 V low-voltage CMOS
/// family (5 V-tolerant inputs) used on the Teensy level-shifter harness.
/// </summary>
public enum TtlFamily { Standard, L, H, S, LS, AS, ALS, F, HC, HCT, AC, ACT, LVC }
