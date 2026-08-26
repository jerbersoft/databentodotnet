namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Verifies the <see cref="FlagSet"/> bit masks against upstream's <c>flags.rs</c> constants.
/// </summary>
public class FlagSetTests
{
    [Fact]
    public void FlagSet_Last_IsBit7()
    {
        Assert.Equal((byte)0x80, (byte)FlagSet.Last);
    }

    [Fact]
    public void FlagSet_Tob_IsBit6()
    {
        Assert.Equal((byte)0x40, (byte)FlagSet.Tob);
    }

    [Fact]
    public void FlagSet_Snapshot_IsBit5()
    {
        Assert.Equal((byte)0x20, (byte)FlagSet.Snapshot);
    }

    [Fact]
    public void FlagSet_Mbp_IsBit4()
    {
        Assert.Equal((byte)0x10, (byte)FlagSet.Mbp);
    }

    [Fact]
    public void FlagSet_BadTsRecv_IsBit3()
    {
        Assert.Equal((byte)0x08, (byte)FlagSet.BadTsRecv);
    }

    [Fact]
    public void FlagSet_MaybeBadBook_IsBit2()
    {
        Assert.Equal((byte)0x04, (byte)FlagSet.MaybeBadBook);
    }

    [Fact]
    public void FlagSet_PublisherSpecific_IsBit1()
    {
        Assert.Equal((byte)0x02, (byte)FlagSet.PublisherSpecific);
    }

    [Fact]
    public void FlagSet_None_IsZero()
    {
        Assert.Equal((byte)0x00, (byte)FlagSet.None);
    }

    [Fact]
    public void FlagSet_CombinesViaBitwiseOr()
    {
        var combined = FlagSet.Tob | FlagSet.Snapshot | FlagSet.MaybeBadBook;
        Assert.Equal((byte)0x64, (byte)combined);
        Assert.True(combined.HasFlag(FlagSet.Tob));
        Assert.True(combined.HasFlag(FlagSet.Snapshot));
        Assert.True(combined.HasFlag(FlagSet.MaybeBadBook));
        Assert.False(combined.HasFlag(FlagSet.Last));
    }

    [Fact]
    public void FlagSet_AllSevenNamedBits_CombineToTheUpstreamRstestValue()
    {
        // Mirrors flags.rs's own #[rstest] "reserved_set" case for raw = 255: every named flag
        // is present; only the reserved bit 0 is excluded from this combination.
        var combined = FlagSet.Last | FlagSet.Tob | FlagSet.Snapshot | FlagSet.Mbp
            | FlagSet.BadTsRecv | FlagSet.MaybeBadBook | FlagSet.PublisherSpecific;
        Assert.Equal((byte)0xFE, (byte)combined);
    }
}
