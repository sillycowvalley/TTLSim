using TTLSim.Core;

public class JedecFuseMapTests
{
    // A well-formed JEDEC body: design spec, then '*'-delimited fields.
    // 24 fuses, default 0, set fuses 0,1 and 20.
    private const string Body =
        "demo header*" +
        "QP20*" +
        "QF24*" +
        "F0*" +
        "L0000 11000000000000000000*" +
        "L0020 1000*";

    [Fact]
    public void Parses_counts_and_fuses()
    {
        JedecData j = JedecFuseMap.Parse(Body);
        Assert.Equal(24, j.FuseCount);
        Assert.Equal(20, j.PinCount);
        Assert.True(j.Fuses[0]);
        Assert.True(j.Fuses[1]);
        Assert.False(j.Fuses[2]);
        Assert.True(j.Fuses[20]);
        Assert.False(j.Fuses[21]);
    }

    [Fact]
    public void Default_fuse_one_fills_array()
    {
        JedecData j = JedecFuseMap.Parse("d*QF8*F1*");
        Assert.Equal(8, j.FuseCount);
        Assert.All(j.Fuses, b => Assert.True(b));
    }

    [Fact]
    public void Valid_checksum_accepted()
    {
        bool[] f = new bool[24];
        f[0] = f[1] = true; f[20] = true;
        int cks = JedecFuseMap.FuseChecksum(f);
        JedecData j = JedecFuseMap.Parse(Body + $"C{cks:X4}*");
        Assert.True(j.Fuses[20]);
    }

    [Fact]
    public void Wrong_checksum_throws()
    {
        Assert.Throws<FormatException>(() => JedecFuseMap.Parse(Body + "C9999*"));
    }

    [Fact]
    public void Missing_fuse_count_throws()
    {
        Assert.Throws<FormatException>(() => JedecFuseMap.Parse("header*F0*"));
    }

    [Fact]
    public void Overrun_throws()
    {
        Assert.Throws<FormatException>(() => JedecFuseMap.Parse("d*QF4*F0*L0000 11111111*"));
    }

    [Fact]
    public void Stx_etx_frame_tolerated()
    {
        string framed = "\x02" + "d*QF8*F0*L0000 1*" + "\x03" + "ABCD";
        JedecData j = JedecFuseMap.Parse(framed);
        Assert.Equal(8, j.FuseCount);
        Assert.True(j.Fuses[0]);
    }

    [Fact]
    public void WinCupl_design_header_is_ignored()
    {
        // WinCUPL frames with STX, then a design specification whose first line
        // begins "CUPL(WM)". Earlier this header was dispatched as a field and
        // its leading 'C' was misread as the fuse-checksum field. The design
        // spec (everything before the first '*') must be skipped.
        string winCupl =
            "\x02\r\n" +
            "CUPL(WM)        5.0a  Serial# 60008009\r\n" +
            "Device          g20v8as  Library DLIB-h-40-1\r\n" +
            "Name            NEXTPC4 \r\n" +
            "Location        U_DEC4 \r\n" +
            "*QP24 \r\n" +
            "*QF24 \r\n" +
            "*F0 \r\n" +
            "*L00000 11000000000000000000 \r\n" +
            "*\r\n" + "\x03" + "693d\r\n";

        JedecData j = JedecFuseMap.Parse(winCupl);
        Assert.Equal(24, j.FuseCount);
        Assert.Equal(24, j.PinCount);
        Assert.True(j.Fuses[0]);
        Assert.True(j.Fuses[1]);
        Assert.False(j.Fuses[2]);
    }
}