namespace DatabentoDotNet.Reference;

/// <summary>
/// Which of a security master record's two timestamps <c>security_master.get_range</c> filters on.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>security::Index</c> (<c>security.rs:88-93</c>). It names the field the
/// request range applies to, so it changes <em>which rows come back</em> rather than only how they
/// are presented: <see cref="TsEffective"/> asks when the details became true of the security, and
/// <see cref="TsRecord"/> asks when Databento last changed the record saying so. A security whose
/// details have been stable since 1996 but was corrected last week is inside a recent
/// <see cref="TsRecord"/> range and outside a recent <see cref="TsEffective"/> one.
/// </para>
/// <para>
/// <b>Upstream also uses it as a sort key, and this library does not.</b> Its <c>get_range</c>
/// sorts the buffered response by whichever field the index names (<c>security.rs:50-53</c>). That
/// is a second use of the same value, not a second parameter — the sort happens after the response
/// arrives, and <see cref="SecurityMasterClient.GetRangeAsync"/> streams. So the index is sent and
/// is not sorted by; see that method, and #52.
/// </para>
/// <para>
/// <b><see cref="TsEffective"/> is the zero value, so <c>default</c> agrees with upstream's
/// <c>#[default]</c>.</b> That is the opposite arrangement from the nine closed reference enums,
/// which are byte-backed precisely so that <c>default</c> is an <em>undefined</em> value — see
/// <see cref="ReferenceWireStrings"/>. The difference is direction of travel rather than
/// inconsistency: those nine are read off the wire, where a zero would mean a field this library
/// failed to populate, and this one is only ever written to it, where a zero means the caller left
/// a defaulted property alone and upstream's builder would have filled in the same value.
/// <see cref="DatabentoDotNet.Historical.SplitDuration"/> makes the same call for the same reason.
/// </para>
/// </remarks>
public enum SecurityMasterIndex
{
    /// <summary>
    /// Filter on <see cref="SecurityMaster.TsEffective"/> — when the record's details take effect.
    /// Upstream's default, and this enum's.
    /// </summary>
    TsEffective,

    /// <summary>
    /// Filter on <see cref="SecurityMaster.TsRecord"/> — when the record last changed.
    /// </summary>
    TsRecord,
}
