namespace DatabentoDotNet.Reference;

/// <summary>
/// Which of a corporate action's three dates <c>corporate_actions.get_range</c> filters on.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>corporate::Index</c> (<c>corporate.rs:111-122</c>). It names the field the
/// request range applies to, so it changes <em>which rows come back</em> rather than only how they
/// are presented — the same role <see cref="SecurityMasterIndex"/> plays for its endpoint, and the
/// same three-way split between "when the event happens", "when it goes ex" and "when Databento
/// last changed the record saying so".
/// </para>
/// <para>
/// <b>Two of the three name a nullable column, which <see cref="SecurityMasterIndex"/> does not.</b>
/// <see cref="CorporateAction.EventDate"/> and <see cref="CorporateAction.ExDate"/> are both
/// <c>LocalDate?</c>; <see cref="CorporateAction.TsRecord"/> is required. What the server does with
/// a row whose index column is null is its business rather than this library's — upstream sorts
/// such rows first, having buffered them (<c>corporate.rs:59-63</c>), and this library does not sort
/// at all. It is worth knowing before reading a filtered result as exhaustive.
/// </para>
/// <para>
/// <b>Upstream also uses it as a sort key, and this library does not.</b> Its <c>get_range</c>
/// sorts the buffered response by whichever field the index names (<c>corporate.rs:59-63</c>). That
/// is a second use of the same value, not a second parameter — the sort happens after the response
/// arrives, and <see cref="CorporateActionsClient.GetRangeAsync"/> streams. So the index is sent and
/// is not sorted by; see that method, and #52.
/// </para>
/// <para>
/// <b><see cref="EventDate"/> is the zero value, so <c>default</c> agrees with upstream's
/// <c>#[default]</c>.</b> <see cref="SecurityMasterIndex"/> records why the request enums are
/// arranged this way round and the nine closed response enums are not.
/// </para>
/// </remarks>
public enum CorporateActionIndex
{
    /// <summary>
    /// Filter on <see cref="CorporateAction.EventDate"/> — the primary date of the event.
    /// Upstream's default, and this enum's.
    /// </summary>
    EventDate,

    /// <summary>
    /// Filter on <see cref="CorporateAction.ExDate"/> — the ex-dividend date.
    /// </summary>
    ExDate,

    /// <summary>
    /// Filter on <see cref="CorporateAction.TsRecord"/> — when the record last changed.
    /// </summary>
    TsRecord,
}
