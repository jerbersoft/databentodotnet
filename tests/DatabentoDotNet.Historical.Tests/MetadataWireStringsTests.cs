using DatabentoDotNet.Historical;
using Xunit;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The wire spellings for <see cref="FeedMode"/> and <see cref="DatasetCondition"/>.
/// </summary>
/// <remarks>
/// These two enums are the only ones in the library whose wire strings are established by nothing
/// but a hand-written match arm upstream — <c>metadata.rs:370-392</c> and <c>:405-433</c>. There is
/// no serde attribute to read them off, so a typo here would be invisible until a real response
/// arrived. Hence one assertion per variant rather than a round-trip loop.
/// </remarks>
public sealed class MetadataWireStringsTests
{
    [Fact]
    public void FeedMode_RendersTheThreeSpellingsUpstreamMatchesOn()
    {
        Assert.Equal("historical", FeedMode.Historical.ToWireString());
        Assert.Equal("historical-streaming", FeedMode.HistoricalStreaming.ToWireString());
        Assert.Equal("live", FeedMode.Live.ToWireString());
    }

    [Fact]
    public void DatasetCondition_RendersTheFourSpellingsUpstreamMatchesOn()
    {
        Assert.Equal("available", DatasetCondition.Available.ToWireString());
        Assert.Equal("degraded", DatasetCondition.Degraded.ToWireString());
        Assert.Equal("pending", DatasetCondition.Pending.ToWireString());
        Assert.Equal("missing", DatasetCondition.Missing.ToWireString());
    }

    [Fact]
    public void TryParseFeedMode_RoundTripsEveryVariant()
    {
        foreach (var expected in new[] { FeedMode.Historical, FeedMode.HistoricalStreaming, FeedMode.Live })
        {
            Assert.True(MetadataWireStrings.TryParseFeedMode(expected.ToWireString(), out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TryParseDatasetCondition_RoundTripsEveryVariant()
    {
        foreach (var expected in new[]
                 {
                     DatasetCondition.Available, DatasetCondition.Degraded,
                     DatasetCondition.Pending, DatasetCondition.Missing,
                 })
        {
            Assert.True(MetadataWireStrings.TryParseDatasetCondition(expected.ToWireString(), out var actual));
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// The codec's contract, matched here: <c>TryParse*</c> answers <see langword="false"/> and
    /// never throws, so an unrecognised value is the caller's decision rather than an exception
    /// from inside a lookup.
    /// </summary>
    [Fact]
    public void TryParse_AnswersFalseForAnUnknownValue_AndForNull()
    {
        Assert.False(MetadataWireStrings.TryParseFeedMode("historical_streaming", out var mode));
        Assert.Equal(default, mode);
        Assert.False(MetadataWireStrings.TryParseFeedMode(null, out _));
        Assert.False(MetadataWireStrings.TryParseDatasetCondition("AVAILABLE", out var condition));
        Assert.Equal(default, condition);
        Assert.False(MetadataWireStrings.TryParseDatasetCondition(null, out _));
    }

    /// <summary>
    /// Also the codec's contract: <c>ToWireString</c> throws rather than inventing a spelling for a
    /// value that was cast in from outside the defined set.
    /// </summary>
    [Fact]
    public void ToWireString_ThrowsForAnUndefinedValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((FeedMode)99).ToWireString());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((DatasetCondition)99).ToWireString());
    }
}
