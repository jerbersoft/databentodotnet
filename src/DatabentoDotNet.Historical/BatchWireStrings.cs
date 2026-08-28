namespace DatabentoDotNet.Historical;

/// <summary>
/// Wire-string conversions for the three enums the <c>batch.*</c> endpoints carry.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like <see cref="MetadataWireStrings"/> and holding to the same contract, which is
/// <see cref="DatabentoDotNet.Dbn.WireStrings"/>': <c>ToWireString</c> throws
/// <see cref="ArgumentOutOfRangeException"/> for a value outside the defined set, and every
/// <c>TryParse</c> answers <see langword="false"/> without throwing. One method per enum rather
/// than an overload set, so the ordinary <c>out var</c> call form stays unambiguous.
/// </para>
/// <para>
/// Upstream establishes each spelling by hand in an <c>as_str</c> / <c>FromStr</c> pair with no
/// serde attribute anywhere — <c>batch.rs:632-680</c> for <see cref="SplitDuration"/>,
/// <c>:683-720</c> for <see cref="Delivery"/>, <c>:722-760</c> for <see cref="JobState"/> — so
/// these tables are the only written record of them in this codebase.
/// </para>
/// <para>
/// <b><see cref="TryParseJobState"/> knows three spellings upstream's <c>FromStr</c> does not.</b>
/// See <see cref="JobState"/> for what the API answered when #39 asked, and for why the missing
/// ones are a defect rather than a difference of taste.
/// </para>
/// </remarks>
public static class BatchWireStrings
{
    /// <summary>Returns the wire string for <paramref name="value"/>.</summary>
    /// <remarks>
    /// <see cref="SplitDuration.None"/> renders as <c>none</c>, which is what a <em>request</em>
    /// carries. A <em>response</em> spells the same thing as JSON <c>null</c>; see
    /// <see cref="TryParseSplitDuration"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="SplitDuration"/>.
    /// </exception>
    public static string ToWireString(this SplitDuration value) => value switch
    {
        SplitDuration.Day => "day",
        SplitDuration.Week => "week",
        SplitDuration.Month => "month",
        SplitDuration.Year => "year",
        SplitDuration.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>Parses a <see cref="SplitDuration"/> wire string.</summary>
    /// <remarks>
    /// A <see langword="null"/> <paramref name="value"/> is <b>not</b> handled here, and that is
    /// deliberate: this method answers <see langword="false"/> for it like any other unrecognised
    /// spelling. The API's <c>null</c>-means-<see cref="SplitDuration.None"/> rule belongs to the
    /// JSON layer, where a JSON <c>null</c> token is distinguishable from the four-character string
    /// <c>"null"</c> — see <c>Json.SplitDurationJsonConverter</c>. Folding it in here would make
    /// <c>TryParseSplitDuration("null", out _)</c> succeed, which no wire value ever should.
    /// </remarks>
    /// <returns><see langword="true"/> if <paramref name="value"/> named a defined duration.</returns>
    public static bool TryParseSplitDuration(string? value, out SplitDuration result)
    {
        switch (value)
        {
            case "day": result = SplitDuration.Day; return true;
            case "week": result = SplitDuration.Week; return true;
            case "month": result = SplitDuration.Month; return true;
            case "year": result = SplitDuration.Year; return true;
            case "none": result = SplitDuration.None; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Returns the wire string for <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="Delivery"/>.
    /// </exception>
    public static string ToWireString(this Delivery value) => value switch
    {
        Delivery.Download => "download",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>Parses a <see cref="Delivery"/> wire string.</summary>
    /// <returns><see langword="true"/> if <paramref name="value"/> named a defined mechanism.</returns>
    public static bool TryParseDelivery(string? value, out Delivery result)
    {
        switch (value)
        {
            case "download": result = Delivery.Download; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Returns the wire string for <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="JobState"/>.
    /// </exception>
    public static string ToWireString(this JobState value) => value switch
    {
        JobState.Received => "received",
        JobState.Queued => "queued",
        JobState.Processing => "processing",
        JobState.Finalizing => "finalizing",
        JobState.Done => "done",
        JobState.Expired => "expired",
        JobState.Purged => "purged",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary>Parses a <see cref="JobState"/> wire string.</summary>
    /// <returns><see langword="true"/> if <paramref name="value"/> named a defined state.</returns>
    public static bool TryParseJobState(string? value, out JobState result)
    {
        switch (value)
        {
            case "received": result = JobState.Received; return true;
            case "queued": result = JobState.Queued; return true;
            case "processing": result = JobState.Processing; return true;
            case "finalizing": result = JobState.Finalizing; return true;
            case "done": result = JobState.Done; return true;
            case "expired": result = JobState.Expired; return true;
            case "purged": result = JobState.Purged; return true;
            default: result = default; return false;
        }
    }
}
