namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="BatchWireStrings"/> — the only written record of three enums' spellings.
/// </summary>
/// <remarks>
/// <para>
/// The tables are hand-written on both sides of the port, with no attribute or derive generating
/// them, so a transposed pair is a plausible mistake that nothing else would catch: every one of
/// these values round-trips, so a rendering that swapped <c>week</c> and <c>month</c> would still
/// parse back to what it wrote.
/// </para>
/// <para>
/// <b><see cref="TryParseJobState"/>'s seven cases are the finding, not the coverage.</b> Upstream
/// knows four; the three extra ones came from asking the API, and the test that names them is what
/// stops a later "tidy-up against upstream" removing them.
/// </para>
/// </remarks>
public sealed class BatchWireStringsTests
{
    [Theory]
    [InlineData(SplitDuration.Day, "day")]
    [InlineData(SplitDuration.Week, "week")]
    [InlineData(SplitDuration.Month, "month")]
    [InlineData(SplitDuration.Year, "year")]
    [InlineData(SplitDuration.None, "none")]
    public void SplitDuration_RendersAndParsesEveryDefinedValue(SplitDuration value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(BatchWireStrings.TryParseSplitDuration(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData(Delivery.Download, "download")]
    public void Delivery_RendersAndParsesEveryDefinedValue(Delivery value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(BatchWireStrings.TryParseDelivery(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    /// <summary>
    /// All seven the API named when #39 asked it, in the order its own error message lists them.
    /// </summary>
    /// <remarks>
    /// <c>batch.list_jobs?states=bogus</c> answers <c>400</c> with "use any of ['received',
    /// 'queued', 'processing', 'finalizing', 'done', 'expired', 'purged']". Upstream's
    /// <c>JobState</c> has the four in the middle and neither end.
    /// </remarks>
    [Theory]
    [InlineData(JobState.Received, "received")]
    [InlineData(JobState.Queued, "queued")]
    [InlineData(JobState.Processing, "processing")]
    [InlineData(JobState.Finalizing, "finalizing")]
    [InlineData(JobState.Done, "done")]
    [InlineData(JobState.Expired, "expired")]
    [InlineData(JobState.Purged, "purged")]
    public void JobState_RendersAndParsesEveryStateTheApiNames(JobState value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(BatchWireStrings.TryParseJobState(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    /// <summary>
    /// The three states upstream's <c>FromStr</c> refuses, named individually so removing one from
    /// the enum fails here with a message that says which.
    /// </summary>
    [Theory]
    [InlineData("received", JobState.Received)]
    [InlineData("finalizing", JobState.Finalizing)]
    [InlineData("purged", JobState.Purged)]
    public void JobState_KnowsTheThreeStatesUpstreamDoesNot(string wire, JobState expected)
    {
        Assert.True(
            BatchWireStrings.TryParseJobState(wire, out var parsed),
            $"'{wire}' is one of the seven states the API's own 400 enumerates. Upstream's JobState "
            + "has four, and a listing containing one job in this state fails to deserialize there "
            + "in its entirety — see JobState's remarks.");

        Assert.Equal(expected, parsed);
    }

    /// <summary>
    /// Every <c>TryParse</c> answers <see langword="false"/> rather than throwing, which is the
    /// contract <see cref="DatabentoDotNet.Dbn.WireStrings"/> sets and
    /// <see cref="MetadataWireStrings"/> repeats.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Done")]
    [InlineData("DAY")]
    [InlineData("null")]
    [InlineData("bogus")]
    public void EveryTryParse_AnswersFalseForAnUnknownSpelling(string? value)
    {
        Assert.False(BatchWireStrings.TryParseSplitDuration(value, out var duration));
        Assert.Equal(default, duration);

        Assert.False(BatchWireStrings.TryParseDelivery(value, out var delivery));
        Assert.Equal(default, delivery);

        Assert.False(BatchWireStrings.TryParseJobState(value, out var state));
        Assert.Equal(default, state);
    }

    /// <summary>
    /// <c>ToWireString</c> throws for a value outside the defined set, rather than returning a
    /// spelling the API would reject.
    /// </summary>
    [Fact]
    public void EveryToWireString_ThrowsForAnUndefinedValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((SplitDuration)99).ToWireString());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Delivery)99).ToWireString());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((JobState)99).ToWireString());
    }

    /// <summary>
    /// <c>null</c> is not a split duration <em>here</em>. The API spells
    /// <see cref="SplitDuration.None"/> as a JSON <c>null</c> token, and that belongs to the
    /// converter, where a null token is distinguishable from the four-character string.
    /// </summary>
    [Fact]
    public void TryParseSplitDuration_DoesNotTreatTheStringNullAsNone()
    {
        Assert.False(BatchWireStrings.TryParseSplitDuration("null", out _));
        Assert.False(BatchWireStrings.TryParseSplitDuration(null, out _));
    }

    /// <summary>
    /// The defaults the enums declare are upstream's, which matters because a
    /// <see langword="default"/> value reaches the wire on any path that does not set one.
    /// </summary>
    [Fact]
    public void TheDefaultsMatchUpstreamsDefaults()
    {
        Assert.Equal(SplitDuration.Day, default(SplitDuration));
        Assert.Equal(Delivery.Download, default(Delivery));
    }
}
