namespace DatabentoDotNet.Reference;

/// <summary>
/// Wire-string conversions for the enums the <c>security_master.*</c> endpoints carry.
/// </summary>
/// <remarks>
/// <para>
/// Named for its endpoint group, as <see cref="DatabentoDotNet.Historical.BatchWireStrings"/> and
/// <see cref="DatabentoDotNet.Historical.MetadataWireStrings"/> are, and holding to the same
/// contract as <see cref="DatabentoDotNet.Dbn.WireStrings"/>: <c>ToWireString</c> throws
/// <see cref="ArgumentOutOfRangeException"/> for a value outside the defined set rather than
/// inventing a spelling for it. It holds one method because this endpoint group has one enum.
/// </para>
/// <para>
/// <b>Separate from <see cref="ReferenceWireStrings"/>, which is a different kind of table.</b>
/// That one carries the nine closed <em>response</em> alphabets: one character each, byte-backed,
/// with a <c>TryParse</c> half that the JSON converters call on every row. This is a
/// <em>request</em> enum with a multi-character spelling that nothing ever reads back.
/// </para>
/// <para>
/// <b>There is deliberately no <c>TryParseSecurityMasterIndex</c>.</b> No reference response
/// carries an index, so a parse direction would be public surface with no caller and no wire to
/// check it against — the same call <c>Json.ReferenceCodeJsonConverter</c> makes about property-name
/// support. Add it with the first response that needs one.
/// </para>
/// </remarks>
public static class SecurityMasterWireStrings
{
    /// <summary>Returns the wire string for <paramref name="value"/>.</summary>
    /// <remarks>
    /// The two spellings are upstream's <c>Display</c> impl (<c>security.rs:297-304</c>), and they
    /// are the names of the response fields they filter on — so a spelling that drifted from
    /// <see cref="SecurityMaster.TsEffective"/> or <see cref="SecurityMaster.TsRecord"/> would be
    /// wrong in a way the model itself records.
    /// </remarks>
    /// <param name="value">The index.</param>
    /// <returns>The wire string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not a defined <see cref="SecurityMasterIndex"/>.
    /// </exception>
    public static string ToWireString(this SecurityMasterIndex value) => value switch
    {
        SecurityMasterIndex.TsEffective => "ts_effective",
        SecurityMasterIndex.TsRecord => "ts_record",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
