namespace DatabentoDotNet.Dbn;

/// <summary>
/// Allocation-free, reflection-free text conversions for the DBN enums that have a wire text
/// form.
/// </summary>
/// <remarks>
/// <para>
/// Two distinct groups of enums are covered, and they are not interchangeable:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="RType"/>, <see cref="SType"/>, <see cref="Schema"/>, <see cref="Encoding"/>,
/// <see cref="Compression"/>, <see cref="ErrorCode"/>, and <see cref="SystemCode"/> have a real
/// <c>string</c> wire form in the Rust source (<c>as_str</c>/<c>FromStr</c>). Use
/// <c>ToWireString</c> and the matching <c>TryParse{Enum}</c> (e.g.
/// <see cref="TryParseRType(string?, out RType)"/>) for these — one method per enum rather than
/// a single overload distinguished only by its <see langword="out"/> parameter's type, because
/// that would make the ordinary <c>out var</c> call form ambiguous and fail to compile.
/// </description></item>
/// <item><description>
/// <see cref="Side"/>, <see cref="Action"/>, <see cref="InstrumentClass"/>,
/// <see cref="MatchAlgorithm"/>, <see cref="UserDefinedInstrument"/>,
/// <see cref="SecurityUpdateAction"/>, and <see cref="TriState"/> have <b>no</b> string wire
/// form at all in the Rust source — no <c>as_str</c>, no <c>FromStr</c>, no <c>Display</c>.
/// Their only wire/text representation is the raw ASCII byte itself (e.g.
/// <see cref="Side.Ask"/> <b>is</b> <c>b'A'</c>, not a string <c>"ask"</c>). Use <c>ToChar</c>
/// for these; do not invent strings for them.
/// </description></item>
/// </list>
/// <para>
/// None of the string conversions are a mechanical <c>ToString().ToLowerInvariant()</c> —
/// several wire strings insert a hyphen before a leading digit (<c>mbp-1</c>, <c>ohlcv-1s</c>,
/// <c>cbbo-1m</c>) that a naive case-transition splitter would miss. Every <c>switch</c> below
/// mirrors the Rust crate's <c>as_str</c>/<c>FromStr</c> match arms verbatim, including the
/// handful of legacy parse-only aliases upstream still accepts (documented per method).
/// </para>
/// <para>
/// This is the text/string half of enum conversion. For numeric wire-byte validation (the
/// equivalent of Rust's <c>TryFrom&lt;u8&gt;</c>/<c>TryFrom&lt;u16&gt;</c>), see
/// <see cref="EnumValues"/> — the two failure modes (an unrecognized string vs. an
/// out-of-range raw byte) are kept on separate call surfaces on purpose, since upstream
/// represents them as different error types.
/// </para>
/// </remarks>
public static class WireStrings
{
    // ---------------------------------------------------------------- RType

    /// <summary>Converts <paramref name="value"/> to its DBN wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="RType"/>.</exception>
    public static string ToWireString(this RType value) => value switch
    {
        RType.Mbp0 => "mbp-0",
        RType.Mbp1 => "mbp-1",
        RType.Mbp10 => "mbp-10",
        RType.OhlcvDeprecated => "ohlcv-deprecated",
        RType.Status => "status",
        RType.InstrumentDef => "instrument-def",
        RType.Imbalance => "imbalance",
        RType.Error => "error",
        RType.SymbolMapping => "symbol-mapping",
        RType.System => "system",
        RType.Statistics => "statistics",
        RType.Ohlcv1S => "ohlcv-1s",
        RType.Ohlcv1M => "ohlcv-1m",
        RType.Ohlcv1H => "ohlcv-1h",
        RType.Ohlcv1D => "ohlcv-1d",
        RType.OhlcvEod => "ohlcv-eod",
        RType.Mbo => "mbo",
        RType.Cmbp1 => "cmbp-1",
        RType.Cbbo1S => "cbbo-1s",
        RType.Cbbo1M => "cbbo-1m",
        RType.Tcbbo => "tcbbo",
        RType.Bbo1S => "bbo-1s",
        RType.Bbo1M => "bbo-1m",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined RType."),
    };

