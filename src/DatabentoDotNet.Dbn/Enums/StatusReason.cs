namespace DatabentoDotNet.Dbn;

/// <summary>
/// The secondary enum for a <c>StatusMsg</c> update: the cause of a halt or other change in
/// <see cref="StatusAction"/>.
/// </summary>
/// <remarks>
/// Purely numeric — this type has no wire string form. Discriminants are grouped into families
/// (scheduling, regulatory, news, circuit breakers, ...) with large gaps between them reserved
/// for future family members: 7-9, 19-29, 34-39, 41-49, 51-59, 61-69, 71-79, 81-89, 91-99,
/// 101-109, 111-119, and 125-129 are all unused today, not sentinels. Upstream marks this type
/// <c>#[non_exhaustive]</c>; Databento may add variants in a future release without that being
/// a breaking change.
/// </remarks>
public enum StatusReason : ushort
{
    /// <summary>No reason is given.</summary>
    None = 0,

    /// <summary>The change in status occurred as scheduled.</summary>
    Scheduled = 1,

    /// <summary>The instrument stopped due to a market surveillance intervention.</summary>
    SurveillanceIntervention = 2,

    /// <summary>The status changed due to activity in the market.</summary>
    MarketEvent = 3,

    /// <summary>The derivative instrument began trading.</summary>
    InstrumentActivation = 4,

    /// <summary>The derivative instrument expired.</summary>
    InstrumentExpiration = 5,

    /// <summary>Recovery in progress.</summary>
    RecoveryInProcess = 6,

    /// <summary>The status change was caused by a regulatory action.</summary>
    Regulatory = 10,

    /// <summary>The status change was caused by an administrative action.</summary>
    Administrative = 11,

    /// <summary>The status change was caused by the issuer's non-compliance with regulatory requirements.</summary>
    NonCompliance = 12,

    /// <summary>Trading halted because the issuer's filings are not current.</summary>
    FilingsNotCurrent = 13,

    /// <summary>Trading halted due to an SEC trading suspension.</summary>
    SecTradingSuspension = 14,

    /// <summary>The status changed because a new issue is available.</summary>
    NewIssue = 15,

    /// <summary>The status changed because an issue is available.</summary>
    IssueAvailable = 16,

    /// <summary>The status changed because the issue(s) were reviewed.</summary>
    IssuesReviewed = 17,

    /// <summary>The status changed because the filing requirements were satisfied.</summary>
    FilingReqsSatisfied = 18,

    /// <summary>Relevant news is pending.</summary>
    NewsPending = 30,

    /// <summary>Relevant news was released.</summary>
    NewsReleased = 31,

    /// <summary>
    /// The news has been fully disseminated and times are available for the resumption in
    /// quoting and trading.
    /// </summary>
    NewsAndResumptionTimes = 32,

    /// <summary>The relevant news was not forthcoming.</summary>
    NewsNotForthcoming = 33,

    /// <summary>Halted for order imbalance.</summary>
    OrderImbalance = 40,

    /// <summary>The instrument hit limit up or limit down.</summary>
    LuldPause = 50,

    /// <summary>An operational issue occurred with the venue.</summary>
    Operational = 60,

    /// <summary>The status changed until the exchange receives additional information.</summary>
    AdditionalInformationRequested = 70,

    /// <summary>Trading halted due to a merger becoming effective.</summary>
    MergerEffective = 80,

    /// <summary>Trading is halted in an ETF due to conditions with the component securities.</summary>
    Etf = 90,

    /// <summary>Trading is halted for a corporate action.</summary>
    CorporateAction = 100,

    /// <summary>Trading is halted because the instrument is a new offering.</summary>
    NewSecurityOffering = 110,

    /// <summary>Halted due to the market-wide circuit breaker level 1.</summary>
    MarketWideHaltLevel1 = 120,

    /// <summary>Halted due to the market-wide circuit breaker level 2.</summary>
    MarketWideHaltLevel2 = 121,

    /// <summary>Halted due to the market-wide circuit breaker level 3.</summary>
    MarketWideHaltLevel3 = 122,

    /// <summary>Halted due to the carryover of a market-wide circuit breaker from the previous trading day.</summary>
    MarketWideHaltCarryover = 123,

    /// <summary>Resumption due to the end of a market-wide circuit breaker halt.</summary>
    MarketWideHaltResumption = 124,

    /// <summary>Halted because quotation is not available.</summary>
    QuotationNotAvailable = 130,
}
