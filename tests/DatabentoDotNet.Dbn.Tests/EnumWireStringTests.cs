namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Round-trips every enum value that has a DBN wire <c>string</c> form (<see cref="RType"/>,
/// <see cref="SType"/>, <see cref="Schema"/>, <see cref="Encoding"/>, <see cref="Compression"/>,
/// <see cref="ErrorCode"/>, <see cref="SystemCode"/>) through <see cref="WireStrings"/>, and
/// asserts the strings are byte-identical to upstream — not a mechanical
/// <c>ToString().ToLowerInvariant()</c> of the variant name.
/// </summary>
public class EnumWireStringTests
{
    // ---------------------------------------------------------------- RType

    [Theory]
    [InlineData(RType.Mbp0, "mbp-0")]
    [InlineData(RType.Mbp1, "mbp-1")]
    [InlineData(RType.Mbp10, "mbp-10")]
    [InlineData(RType.OhlcvDeprecated, "ohlcv-deprecated")]
    [InlineData(RType.Status, "status")]
    [InlineData(RType.InstrumentDef, "instrument-def")]
    [InlineData(RType.Imbalance, "imbalance")]
    [InlineData(RType.Error, "error")]
    [InlineData(RType.SymbolMapping, "symbol-mapping")]
    [InlineData(RType.System, "system")]
    [InlineData(RType.Statistics, "statistics")]
    [InlineData(RType.Ohlcv1S, "ohlcv-1s")]
    [InlineData(RType.Ohlcv1M, "ohlcv-1m")]
    [InlineData(RType.Ohlcv1H, "ohlcv-1h")]
    [InlineData(RType.Ohlcv1D, "ohlcv-1d")]
    [InlineData(RType.OhlcvEod, "ohlcv-eod")]
    [InlineData(RType.Mbo, "mbo")]
    [InlineData(RType.Cmbp1, "cmbp-1")]
    [InlineData(RType.Cbbo1S, "cbbo-1s")]
    [InlineData(RType.Cbbo1M, "cbbo-1m")]
    [InlineData(RType.Tcbbo, "tcbbo")]
    [InlineData(RType.Bbo1S, "bbo-1s")]
    [InlineData(RType.Bbo1M, "bbo-1m")]
    public void RType_WireString_RoundTrips(RType value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(WireStrings.TryParseRType(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void RType_Mbp1_WireStringIsHyphenated()
    {
        // The case a naive ToString().ToLowerInvariant() gets wrong: "mbp1", not "mbp-1".
        Assert.Equal("mbp-1", RType.Mbp1.ToWireString());
    }

    [Fact]
    public void RType_Ohlcv1S_WireStringIsHyphenated()
    {
        Assert.Equal("ohlcv-1s", RType.Ohlcv1S.ToWireString());
    }

    [Fact]
    public void RType_Cbbo1M_WireStringIsHyphenated()
    {
        Assert.Equal("cbbo-1m", RType.Cbbo1M.ToWireString());
    }

    [Fact]
    public void RType_TryParse_UnknownString_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WireStrings.TryParseRType("not-a-real-rtype", out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void RType_ToWireString_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((RType)0x05).ToWireString());
    }

    // ---------------------------------------------------------------- SType

    [Theory]
    [InlineData(SType.InstrumentId, "instrument_id")]
    [InlineData(SType.RawSymbol, "raw_symbol")]
    [InlineData(SType.Smart, "smart")]
    [InlineData(SType.Continuous, "continuous")]
    [InlineData(SType.Parent, "parent")]
    [InlineData(SType.NasdaqSymbol, "nasdaq_symbol")]
    [InlineData(SType.CmsSymbol, "cms_symbol")]
    [InlineData(SType.Isin, "isin")]
    [InlineData(SType.UsCode, "us_code")]
    [InlineData(SType.BbgCompId, "bbg_comp_id")]
    [InlineData(SType.BbgCompTicker, "bbg_comp_ticker")]
    [InlineData(SType.Figi, "figi")]
    [InlineData(SType.FigiTicker, "figi_ticker")]
    [InlineData(SType.ListingId, "listing_id")]
    [InlineData(SType.IssuerId, "issuer_id")]
    [InlineData(SType.SecurityId, "security_id")]
    public void SType_WireString_RoundTrips(SType value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(WireStrings.TryParseSType(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("product_id", SType.InstrumentId)]
    [InlineData("native", SType.RawSymbol)]
    [InlineData("nasdaq", SType.NasdaqSymbol)]
    [InlineData("cms", SType.CmsSymbol)]
    public void SType_TryParse_LegacyAlias_ParsesButIsNeverEmitted(string alias, SType expected)
    {
        Assert.True(WireStrings.TryParseSType(alias, out var parsed));
        Assert.Equal(expected, parsed);
        // The alias itself is never the canonical wire string.
        Assert.NotEqual(alias, parsed.ToWireString());
    }

    [Fact]
    public void SType_TryParse_UnknownString_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WireStrings.TryParseSType("not-a-real-stype", out var result));
        Assert.Equal(default, result);
    }

    // ---------------------------------------------------------------- Schema

    [Theory]
    [InlineData(Schema.Mbo, "mbo")]
    [InlineData(Schema.Mbp1, "mbp-1")]
    [InlineData(Schema.Mbp10, "mbp-10")]
    [InlineData(Schema.Tbbo, "tbbo")]
    [InlineData(Schema.Trades, "trades")]
    [InlineData(Schema.Ohlcv1S, "ohlcv-1s")]
    [InlineData(Schema.Ohlcv1M, "ohlcv-1m")]
    [InlineData(Schema.Ohlcv1H, "ohlcv-1h")]
    [InlineData(Schema.Ohlcv1D, "ohlcv-1d")]
    [InlineData(Schema.Definition, "definition")]
    [InlineData(Schema.Statistics, "statistics")]
    [InlineData(Schema.Status, "status")]
    [InlineData(Schema.Imbalance, "imbalance")]
    [InlineData(Schema.OhlcvEod, "ohlcv-eod")]
    [InlineData(Schema.Cmbp1, "cmbp-1")]
    [InlineData(Schema.Cbbo1S, "cbbo-1s")]
    [InlineData(Schema.Cbbo1M, "cbbo-1m")]
    [InlineData(Schema.Tcbbo, "tcbbo")]
    [InlineData(Schema.Bbo1S, "bbo-1s")]
    [InlineData(Schema.Bbo1M, "bbo-1m")]
    public void Schema_WireString_RoundTrips(Schema value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(WireStrings.TryParseSchema(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void Schema_Mbp1_WireStringIsHyphenated()
    {
        // The exact case called out in the porting notes: not "mbp1".
        Assert.Equal("mbp-1", Schema.Mbp1.ToWireString());
    }

    [Fact]
    public void Schema_Ohlcv1S_WireStringIsHyphenated()
    {
        Assert.Equal("ohlcv-1s", Schema.Ohlcv1S.ToWireString());
    }

    [Fact]
    public void Schema_Cbbo1M_WireStringIsHyphenated()
    {
        Assert.Equal("cbbo-1m", Schema.Cbbo1M.ToWireString());
    }

    [Fact]
    public void Schema_TryParse_UnknownString_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WireStrings.TryParseSchema("not-a-real-schema", out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void Schema_ToWireString_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Schema)999).ToWireString());
    }

    // ------------------------------------------------------------- Encoding

    [Theory]
    [InlineData(Encoding.Dbn, "dbn")]
    [InlineData(Encoding.Csv, "csv")]
    [InlineData(Encoding.Json, "json")]
    public void Encoding_WireString_RoundTrips(Encoding value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(WireStrings.TryParseEncoding(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void Encoding_TryParse_DbzAlias_ParsesButIsNeverEmitted()
    {
        Assert.True(WireStrings.TryParseEncoding("dbz", out var parsed));
        Assert.Equal(Encoding.Dbn, parsed);
        Assert.NotEqual("dbz", parsed.ToWireString());
    }

    [Fact]
    public void Encoding_TryParse_UnknownString_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WireStrings.TryParseEncoding("not-a-real-encoding", out var result));
        Assert.Equal(default, result);
    }

    // ---------------------------------------------------------- Compression

    [Theory]
    [InlineData(Compression.None, "none")]
    [InlineData(Compression.Zstd, "zstd")]
    public void Compression_WireString_RoundTrips(Compression value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(WireStrings.TryParseCompression(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void Compression_TryParse_UnknownString_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WireStrings.TryParseCompression("not-a-real-compression", out var result));
        Assert.Equal(default, result);
    }

    // ----------------------------------------------------------- ErrorCode

    [Theory]
    [InlineData(ErrorCode.AuthFailed, "auth_failed")]
    [InlineData(ErrorCode.ApiKeyDeactivated, "api_key_deactivated")]
    [InlineData(ErrorCode.ConnectionLimitExceeded, "connection_limit_exceeded")]
    [InlineData(ErrorCode.SymbolResolutionFailed, "symbol_resolution_failed")]
    [InlineData(ErrorCode.InvalidSubscription, "invalid_subscription")]
    [InlineData(ErrorCode.InternalError, "internal_error")]
    [InlineData(ErrorCode.SkippedRecordsAfterSlowReading, "skipped_records_after_slow_reading")]
    [InlineData(ErrorCode.ReplayDataAgedOut, "replay_data_aged_out")]
    [InlineData(ErrorCode.Unset, "unset")]
    public void ErrorCode_WireString_RoundTrips(ErrorCode value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(WireStrings.TryParseErrorCode(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void ErrorCode_TryParse_UnknownString_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WireStrings.TryParseErrorCode("not-a-real-error-code", out var result));
        Assert.Equal(default, result);
    }

    // ---------------------------------------------------------- SystemCode

    [Theory]
    [InlineData(SystemCode.Heartbeat, "heartbeat")]
    [InlineData(SystemCode.SubscriptionAck, "subscription_ack")]
    [InlineData(SystemCode.SlowReaderWarning, "slow_reader_warning")]
    [InlineData(SystemCode.ReplayCompleted, "replay_completed")]
    [InlineData(SystemCode.EndOfInterval, "end_of_interval")]
    [InlineData(SystemCode.Unset, "unset")]
    public void SystemCode_WireString_RoundTrips(SystemCode value, string wire)
    {
        Assert.Equal(wire, value.ToWireString());
        Assert.True(WireStrings.TryParseSystemCode(wire, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void SystemCode_TryParse_UnknownString_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WireStrings.TryParseSystemCode("not-a-real-system-code", out var result));
        Assert.Equal(default, result);
    }

    // ------------------------------------------------------------------------------------
    // Completeness. Every round-trip above is a hand-written [InlineData] row asserting one
    // exact string, which is what makes them worth having — they prove the strings match
    // upstream rather than merely being self-consistent. What they cannot prove is that the
    // rows cover the enum: a variant added by an upstream bump and missed in
    // WireStrings.ToWireString falls through to the `_ =>` arm and throws
    // ArgumentOutOfRangeException at run time, on a value the wire says is legal, and a missed
    // TryParse* entry rejects it. No hand-written row notices, because nobody wrote one.
    //
    // These tests enumerate the declaration instead. Enum.GetValues<T>() means a new variant is
    // in the test the moment it is in the enum, and every one of them must produce a non-empty
    // string that parses straight back to itself.
    //
    // Only the seven enums with a string form appear here. Side, Action, InstrumentClass,
    // MatchAlgorithm, UserDefinedInstrument, SecurityUpdateAction and TriState have no wire
    // string by design — their wire form is the raw ASCII byte, covered by EnumCharTests — so
    // there is nothing here to assert about them.
    // ------------------------------------------------------------------------------------

    private delegate bool TryParseWireString<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum;

    [Fact]
    public void RType_EveryDeclaredVariant_HasAWireStringThatRoundTrips()
        => AssertEveryVariantRoundTrips<RType>(value => value.ToWireString(), WireStrings.TryParseRType);

    [Fact]
    public void SType_EveryDeclaredVariant_HasAWireStringThatRoundTrips()
        => AssertEveryVariantRoundTrips<SType>(value => value.ToWireString(), WireStrings.TryParseSType);

    [Fact]
    public void Schema_EveryDeclaredVariant_HasAWireStringThatRoundTrips()
        => AssertEveryVariantRoundTrips<Schema>(value => value.ToWireString(), WireStrings.TryParseSchema);

    [Fact]
    public void Encoding_EveryDeclaredVariant_HasAWireStringThatRoundTrips()
        => AssertEveryVariantRoundTrips<Encoding>(value => value.ToWireString(), WireStrings.TryParseEncoding);

    [Fact]
    public void Compression_EveryDeclaredVariant_HasAWireStringThatRoundTrips()
        => AssertEveryVariantRoundTrips<Compression>(value => value.ToWireString(), WireStrings.TryParseCompression);

    [Fact]
    public void ErrorCode_EveryDeclaredVariant_HasAWireStringThatRoundTrips()
        => AssertEveryVariantRoundTrips<ErrorCode>(value => value.ToWireString(), WireStrings.TryParseErrorCode);

    [Fact]
    public void SystemCode_EveryDeclaredVariant_HasAWireStringThatRoundTrips()
        => AssertEveryVariantRoundTrips<SystemCode>(value => value.ToWireString(), WireStrings.TryParseSystemCode);

    /// <summary>
    /// Asserts every declared <typeparamref name="TEnum"/> variant converts to a non-empty wire
    /// string, that the string parses back to the same variant, and that no two variants share a
    /// string — a shared string cannot round-trip, and is the shape a copy-paste slip in the
    /// switch takes.
    /// </summary>
    private static void AssertEveryVariantRoundTrips<TEnum>(
        Func<TEnum, string> toWireString,
        TryParseWireString<TEnum> tryParse)
        where TEnum : struct, Enum
    {
        var wires = new Dictionary<string, TEnum>(StringComparer.Ordinal);

        foreach (var value in Enum.GetValues<TEnum>())
        {
            // Not wrapped in a try: an ArgumentOutOfRangeException out of ToWireString is
            // exactly the failure this test is here to surface, and xunit reports the throwing
            // variant as clearly as an assertion would.
            var wire = toWireString(value);

            Assert.False(
                string.IsNullOrEmpty(wire),
                $"{typeof(TEnum).Name}.{value} has no wire string.");
            Assert.True(
                tryParse(wire, out var parsed),
                $"{typeof(TEnum).Name}.{value} emits \"{wire}\" but TryParse{typeof(TEnum).Name} does not accept it.");
            Assert.Equal(value, parsed);

            Assert.False(
                wires.TryGetValue(wire, out var owner),
                $"{typeof(TEnum).Name}.{value} and {typeof(TEnum).Name}.{owner} both emit \"{wire}\".");
            wires.Add(wire, value);
        }

        Assert.Equal(Enum.GetValues<TEnum>().Length, wires.Count);
    }
}