    /// <summary>Tries to parse a DBN wire string into an <see cref="RType"/>. No aliases.</summary>
    public static bool TryParseRType(string? value, out RType result)
    {
        switch (value)
        {
            case "mbp-0": result = RType.Mbp0; return true;
            case "mbp-1": result = RType.Mbp1; return true;
            case "mbp-10": result = RType.Mbp10; return true;
            case "ohlcv-deprecated": result = RType.OhlcvDeprecated; return true;
            case "status": result = RType.Status; return true;
            case "instrument-def": result = RType.InstrumentDef; return true;
            case "imbalance": result = RType.Imbalance; return true;
            case "error": result = RType.Error; return true;
            case "symbol-mapping": result = RType.SymbolMapping; return true;
            case "system": result = RType.System; return true;
            case "statistics": result = RType.Statistics; return true;
            case "ohlcv-1s": result = RType.Ohlcv1S; return true;
            case "ohlcv-1m": result = RType.Ohlcv1M; return true;
            case "ohlcv-1h": result = RType.Ohlcv1H; return true;
            case "ohlcv-1d": result = RType.Ohlcv1D; return true;
            case "ohlcv-eod": result = RType.OhlcvEod; return true;
            case "mbo": result = RType.Mbo; return true;
            case "cmbp-1": result = RType.Cmbp1; return true;
            case "cbbo-1s": result = RType.Cbbo1S; return true;
            case "cbbo-1m": result = RType.Cbbo1M; return true;
            case "tcbbo": result = RType.Tcbbo; return true;
            case "bbo-1s": result = RType.Bbo1S; return true;
            case "bbo-1m": result = RType.Bbo1M; return true;
            default: result = default; return false;
        }
    }

    // ---------------------------------------------------------------- SType

    /// <summary>Converts <paramref name="value"/> to its DBN wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="SType"/>.</exception>
    public static string ToWireString(this SType value) => value switch
    {
        SType.InstrumentId => "instrument_id",
        SType.RawSymbol => "raw_symbol",
        SType.Smart => "smart",
        SType.Continuous => "continuous",
        SType.Parent => "parent",
        SType.NasdaqSymbol => "nasdaq_symbol",
        SType.CmsSymbol => "cms_symbol",
        SType.Isin => "isin",
        SType.UsCode => "us_code",
        SType.BbgCompId => "bbg_comp_id",
        SType.BbgCompTicker => "bbg_comp_ticker",
        SType.Figi => "figi",
        SType.FigiTicker => "figi_ticker",
        SType.ListingId => "listing_id",
        SType.IssuerId => "issuer_id",
        SType.SecurityId => "security_id",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined SType."),
    };

    /// <summary>
    /// Tries to parse a DBN wire string into an <see cref="SType"/>. Accepts four parse-only
    /// legacy aliases that are never emitted by <see cref="ToWireString(SType)"/>:
    /// <c>product_id</c> (&#8594; <see cref="SType.InstrumentId"/>), <c>native</c> (&#8594;
    /// <see cref="SType.RawSymbol"/>), <c>nasdaq</c> (&#8594; <see cref="SType.NasdaqSymbol"/>),
    /// and <c>cms</c> (&#8594; <see cref="SType.CmsSymbol"/>).
    /// </summary>
    public static bool TryParseSType(string? value, out SType result)
    {
        switch (value)
        {
            case "instrument_id" or "product_id": result = SType.InstrumentId; return true;
            case "raw_symbol" or "native": result = SType.RawSymbol; return true;
            case "smart": result = SType.Smart; return true;
            case "continuous": result = SType.Continuous; return true;
            case "parent": result = SType.Parent; return true;
            case "nasdaq_symbol" or "nasdaq": result = SType.NasdaqSymbol; return true;
            case "cms_symbol" or "cms": result = SType.CmsSymbol; return true;
            case "isin": result = SType.Isin; return true;
            case "us_code": result = SType.UsCode; return true;
            case "bbg_comp_id": result = SType.BbgCompId; return true;
            case "bbg_comp_ticker": result = SType.BbgCompTicker; return true;
            case "figi": result = SType.Figi; return true;
            case "figi_ticker": result = SType.FigiTicker; return true;
            case "listing_id": result = SType.ListingId; return true;
            case "issuer_id": result = SType.IssuerId; return true;
            case "security_id": result = SType.SecurityId; return true;
            default: result = default; return false;
        }
    }

    // ---------------------------------------------------------------- Schema

