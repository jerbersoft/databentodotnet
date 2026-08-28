namespace DatabentoDotNet.Historical;

/// <summary>
/// Wire-string conversions for the two enums the <c>metadata.*</c> responses carry.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shaped like <see cref="DatabentoDotNet.Dbn.WireStrings"/> and holding to the
/// same contract: <c>ToWireString</c> throws <see cref="ArgumentOutOfRangeException"/> for a value
/// outside the defined set, and every <c>TryParse</c> answers <see langword="false"/> without
/// throwing. One method per enum rather than an overload set, for the reason that file gives — an
/// overload would make the ordinary <c>out var</c> call form ambiguous and fail to compile.
/// </para>
/// <para>
/// These two enums live here rather than in the codec because nothing on the wire carries them:
/// they exist only in <c>metadata.*</c> JSON response bodies. Upstream establishes both spellings
/// in a hand-written <c>FromStr</c> with no serde attribute anywhere
/// (<c>metadata.rs:378-391</c> for <c>FeedMode</c>, <c>:418-432</c> for <c>DatasetCondition</c>),
/// so these tables are the only written record of them in this codebase.
/// </para>
/// </remarks>
public static class MetadataWireStrings
{
    /// <summary>Returns the wire string for <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="FeedMode"/>.
    /// </exception>
    public static string ToWireString(this FeedMode value) => value switch
    {
        FeedMode.Historical => "historical",
        FeedMode.HistoricalStreaming => "historical-streaming",
        FeedMode.Live => "live",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>Parses a <see cref="FeedMode"/> wire string.</summary>
    /// <returns><see langword="true"/> if <paramref name="value"/> named a defined mode.</returns>
    public static bool TryParseFeedMode(string? value, out FeedMode result)
    {
        switch (value)
        {
            case "historical": result = FeedMode.Historical; return true;
            case "historical-streaming": result = FeedMode.HistoricalStreaming; return true;
            case "live": result = FeedMode.Live; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Returns the wire string for <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="DatasetCondition"/>.
    /// </exception>
    public static string ToWireString(this DatasetCondition value) => value switch
    {
        DatasetCondition.Available => "available",
        DatasetCondition.Degraded => "degraded",
        DatasetCondition.Pending => "pending",
        DatasetCondition.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>Parses a <see cref="DatasetCondition"/> wire string.</summary>
    /// <returns>
    /// <see langword="true"/> if <paramref name="value"/> named a defined condition.
    /// </returns>
    public static bool TryParseDatasetCondition(string? value, out DatasetCondition result)
    {
        switch (value)
        {
            case "available": result = DatasetCondition.Available; return true;
            case "degraded": result = DatasetCondition.Degraded; return true;
            case "pending": result = DatasetCondition.Pending; return true;
            case "missing": result = DatasetCondition.Missing; return true;
            default: result = default; return false;
        }
    }
}
