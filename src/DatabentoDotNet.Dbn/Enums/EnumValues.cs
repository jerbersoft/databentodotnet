namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// Validates a raw wire byte/word against the discriminants a DBN enum actually defines.
/// </summary>
/// <remarks>
/// <para>
/// This is the numeric-decode half of enum conversion — the equivalent of the Rust crate's
/// <c>num_enum</c>-derived <c>TryFrom&lt;u8&gt;</c>/<c>TryFrom&lt;u16&gt;</c> impls, which every
/// enum in this namespace except <see cref="FlagSet"/> derives. It answers "is this raw value
/// one of the discriminants this enum defines", independent of any text form. Rejection here is
/// the numeric out-of-range failure mode; an unrecognized wire <em>string</em> is a distinct
/// failure handled by <see cref="WireStrings"/> — upstream keeps those as two different error
/// types, and this port keeps them as two different call surfaces on purpose.
/// </para>
/// <para>
/// Every enum here is strict: an undefined raw value is rejected the same way whether or not
/// the enum is <c>#[non_exhaustive]</c> upstream — <c>#[non_exhaustive]</c> only affects whether
/// downstream Rust code can exhaustively <c>match</c> the type, not whether an arbitrary byte is
/// a valid instance of it. <see cref="FlagSet"/> is the sole exception in this namespace: every
/// raw byte is already a valid <see cref="FlagSet"/>, so it has no entry here — use an explicit
/// cast instead.
/// </para>
/// <para>
/// Each enum gets its own <c>TryFrom{Enum}</c> method rather than one <c>TryFrom</c> overloaded
/// by the <see langword="out"/> parameter's type: overload resolution needs that type before it
/// can pick an overload, so <c>out var</c> at a call site — the ordinary way to call a
/// <c>TryXxx</c> method — cannot disambiguate and fails to compile. Per-enum names work with
/// <c>out var</c> the same way <see cref="int.TryParse(string?, out int)"/> does.
/// </para>
/// </remarks>
public static class EnumValues
{
    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="RType"/>.</summary>
    public static bool TryFromRType(byte raw, out RType value)
    {
        switch (raw)
        {
            case (byte)RType.Mbp0: value = RType.Mbp0; return true;
            case (byte)RType.Mbp1: value = RType.Mbp1; return true;
            case (byte)RType.Mbp10: value = RType.Mbp10; return true;
            case (byte)RType.OhlcvDeprecated: value = RType.OhlcvDeprecated; return true;
            case (byte)RType.Status: value = RType.Status; return true;
            case (byte)RType.InstrumentDef: value = RType.InstrumentDef; return true;
            case (byte)RType.Imbalance: value = RType.Imbalance; return true;
            case (byte)RType.Error: value = RType.Error; return true;
            case (byte)RType.SymbolMapping: value = RType.SymbolMapping; return true;
            case (byte)RType.System: value = RType.System; return true;
            case (byte)RType.Statistics: value = RType.Statistics; return true;
            case (byte)RType.Ohlcv1S: value = RType.Ohlcv1S; return true;
            case (byte)RType.Ohlcv1M: value = RType.Ohlcv1M; return true;
            case (byte)RType.Ohlcv1H: value = RType.Ohlcv1H; return true;
            case (byte)RType.Ohlcv1D: value = RType.Ohlcv1D; return true;
            case (byte)RType.OhlcvEod: value = RType.OhlcvEod; return true;
            case (byte)RType.Mbo: value = RType.Mbo; return true;
            case (byte)RType.Cmbp1: value = RType.Cmbp1; return true;
            case (byte)RType.Cbbo1S: value = RType.Cbbo1S; return true;
            case (byte)RType.Cbbo1M: value = RType.Cbbo1M; return true;
            case (byte)RType.Tcbbo: value = RType.Tcbbo; return true;
            case (byte)RType.Bbo1S: value = RType.Bbo1S; return true;
            case (byte)RType.Bbo1M: value = RType.Bbo1M; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="Side"/>.</summary>
    public static bool TryFromSide(byte raw, out Side value)
    {
        switch (raw)
        {
            case (byte)Side.Ask: value = Side.Ask; return true;
            case (byte)Side.Bid: value = Side.Bid; return true;
            case (byte)Side.None: value = Side.None; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="Action"/>.</summary>
    public static bool TryFromAction(byte raw, out Action value)
    {
        switch (raw)
        {
            case (byte)Action.Modify: value = Action.Modify; return true;
            case (byte)Action.Trade: value = Action.Trade; return true;
            case (byte)Action.Fill: value = Action.Fill; return true;
            case (byte)Action.Cancel: value = Action.Cancel; return true;
            case (byte)Action.Add: value = Action.Add; return true;
            case (byte)Action.Clear: value = Action.Clear; return true;
            case (byte)Action.None: value = Action.None; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="InstrumentClass"/>.</summary>
    public static bool TryFromInstrumentClass(byte raw, out InstrumentClass value)
    {
        switch (raw)
        {
            case (byte)InstrumentClass.Bond: value = InstrumentClass.Bond; return true;
            case (byte)InstrumentClass.Call: value = InstrumentClass.Call; return true;
            case (byte)InstrumentClass.Future: value = InstrumentClass.Future; return true;
            case (byte)InstrumentClass.Index: value = InstrumentClass.Index; return true;
            case (byte)InstrumentClass.Stock: value = InstrumentClass.Stock; return true;
            case (byte)InstrumentClass.MixedSpread: value = InstrumentClass.MixedSpread; return true;
            case (byte)InstrumentClass.Put: value = InstrumentClass.Put; return true;
            case (byte)InstrumentClass.FutureSpread: value = InstrumentClass.FutureSpread; return true;
            case (byte)InstrumentClass.OptionSpread: value = InstrumentClass.OptionSpread; return true;
            case (byte)InstrumentClass.FxSpot: value = InstrumentClass.FxSpot; return true;
            case (byte)InstrumentClass.CommoditySpot: value = InstrumentClass.CommoditySpot; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="MatchAlgorithm"/>.</summary>
    public static bool TryFromMatchAlgorithm(byte raw, out MatchAlgorithm value)
    {
        switch (raw)
        {
            case (byte)MatchAlgorithm.Undefined: value = MatchAlgorithm.Undefined; return true;
            case (byte)MatchAlgorithm.Fifo: value = MatchAlgorithm.Fifo; return true;
            case (byte)MatchAlgorithm.Configurable: value = MatchAlgorithm.Configurable; return true;
            case (byte)MatchAlgorithm.ProRata: value = MatchAlgorithm.ProRata; return true;
            case (byte)MatchAlgorithm.FifoLmm: value = MatchAlgorithm.FifoLmm; return true;
            case (byte)MatchAlgorithm.ThresholdProRata: value = MatchAlgorithm.ThresholdProRata; return true;
            case (byte)MatchAlgorithm.FifoTopLmm: value = MatchAlgorithm.FifoTopLmm; return true;
            case (byte)MatchAlgorithm.ThresholdProRataLmm: value = MatchAlgorithm.ThresholdProRataLmm; return true;
            case (byte)MatchAlgorithm.EurodollarFutures: value = MatchAlgorithm.EurodollarFutures; return true;
            case (byte)MatchAlgorithm.TimeProRata: value = MatchAlgorithm.TimeProRata; return true;
            case (byte)MatchAlgorithm.InstitutionalPrioritization: value = MatchAlgorithm.InstitutionalPrioritization; return true;
            case (byte)MatchAlgorithm.Allocation: value = MatchAlgorithm.Allocation; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="UserDefinedInstrument"/>.</summary>
    public static bool TryFromUserDefinedInstrument(byte raw, out UserDefinedInstrument value)
    {
        switch (raw)
        {
            case (byte)UserDefinedInstrument.No: value = UserDefinedInstrument.No; return true;
            case (byte)UserDefinedInstrument.Yes: value = UserDefinedInstrument.Yes; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="SecurityUpdateAction"/>.</summary>
    public static bool TryFromSecurityUpdateAction(byte raw, out SecurityUpdateAction value)
    {
        switch (raw)
        {
            case (byte)SecurityUpdateAction.Add: value = SecurityUpdateAction.Add; return true;
            case (byte)SecurityUpdateAction.Modify: value = SecurityUpdateAction.Modify; return true;
            case (byte)SecurityUpdateAction.Delete: value = SecurityUpdateAction.Delete; return true;
            case (byte)SecurityUpdateAction.Invalid: value = SecurityUpdateAction.Invalid; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="SType"/>.</summary>
    public static bool TryFromSType(byte raw, out SType value)
    {
        switch (raw)
        {
            case (byte)SType.InstrumentId: value = SType.InstrumentId; return true;
            case (byte)SType.RawSymbol: value = SType.RawSymbol; return true;
            case (byte)SType.Smart: value = SType.Smart; return true;
            case (byte)SType.Continuous: value = SType.Continuous; return true;
            case (byte)SType.Parent: value = SType.Parent; return true;
            case (byte)SType.NasdaqSymbol: value = SType.NasdaqSymbol; return true;
            case (byte)SType.CmsSymbol: value = SType.CmsSymbol; return true;
            case (byte)SType.Isin: value = SType.Isin; return true;
            case (byte)SType.UsCode: value = SType.UsCode; return true;
            case (byte)SType.BbgCompId: value = SType.BbgCompId; return true;
            case (byte)SType.BbgCompTicker: value = SType.BbgCompTicker; return true;
            case (byte)SType.Figi: value = SType.Figi; return true;
            case (byte)SType.FigiTicker: value = SType.FigiTicker; return true;
            case (byte)SType.ListingId: value = SType.ListingId; return true;
            case (byte)SType.IssuerId: value = SType.IssuerId; return true;
            case (byte)SType.SecurityId: value = SType.SecurityId; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="Schema"/>.</summary>
    public static bool TryFromSchema(ushort raw, out Schema value)
    {
        switch (raw)
        {
            case (ushort)Schema.Mbo: value = Schema.Mbo; return true;
            case (ushort)Schema.Mbp1: value = Schema.Mbp1; return true;
            case (ushort)Schema.Mbp10: value = Schema.Mbp10; return true;
            case (ushort)Schema.Tbbo: value = Schema.Tbbo; return true;
            case (ushort)Schema.Trades: value = Schema.Trades; return true;
            case (ushort)Schema.Ohlcv1S: value = Schema.Ohlcv1S; return true;
            case (ushort)Schema.Ohlcv1M: value = Schema.Ohlcv1M; return true;
            case (ushort)Schema.Ohlcv1H: value = Schema.Ohlcv1H; return true;
            case (ushort)Schema.Ohlcv1D: value = Schema.Ohlcv1D; return true;
            case (ushort)Schema.Definition: value = Schema.Definition; return true;
            case (ushort)Schema.Statistics: value = Schema.Statistics; return true;
            case (ushort)Schema.Status: value = Schema.Status; return true;
            case (ushort)Schema.Imbalance: value = Schema.Imbalance; return true;
            case (ushort)Schema.OhlcvEod: value = Schema.OhlcvEod; return true;
            case (ushort)Schema.Cmbp1: value = Schema.Cmbp1; return true;
            case (ushort)Schema.Cbbo1S: value = Schema.Cbbo1S; return true;
            case (ushort)Schema.Cbbo1M: value = Schema.Cbbo1M; return true;
            case (ushort)Schema.Tcbbo: value = Schema.Tcbbo; return true;
            case (ushort)Schema.Bbo1S: value = Schema.Bbo1S; return true;
            case (ushort)Schema.Bbo1M: value = Schema.Bbo1M; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="Encoding"/>.</summary>
    public static bool TryFromEncoding(byte raw, out Encoding value)
    {
        switch (raw)
        {
            case (byte)Encoding.Dbn: value = Encoding.Dbn; return true;
            case (byte)Encoding.Csv: value = Encoding.Csv; return true;
            case (byte)Encoding.Json: value = Encoding.Json; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="Compression"/>.</summary>
    public static bool TryFromCompression(byte raw, out Compression value)
    {
        switch (raw)
        {
            case (byte)Compression.None: value = Compression.None; return true;
            case (byte)Compression.Zstd: value = Compression.Zstd; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="StatType"/>.</summary>
    public static bool TryFromStatType(ushort raw, out StatType value)
    {
        switch (raw)
        {
            case (ushort)StatType.OpeningPrice: value = StatType.OpeningPrice; return true;
            case (ushort)StatType.IndicativeOpeningPrice: value = StatType.IndicativeOpeningPrice; return true;
            case (ushort)StatType.SettlementPrice: value = StatType.SettlementPrice; return true;
            case (ushort)StatType.TradingSessionLowPrice: value = StatType.TradingSessionLowPrice; return true;
            case (ushort)StatType.TradingSessionHighPrice: value = StatType.TradingSessionHighPrice; return true;
            case (ushort)StatType.ClearedVolume: value = StatType.ClearedVolume; return true;
            case (ushort)StatType.LowestOffer: value = StatType.LowestOffer; return true;
            case (ushort)StatType.HighestBid: value = StatType.HighestBid; return true;
            case (ushort)StatType.OpenInterest: value = StatType.OpenInterest; return true;
            case (ushort)StatType.FixingPrice: value = StatType.FixingPrice; return true;
            case (ushort)StatType.ClosePrice: value = StatType.ClosePrice; return true;
            case (ushort)StatType.NetChange: value = StatType.NetChange; return true;
            case (ushort)StatType.Vwap: value = StatType.Vwap; return true;
            case (ushort)StatType.Volatility: value = StatType.Volatility; return true;
            case (ushort)StatType.Delta: value = StatType.Delta; return true;
            case (ushort)StatType.UncrossingPrice: value = StatType.UncrossingPrice; return true;
            case (ushort)StatType.UpperPriceLimit: value = StatType.UpperPriceLimit; return true;
            case (ushort)StatType.LowerPriceLimit: value = StatType.LowerPriceLimit; return true;
            case (ushort)StatType.BlockVolume: value = StatType.BlockVolume; return true;
            case (ushort)StatType.IndicativeClosePrice: value = StatType.IndicativeClosePrice; return true;
            case (ushort)StatType.MwcbLevel1: value = StatType.MwcbLevel1; return true;
            case (ushort)StatType.MwcbLevel2: value = StatType.MwcbLevel2; return true;
            case (ushort)StatType.MwcbLevel3: value = StatType.MwcbLevel3; return true;
            case (ushort)StatType.AuctionCollarReferencePrice: value = StatType.AuctionCollarReferencePrice; return true;
            case (ushort)StatType.AuctionCollarUpperPrice: value = StatType.AuctionCollarUpperPrice; return true;
            case (ushort)StatType.AuctionCollarLowerPrice: value = StatType.AuctionCollarLowerPrice; return true;
            case (ushort)StatType.VenueSpecificVolume1: value = StatType.VenueSpecificVolume1; return true;
            case (ushort)StatType.VenueSpecificPrice1: value = StatType.VenueSpecificPrice1; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="StatUpdateAction"/>.</summary>
    public static bool TryFromStatUpdateAction(byte raw, out StatUpdateAction value)
    {
        switch (raw)
        {
            case (byte)StatUpdateAction.New: value = StatUpdateAction.New; return true;
            case (byte)StatUpdateAction.Delete: value = StatUpdateAction.Delete; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="StatusAction"/>.</summary>
    public static bool TryFromStatusAction(ushort raw, out StatusAction value)
    {
        switch (raw)
        {
            case (ushort)StatusAction.None: value = StatusAction.None; return true;
            case (ushort)StatusAction.PreOpen: value = StatusAction.PreOpen; return true;
            case (ushort)StatusAction.PreCross: value = StatusAction.PreCross; return true;
            case (ushort)StatusAction.Quoting: value = StatusAction.Quoting; return true;
            case (ushort)StatusAction.Cross: value = StatusAction.Cross; return true;
            case (ushort)StatusAction.Rotation: value = StatusAction.Rotation; return true;
            case (ushort)StatusAction.NewPriceIndication: value = StatusAction.NewPriceIndication; return true;
            case (ushort)StatusAction.Trading: value = StatusAction.Trading; return true;
            case (ushort)StatusAction.Halt: value = StatusAction.Halt; return true;
            case (ushort)StatusAction.Pause: value = StatusAction.Pause; return true;
            case (ushort)StatusAction.Suspend: value = StatusAction.Suspend; return true;
            case (ushort)StatusAction.PreClose: value = StatusAction.PreClose; return true;
            case (ushort)StatusAction.Close: value = StatusAction.Close; return true;
            case (ushort)StatusAction.PostClose: value = StatusAction.PostClose; return true;
            case (ushort)StatusAction.SsrChange: value = StatusAction.SsrChange; return true;
            case (ushort)StatusAction.NotAvailableForTrading: value = StatusAction.NotAvailableForTrading; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="StatusReason"/>.</summary>
    public static bool TryFromStatusReason(ushort raw, out StatusReason value)
    {
        switch (raw)
        {
            case (ushort)StatusReason.None: value = StatusReason.None; return true;
            case (ushort)StatusReason.Scheduled: value = StatusReason.Scheduled; return true;
            case (ushort)StatusReason.SurveillanceIntervention: value = StatusReason.SurveillanceIntervention; return true;
            case (ushort)StatusReason.MarketEvent: value = StatusReason.MarketEvent; return true;
            case (ushort)StatusReason.InstrumentActivation: value = StatusReason.InstrumentActivation; return true;
            case (ushort)StatusReason.InstrumentExpiration: value = StatusReason.InstrumentExpiration; return true;
            case (ushort)StatusReason.RecoveryInProcess: value = StatusReason.RecoveryInProcess; return true;
            case (ushort)StatusReason.Regulatory: value = StatusReason.Regulatory; return true;
            case (ushort)StatusReason.Administrative: value = StatusReason.Administrative; return true;
            case (ushort)StatusReason.NonCompliance: value = StatusReason.NonCompliance; return true;
            case (ushort)StatusReason.FilingsNotCurrent: value = StatusReason.FilingsNotCurrent; return true;
            case (ushort)StatusReason.SecTradingSuspension: value = StatusReason.SecTradingSuspension; return true;
            case (ushort)StatusReason.NewIssue: value = StatusReason.NewIssue; return true;
            case (ushort)StatusReason.IssueAvailable: value = StatusReason.IssueAvailable; return true;
            case (ushort)StatusReason.IssuesReviewed: value = StatusReason.IssuesReviewed; return true;
            case (ushort)StatusReason.FilingReqsSatisfied: value = StatusReason.FilingReqsSatisfied; return true;
            case (ushort)StatusReason.NewsPending: value = StatusReason.NewsPending; return true;
            case (ushort)StatusReason.NewsReleased: value = StatusReason.NewsReleased; return true;
            case (ushort)StatusReason.NewsAndResumptionTimes: value = StatusReason.NewsAndResumptionTimes; return true;
            case (ushort)StatusReason.NewsNotForthcoming: value = StatusReason.NewsNotForthcoming; return true;
            case (ushort)StatusReason.OrderImbalance: value = StatusReason.OrderImbalance; return true;
            case (ushort)StatusReason.LuldPause: value = StatusReason.LuldPause; return true;
            case (ushort)StatusReason.Operational: value = StatusReason.Operational; return true;
            case (ushort)StatusReason.AdditionalInformationRequested: value = StatusReason.AdditionalInformationRequested; return true;
            case (ushort)StatusReason.MergerEffective: value = StatusReason.MergerEffective; return true;
            case (ushort)StatusReason.Etf: value = StatusReason.Etf; return true;
            case (ushort)StatusReason.CorporateAction: value = StatusReason.CorporateAction; return true;
            case (ushort)StatusReason.NewSecurityOffering: value = StatusReason.NewSecurityOffering; return true;
            case (ushort)StatusReason.MarketWideHaltLevel1: value = StatusReason.MarketWideHaltLevel1; return true;
            case (ushort)StatusReason.MarketWideHaltLevel2: value = StatusReason.MarketWideHaltLevel2; return true;
            case (ushort)StatusReason.MarketWideHaltLevel3: value = StatusReason.MarketWideHaltLevel3; return true;
            case (ushort)StatusReason.MarketWideHaltCarryover: value = StatusReason.MarketWideHaltCarryover; return true;
            case (ushort)StatusReason.MarketWideHaltResumption: value = StatusReason.MarketWideHaltResumption; return true;
            case (ushort)StatusReason.QuotationNotAvailable: value = StatusReason.QuotationNotAvailable; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="TradingEvent"/>.</summary>
    public static bool TryFromTradingEvent(ushort raw, out TradingEvent value)
    {
        switch (raw)
        {
            case (ushort)TradingEvent.None: value = TradingEvent.None; return true;
            case (ushort)TradingEvent.NoCancel: value = TradingEvent.NoCancel; return true;
            case (ushort)TradingEvent.ChangeTradingSession: value = TradingEvent.ChangeTradingSession; return true;
            case (ushort)TradingEvent.ImpliedMatchingOn: value = TradingEvent.ImpliedMatchingOn; return true;
            case (ushort)TradingEvent.ImpliedMatchingOff: value = TradingEvent.ImpliedMatchingOff; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="TriState"/>.</summary>
    public static bool TryFromTriState(byte raw, out TriState value)
    {
        switch (raw)
        {
            case (byte)TriState.NotAvailable: value = TriState.NotAvailable; return true;
            case (byte)TriState.No: value = TriState.No; return true;
            case (byte)TriState.Yes: value = TriState.Yes; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="VersionUpgradePolicy"/>.</summary>
    public static bool TryFromVersionUpgradePolicy(byte raw, out VersionUpgradePolicy value)
    {
        switch (raw)
        {
            case (byte)VersionUpgradePolicy.AsIs: value = VersionUpgradePolicy.AsIs; return true;
            case (byte)VersionUpgradePolicy.UpgradeToV2: value = VersionUpgradePolicy.UpgradeToV2; return true;
            case (byte)VersionUpgradePolicy.UpgradeToV3: value = VersionUpgradePolicy.UpgradeToV3; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="ErrorCode"/>.</summary>
    public static bool TryFromErrorCode(byte raw, out ErrorCode value)
    {
        switch (raw)
        {
            case (byte)ErrorCode.AuthFailed: value = ErrorCode.AuthFailed; return true;
            case (byte)ErrorCode.ApiKeyDeactivated: value = ErrorCode.ApiKeyDeactivated; return true;
            case (byte)ErrorCode.ConnectionLimitExceeded: value = ErrorCode.ConnectionLimitExceeded; return true;
            case (byte)ErrorCode.SymbolResolutionFailed: value = ErrorCode.SymbolResolutionFailed; return true;
            case (byte)ErrorCode.InvalidSubscription: value = ErrorCode.InvalidSubscription; return true;
            case (byte)ErrorCode.InternalError: value = ErrorCode.InternalError; return true;
            case (byte)ErrorCode.SkippedRecordsAfterSlowReading: value = ErrorCode.SkippedRecordsAfterSlowReading; return true;
            case (byte)ErrorCode.ReplayDataAgedOut: value = ErrorCode.ReplayDataAgedOut; return true;
            case (byte)ErrorCode.Unset: value = ErrorCode.Unset; return true;
            default: value = default; return false;
        }
    }

    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="SystemCode"/>.</summary>
    public static bool TryFromSystemCode(byte raw, out SystemCode value)
    {
        switch (raw)
        {
            case (byte)SystemCode.Heartbeat: value = SystemCode.Heartbeat; return true;
            case (byte)SystemCode.SubscriptionAck: value = SystemCode.SubscriptionAck; return true;
            case (byte)SystemCode.SlowReaderWarning: value = SystemCode.SlowReaderWarning; return true;
            case (byte)SystemCode.ReplayCompleted: value = SystemCode.ReplayCompleted; return true;
            case (byte)SystemCode.EndOfInterval: value = SystemCode.EndOfInterval; return true;
            case (byte)SystemCode.Unset: value = SystemCode.Unset; return true;
            default: value = default; return false;
        }
    }
}
