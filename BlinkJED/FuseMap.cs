using System;

namespace BlinkyJed;

/// <summary>
/// The fuse array for a device, partitioned into the GAL regions (mirroring
/// GALasm's JedecStruct): the AND array plus XOR (polarity), signature, AC1,
/// product-term-disable (PT), and the SYN / AC0 architecture bits.
///
/// Convention: a value of 1 emits '1' in the JEDEC list, 0 emits '0'. The
/// default (unlisted) state is 0, declared by the writer's *F0 field. The
/// meaning of 1 vs 0 is the device's; the mapper sets these to match it.
/// </summary>
internal sealed class FuseMap
{
    public readonly TargetDevice Device;
    public readonly byte[] Logic;   // AND array, row-major: row r at [r*Columns ..]
    public readonly byte[] Xor;     // output polarity, one per OLMC
    public readonly byte[] Sig;     // user signature
    public readonly byte[] Ac1;     // AC1 bits
    public readonly byte[] Pt;      // product-term disable
    public byte Syn;                // SYN architecture bit
    public byte Ac0;                // AC0 architecture bit

    public FuseMap(TargetDevice device)
    {
        Device = device;
        Logic = new byte[device.LogicSize];
        Xor = new byte[TargetDevice.XorSize];
        Sig = new byte[TargetDevice.SigSize];
        Ac1 = new byte[TargetDevice.Ac1Size];
        Pt = new byte[TargetDevice.PtSize];
    }

    /// <summary>
    /// Every fuse flattened into one vector in JEDEC address order
    /// (Logic, XOR, Sig, AC1, PT, SYN, AC0). Length == Device.FuseCount.
    /// </summary>
    public byte[] ToLinear()
    {
        var all = new byte[Device.FuseCount];
        int at = 0;
        Array.Copy(Logic, 0, all, at, Logic.Length); at += Logic.Length;
        Array.Copy(Xor, 0, all, at, Xor.Length); at += Xor.Length;
        Array.Copy(Sig, 0, all, at, Sig.Length); at += Sig.Length;
        Array.Copy(Ac1, 0, all, at, Ac1.Length); at += Ac1.Length;
        Array.Copy(Pt, 0, all, at, Pt.Length); at += Pt.Length;
        all[at++] = Syn;
        all[at++] = Ac0;
        return all;
    }
}