namespace DatabentoDotNet.Live;

/// <summary>
/// What the gateway should do when the client stops keeping up with real time.
/// </summary>
/// <remarks>
/// Port of upstream's <c>SlowReaderBehavior</c> (<c>live.rs:36-42</c>). Opt-in: when the client
/// sends no <c>slow_reader_behavior=</c> at all, the gateway applies its own default, which is
/// why <see cref="LiveClient.SlowReaderBehavior"/> is nullable rather than defaulted here.
/// </remarks>
public enum SlowReaderBehavior
{
    /// <summary>Warn, and keep sending every record. The client falls further behind.</summary>
    Warn = 0,

    /// <summary>Drop records to bring the client back to real time. Data is lost, by design.</summary>
    Skip = 1,
}

/// <summary>Wire-string conversions for <see cref="SlowReaderBehavior"/>.</summary>
/// <remarks>
/// Separate from the codec's <c>WireStrings</c> because this enum is a live-session parameter and
/// never appears in DBN. Same shape and the same rule: no aliases, and what
/// <see cref="ToWireString"/> emits is what <see cref="TryParse"/> accepts.
/// </remarks>
public static class SlowReaderBehaviorWireStrings
{
    /// <summary>Returns the wire spelling of <paramref name="value"/>.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The wire string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="SlowReaderBehavior"/>.
    /// </exception>
    public static string ToWireString(this SlowReaderBehavior value) => value switch
    {
        SlowReaderBehavior.Warn => "warn",
        SlowReaderBehavior.Skip => "skip",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined SlowReaderBehavior."),
    };

    /// <summary>Tries to parse a wire string into a <see cref="SlowReaderBehavior"/>.</summary>
    /// <param name="value">The wire string.</param>
    /// <param name="result">The parsed value, or the default when parsing fails.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> was recognised.</returns>
    public static bool TryParse(string? value, out SlowReaderBehavior result)
    {
        switch (value)
        {
            case "warn": result = SlowReaderBehavior.Warn; return true;
            case "skip": result = SlowReaderBehavior.Skip; return true;
            default: result = default; return false;
        }
    }
}
