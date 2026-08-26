namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Round-trips the char-valued enums that have <b>no</b> string wire form upstream
/// (<see cref="Side"/>, <see cref="Action"/>, <see cref="InstrumentClass"/>,
/// <see cref="MatchAlgorithm"/>, <see cref="UserDefinedInstrument"/>,
/// <see cref="SecurityUpdateAction"/>, <see cref="TriState"/>) through their raw ASCII byte —
/// the only wire representation these seven types have.
/// </summary>
public class EnumCharTests
{
    [Theory]
    [InlineData(Side.Ask, 'A')]
    [InlineData(Side.Bid, 'B')]
    [InlineData(Side.None, 'N')]
    public void Side_Char_RoundTrips(Side value, char expected)
    {
        Assert.Equal(expected, value.ToChar());
        Assert.Equal(value, (Side)(byte)expected);
    }

    [Theory]
    [InlineData(Action.Modify, 'M')]
    [InlineData(Action.Trade, 'T')]
    [InlineData(Action.Fill, 'F')]
    [InlineData(Action.Cancel, 'C')]
    [InlineData(Action.Add, 'A')]
    [InlineData(Action.Clear, 'R')]
    [InlineData(Action.None, 'N')]
    public void Action_Char_RoundTrips(Action value, char expected)
    {
        Assert.Equal(expected, value.ToChar());
        Assert.Equal(value, (Action)(byte)expected);
    }

    [Theory]
    [InlineData(InstrumentClass.Bond, 'B')]
    [InlineData(InstrumentClass.Call, 'C')]
    [InlineData(InstrumentClass.Future, 'F')]
    [InlineData(InstrumentClass.Index, 'I')]
    [InlineData(InstrumentClass.Stock, 'K')]
    [InlineData(InstrumentClass.MixedSpread, 'M')]
    [InlineData(InstrumentClass.Put, 'P')]
    [InlineData(InstrumentClass.FutureSpread, 'S')]
    [InlineData(InstrumentClass.OptionSpread, 'T')]
    [InlineData(InstrumentClass.FxSpot, 'X')]
    [InlineData(InstrumentClass.CommoditySpot, 'Y')]
    public void InstrumentClass_Char_RoundTrips(InstrumentClass value, char expected)
    {
        Assert.Equal(expected, value.ToChar());
        Assert.Equal(value, (InstrumentClass)(byte)expected);
    }

    [Theory]
    [InlineData(MatchAlgorithm.Undefined, ' ')]
    [InlineData(MatchAlgorithm.Fifo, 'F')]
    [InlineData(MatchAlgorithm.Configurable, 'K')]
    [InlineData(MatchAlgorithm.ProRata, 'C')]
    [InlineData(MatchAlgorithm.FifoLmm, 'T')]
    [InlineData(MatchAlgorithm.ThresholdProRata, 'O')]
    [InlineData(MatchAlgorithm.FifoTopLmm, 'S')]
    [InlineData(MatchAlgorithm.ThresholdProRataLmm, 'Q')]
    [InlineData(MatchAlgorithm.EurodollarFutures, 'Y')]
    [InlineData(MatchAlgorithm.TimeProRata, 'P')]
    [InlineData(MatchAlgorithm.InstitutionalPrioritization, 'V')]
    [InlineData(MatchAlgorithm.Allocation, 'A')]
    public void MatchAlgorithm_Char_RoundTrips(MatchAlgorithm value, char expected)
    {
        Assert.Equal(expected, value.ToChar());
        Assert.Equal(value, (MatchAlgorithm)(byte)expected);
    }

    [Theory]
    [InlineData(UserDefinedInstrument.No, 'N')]
    [InlineData(UserDefinedInstrument.Yes, 'Y')]
    public void UserDefinedInstrument_Char_RoundTrips(UserDefinedInstrument value, char expected)
    {
        Assert.Equal(expected, value.ToChar());
        Assert.Equal(value, (UserDefinedInstrument)(byte)expected);
    }

    [Theory]
    [InlineData(SecurityUpdateAction.Add, 'A')]
    [InlineData(SecurityUpdateAction.Modify, 'M')]
    [InlineData(SecurityUpdateAction.Delete, 'D')]
    [InlineData(SecurityUpdateAction.Invalid, '~')]
    public void SecurityUpdateAction_Char_RoundTrips(SecurityUpdateAction value, char expected)
    {
        Assert.Equal(expected, value.ToChar());
        Assert.Equal(value, (SecurityUpdateAction)(byte)expected);
    }

    [Theory]
    [InlineData(TriState.NotAvailable, '~')]
    [InlineData(TriState.No, 'N')]
    [InlineData(TriState.Yes, 'Y')]
    public void TriState_Char_RoundTrips(TriState value, char expected)
    {
        Assert.Equal(expected, value.ToChar());
        Assert.Equal(value, (TriState)(byte)expected);
    }
}
