namespace DatabentoDotNet.Reference;

/// <summary>
/// Wire-string conversions for the enums the <c>corporate_actions.*</c> endpoints carry.
/// </summary>
/// <remarks>
/// <para>
/// Named for its endpoint group, as <see cref="SecurityMasterWireStrings"/> is, and holding to the
/// same contract as <see cref="DatabentoDotNet.Dbn.WireStrings"/>: <c>ToWireString</c> throws
/// <see cref="ArgumentOutOfRangeException"/> for a value outside the defined set rather than
/// inventing a spelling for it. It holds one method because this endpoint group has one enum on the
/// request side — the ten open code types and eight closed ones its <em>response</em> carries are
/// <see cref="ReferenceWireStrings"/>' business, and <c>list_enums</c>'.
/// </para>
/// <para>
/// <b>There is deliberately no <c>TryParseCorporateActionIndex</c></b>, for the reason
/// <see cref="SecurityMasterWireStrings"/> gives about its own: no reference response carries an
/// index, so a parse direction would be public surface with no caller and no wire to check it
/// against.
/// </para>
/// </remarks>
public static class CorporateActionsWireStrings
{
    /// <summary>Returns the wire string for <paramref name="value"/>.</summary>
    /// <remarks>
    /// The three spellings are upstream's <c>Display</c> impl (<c>corporate.rs:445-452</c>), and
    /// they are the names of the response fields they filter on — so a spelling that drifted from
    /// <see cref="CorporateAction.EventDate"/>, <see cref="CorporateAction.ExDate"/> or
    /// <see cref="CorporateAction.TsRecord"/> would be wrong in a way the model itself records.
    /// </remarks>
    /// <param name="value">The index.</param>
    /// <returns>The wire string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="CorporateActionIndex"/>.
    /// </exception>
    public static string ToWireString(this CorporateActionIndex value) => value switch
    {
        CorporateActionIndex.EventDate => "event_date",
        CorporateActionIndex.ExDate => "ex_date",
        CorporateActionIndex.TsRecord => "ts_record",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
