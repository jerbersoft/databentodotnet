namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="SlowReaderBehavior"/>'s wire strings.
/// </summary>
/// <remarks>
/// The value goes onto the auth line verbatim, so a wrong spelling is a rejected session rather
/// than a compile error. Asserted over every enum value, with a sweep that fails if a new one is
/// added without a spelling.
/// </remarks>
public class SlowReaderBehaviorTests
{
    [Theory]
    [InlineData(SlowReaderBehavior.Warn, "warn")]
    [InlineData(SlowReaderBehavior.Skip, "skip")]
    public void ToWireString_MatchesUpstreamsDisplayFormatting(SlowReaderBehavior value, string expected)
    {
        Assert.Equal(expected, value.ToWireString());
    }

    [Fact]
    public void EveryValue_HasAWireStringThatRoundTrips()
    {
        foreach (var value in Enum.GetValues<SlowReaderBehavior>())
        {
            Assert.True(SlowReaderBehaviorWireStrings.TryParse(value.ToWireString(), out var parsed));
            Assert.Equal(value, parsed);
        }
    }

    [Fact]
    public void ToWireString_AnUndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((SlowReaderBehavior)99).ToWireString());
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("Skip")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_AnythingButTheExactWireSpelling_IsFalse(string? value)
    {
        Assert.False(SlowReaderBehaviorWireStrings.TryParse(value, out _));
    }
}