    /// <summary>Converts <paramref name="value"/> to its DBN wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="Schema"/>.</exception>
    public static string ToWireString(this Schema value) => value switch
    {
        Schema.Mbo => "mbo",
        Schema.Mbp1 => "mbp-1",
        Schema.Mbp10 => "mbp-10",
        Schema.Tbbo => "tbbo",
        Schema.Trades => "trades",
        Schema.Ohlcv1S => "ohlcv-1s",
        Schema.Ohlcv1M => "ohlcv-1m",
        Schema.Ohlcv1H => "ohlcv-1h",
        Schema.Ohlcv1D => "ohlcv-1d",
        Schema.Definition => "definition",
        Schema.Statistics => "statistics",
        Schema.Status => "status",
        Schema.Imbalance => "imbalance",
        Schema.OhlcvEod => "ohlcv-eod",
        Schema.Cmbp1 => "cmbp-1",
        Schema.Cbbo1S => "cbbo-1s",
        Schema.Cbbo1M => "cbbo-1m",
        Schema.Tcbbo => "tcbbo",
        Schema.Bbo1S => "bbo-1s",
        Schema.Bbo1M => "bbo-1m",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined Schema."),
    };

    /// <summary>Tries to parse a DBN wire string into a <see cref="Schema"/>. No aliases.</summary>
    public static bool TryParseSchema(string? value, out Schema result)
    {
        switch (value)
        {
            case "mbo": result = Schema.Mbo; return true;
            case "mbp-1": result = Schema.Mbp1; return true;
            case "mbp-10": result = Schema.Mbp10; return true;
            case "tbbo": result = Schema.Tbbo; return true;
            case "trades": result = Schema.Trades; return true;
            case "ohlcv-1s": result = Schema.Ohlcv1S; return true;
            case "ohlcv-1m": result = Schema.Ohlcv1M; return true;
            case "ohlcv-1h": result = Schema.Ohlcv1H; return true;
            case "ohlcv-1d": result = Schema.Ohlcv1D; return true;
            case "definition": result = Schema.Definition; return true;
            case "statistics": result = Schema.Statistics; return true;
            case "status": result = Schema.Status; return true;
            case "imbalance": result = Schema.Imbalance; return true;
            case "ohlcv-eod": result = Schema.OhlcvEod; return true;
            case "cmbp-1": result = Schema.Cmbp1; return true;
            case "cbbo-1s": result = Schema.Cbbo1S; return true;
            case "cbbo-1m": result = Schema.Cbbo1M; return true;
            case "tcbbo": result = Schema.Tcbbo; return true;
            case "bbo-1s": result = Schema.Bbo1S; return true;
            case "bbo-1m": result = Schema.Bbo1M; return true;
            default: result = default; return false;
        }
    }

    // ------------------------------------------------------------- Encoding

    /// <summary>Converts <paramref name="value"/> to its DBN wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="Encoding"/>.</exception>
    public static string ToWireString(this Encoding value) => value switch
    {
        Encoding.Dbn => "dbn",
        Encoding.Csv => "csv",
        Encoding.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined Encoding."),
    };

    /// <summary>
    /// Tries to parse a DBN wire string into an <see cref="Encoding"/>. Accepts one parse-only
    /// legacy alias that is never emitted by <see cref="ToWireString(Encoding)"/>: <c>dbz</c>
    /// (the pre-rename file extension, &#8594; <see cref="Encoding.Dbn"/>).
    /// </summary>
    public static bool TryParseEncoding(string? value, out Encoding result)
    {
        switch (value)
        {
            case "dbn" or "dbz": result = Encoding.Dbn; return true;
            case "csv": result = Encoding.Csv; return true;
            case "json": result = Encoding.Json; return true;
            default: result = default; return false;
        }
    }

    // ---------------------------------------------------------- Compression

    /// <summary>Converts <paramref name="value"/> to its DBN wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="Compression"/>.</exception>
    public static string ToWireString(this Compression value) => value switch
    {
        Compression.None => "none",
        Compression.Zstd => "zstd",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined Compression."),
    };

    /// <summary>Tries to parse a DBN wire string into a <see cref="Compression"/>. No aliases.</summary>
    public static bool TryParseCompression(string? value, out Compression result)
    {
        switch (value)
        {
            case "none": result = Compression.None; return true;
            case "zstd": result = Compression.Zstd; return true;
            default: result = default; return false;
        }
    }

    // ----------------------------------------------------------- ErrorCode

    /// <summary>Converts <paramref name="value"/> to its DBN wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="ErrorCode"/>.</exception>
    public static string ToWireString(this ErrorCode value) => value switch
    {
        ErrorCode.AuthFailed => "auth_failed",
        ErrorCode.ApiKeyDeactivated => "api_key_deactivated",
        ErrorCode.ConnectionLimitExceeded => "connection_limit_exceeded",
        ErrorCode.SymbolResolutionFailed => "symbol_resolution_failed",
        ErrorCode.InvalidSubscription => "invalid_subscription",
        ErrorCode.InternalError => "internal_error",
        ErrorCode.SkippedRecordsAfterSlowReading => "skipped_records_after_slow_reading",
        ErrorCode.ReplayDataAgedOut => "replay_data_aged_out",
        ErrorCode.Unset => "unset",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined ErrorCode."),
    };

    /// <summary>Tries to parse a DBN wire string into an <see cref="ErrorCode"/>. No aliases.</summary>
    public static bool TryParseErrorCode(string? value, out ErrorCode result)
    {
        switch (value)
        {
            case "auth_failed": result = ErrorCode.AuthFailed; return true;
            case "api_key_deactivated": result = ErrorCode.ApiKeyDeactivated; return true;
            case "connection_limit_exceeded": result = ErrorCode.ConnectionLimitExceeded; return true;
            case "symbol_resolution_failed": result = ErrorCode.SymbolResolutionFailed; return true;
            case "invalid_subscription": result = ErrorCode.InvalidSubscription; return true;
            case "internal_error": result = ErrorCode.InternalError; return true;
            case "skipped_records_after_slow_reading": result = ErrorCode.SkippedRecordsAfterSlowReading; return true;
            case "replay_data_aged_out": result = ErrorCode.ReplayDataAgedOut; return true;
            case "unset": result = ErrorCode.Unset; return true;
            default: result = default; return false;
        }
    }

    // ---------------------------------------------------------- SystemCode

    /// <summary>Converts <paramref name="value"/> to its DBN wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="SystemCode"/>.</exception>
    public static string ToWireString(this SystemCode value) => value switch
    {
        SystemCode.Heartbeat => "heartbeat",
        SystemCode.SubscriptionAck => "subscription_ack",
        SystemCode.SlowReaderWarning => "slow_reader_warning",
        SystemCode.ReplayCompleted => "replay_completed",
        SystemCode.EndOfInterval => "end_of_interval",
        SystemCode.Unset => "unset",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined SystemCode."),
    };

    /// <summary>Tries to parse a DBN wire string into a <see cref="SystemCode"/>. No aliases.</summary>
    public static bool TryParseSystemCode(string? value, out SystemCode result)
    {
        switch (value)
        {
            case "heartbeat": result = SystemCode.Heartbeat; return true;
            case "subscription_ack": result = SystemCode.SubscriptionAck; return true;
            case "slow_reader_warning": result = SystemCode.SlowReaderWarning; return true;
            case "replay_completed": result = SystemCode.ReplayCompleted; return true;
            case "end_of_interval": result = SystemCode.EndOfInterval; return true;
            case "unset": result = SystemCode.Unset; return true;
            default: result = default; return false;
        }
    }

    // ---------------------------------------------------- Char-valued enums
    //
    // Side, Action, InstrumentClass, MatchAlgorithm, UserDefinedInstrument,
    // SecurityUpdateAction, and TriState have NO string wire form upstream — their sole
    // wire/text representation is the raw ASCII byte. These conversions are infallible in both
    // directions relative to the enum's own byte value, so there is no TryParse counterpart:
    // every char maps straight back to the byte it came from via an explicit cast.

    /// <summary>Returns the ASCII character this <see cref="Side"/> is defined as.</summary>
    public static char ToChar(this Side value) => (char)value;

    /// <summary>Returns the ASCII character this <see cref="Action"/> is defined as.</summary>
    public static char ToChar(this Action value) => (char)value;

    /// <summary>Returns the ASCII character this <see cref="InstrumentClass"/> is defined as.</summary>
    public static char ToChar(this InstrumentClass value) => (char)value;

    /// <summary>Returns the ASCII character this <see cref="MatchAlgorithm"/> is defined as.</summary>
    public static char ToChar(this MatchAlgorithm value) => (char)value;

    /// <summary>Returns the ASCII character this <see cref="UserDefinedInstrument"/> is defined as.</summary>
    public static char ToChar(this UserDefinedInstrument value) => (char)value;

    /// <summary>Returns the ASCII character this <see cref="SecurityUpdateAction"/> is defined as.</summary>
    public static char ToChar(this SecurityUpdateAction value) => (char)value;

    /// <summary>Returns the ASCII character this <see cref="TriState"/> is defined as.</summary>
    public static char ToChar(this TriState value) => (char)value;
}
