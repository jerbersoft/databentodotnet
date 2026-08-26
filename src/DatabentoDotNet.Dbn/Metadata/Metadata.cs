namespace DatabentoDotNet.Dbn;

/// <summary>
/// The header that opens every DBN file and every live DBN stream: what the data is, what range
/// it covers, and how its symbols resolve.
/// </summary>
/// <remarks>
/// <para>
/// A class, not a struct, because it owns heap data — four lists whose contents are strings — so
/// there is no value-type win to be had, and copying one by value would copy references anyway.
/// This is also the one place in the codec where allocation is fine: metadata is decoded once per
/// stream, ahead of the first record, and never on the per-record path the zero-copy rule is
/// about.
/// </para>
/// <para>
/// Construction is by object initializer with <c>required</c> properties, not by a builder.
/// Upstream needs a generic type-state builder to make "you must set dataset, schema, start,
/// stype_in and stype_out" a compile-time error; C# has <c>required</c> for exactly that, and it
/// costs no extra type.
/// </para>
/// <para>
/// <b>Two 64-bit "unset" sentinels that are not the same.</b> <see cref="End"/> is absent when
/// the wire holds <see cref="DbnConstants.UndefTimestamp"/>; <see cref="Limit"/> is absent when
/// the wire holds <see cref="DbnConstants.NullLimit"/>, which is zero. Both surface here as
/// <see langword="null"/>, so callers never have to remember which is which.
/// </para>
/// </remarks>
public sealed class Metadata
{
    /// <summary>
    /// The DBN version this metadata describes: 1, 2, or 3.
    /// </summary>
    /// <remarks>
    /// After decoding, this is the version the data is <em>presented</em> as, which is the input
    /// version only under <see cref="VersionUpgradePolicy.AsIs"/>. Under the default
    /// <see cref="VersionUpgradePolicy.UpgradeToV3"/> a v1 or v2 stream reports 3 here.
    /// </remarks>
    public required byte Version { get; init; }

    /// <summary>The dataset code, for example <c>GLBX.MDP3</c>.</summary>
    public required string Dataset { get; init; }

    /// <summary>
    /// The record schema every record in the stream conforms to, or <see langword="null"/> when
    /// the stream may mix record types — which is the normal case for live data.
    /// </summary>
    public Schema? Schema { get; init; }

    /// <summary>
    /// UNIX nanoseconds: the query start, or the first record's timestamp when the file was
    /// split.
    /// </summary>
    /// <remarks>
    /// Nanoseconds as <see cref="ulong"/>, deliberately not <see cref="DateTime"/>: a
    /// <see cref="DateTime"/> tick is 100 ns, so assigning this to one would silently discard the
    /// low two digits of every timestamp in the library's public surface.
    /// </remarks>
    public required ulong Start { get; init; }

    /// <summary>
    /// UNIX nanoseconds: the query end, or the last record's timestamp when the file was split.
    /// <see langword="null"/> for an open-ended query.
    /// </summary>
    /// <remarks>
    /// A raw zero on the wire also decodes to <see langword="null"/>, matching upstream, which
    /// treats both zero and <see cref="DbnConstants.UndefTimestamp"/> as "no end". This is the one
    /// metadata field whose round-trip is not byte-identical, because re-encoding
    /// <see langword="null"/> always writes <see cref="DbnConstants.UndefTimestamp"/>; upstream
    /// has the same behaviour, no stream in the conformance corpus carries a zero here, and both
    /// spellings mean the same thing to a reader.
    /// </remarks>
    public ulong? End { get; init; }

    /// <summary>
    /// The maximum number of records the query asked for, or <see langword="null"/> when it was
    /// unlimited.
    /// </summary>
    /// <remarks>
    /// Zero and <see langword="null"/> are the same thing on the wire — <c>0</c> <em>is</em> the
    /// "no limit" sentinel — so a zero set here encodes as unlimited and decodes back as
    /// <see langword="null"/>.
    /// </remarks>
    public ulong? Limit { get; init; }

    /// <summary>
    /// The input symbology the query's symbols were expressed in, or <see langword="null"/> when
    /// the stream mixes several — again the normal case for live data.
    /// </summary>
    public SType? StypeIn { get; init; }

    /// <summary>The output symbology symbols resolve to. Never absent.</summary>
    public required SType StypeOut { get; init; }

    /// <summary>
    /// <see langword="true"/> when every record in the stream carries an appended gateway send
    /// timestamp.
    /// </summary>
    public bool TsOut { get; init; }

    /// <summary>
    /// The width in bytes of every fixed-length symbol field in this stream, NUL terminator
    /// included.
    /// </summary>
    /// <remarks>
    /// Read from the wire in DBN v2 and later. DBN v1 has no such field, so a v1 stream is
    /// <see cref="DbnConstants.SymbolCstrLengthV1"/> by definition — see
    /// <see cref="SymbolCstrLengthForVersion"/>. It is kept as decoded rather than re-derived
    /// from <see cref="Version"/> so that re-encoding reproduces the original bytes exactly.
    /// </remarks>
    public required int SymbolCstrLength { get; init; }

    /// <summary>The symbols the original query asked for. Never <see langword="null"/>.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>
    /// Symbols that failed to resolve on at least one day of the query range. Never
    /// <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<string> Partial { get; init; } = [];

    /// <summary>
    /// Symbols that failed to resolve on every day of the query range. Never
    /// <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<string> NotFound { get; init; } = [];

    /// <summary>
    /// Each requested symbol paired with the dated intervals it resolved over. Never
    /// <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<SymbolMapping> Mappings { get; init; } = [];

    /// <summary>
    /// The fixed symbol-field width a DBN stream of the given version uses when the wire does not
    /// state one.
    /// </summary>
    /// <param name="version">A DBN version.</param>
    /// <returns>
    /// <see cref="DbnConstants.SymbolCstrLengthV1"/> below version 2, otherwise
    /// <see cref="DbnConstants.SymbolCstrLength"/> — v2 and v3 share the same width.
    /// </returns>
    public static int SymbolCstrLengthForVersion(byte version)
        => version < 2 ? DbnConstants.SymbolCstrLengthV1 : DbnConstants.SymbolCstrLength;

    /// <summary>
    /// Returns this metadata presented as the version <paramref name="upgradePolicy"/> asks for,
    /// or this same instance when no change is needed.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Version"/> and <see cref="SymbolCstrLength"/> ever change — widening the
    /// symbol fields does not alter the symbols themselves, and every v1 symbol (at most 21
    /// characters) fits a v2/v3 field. There is no downgrade: the policies only move forward, so
    /// a symbol can never be asked to fit a field narrower than the one it came out of.
    /// </remarks>
    /// <param name="upgradePolicy">The policy to apply.</param>
    /// <returns>The upgraded metadata.</returns>
    internal Metadata Upgrade(VersionUpgradePolicy upgradePolicy)
    {
        if (Version >= 2)
        {
            // v2 and v3 share a symbol width, so upgrading v2 is a version bump and nothing else.
            return Version == 2 && upgradePolicy == VersionUpgradePolicy.UpgradeToV3
                ? WithVersion(3, SymbolCstrLength)
                : this;
        }

        return upgradePolicy switch
        {
            VersionUpgradePolicy.UpgradeToV2 => WithVersion(2, DbnConstants.SymbolCstrLength),
            VersionUpgradePolicy.UpgradeToV3 => WithVersion(3, DbnConstants.SymbolCstrLength),

            // AsIs. Upstream re-asserts symbol_cstr_len = 22 on this branch; the decoder has
            // already done that for v1 (the field is not on the wire), so there is nothing to fix.
            _ => this,
        };
    }

    private Metadata WithVersion(byte version, int symbolCstrLength) => new()
    {
        Version = version,
        Dataset = Dataset,
        Schema = Schema,
        Start = Start,
        End = End,
        Limit = Limit,
        StypeIn = StypeIn,
        StypeOut = StypeOut,
        TsOut = TsOut,
        SymbolCstrLength = symbolCstrLength,
        Symbols = Symbols,
        Partial = Partial,
        NotFound = NotFound,
        Mappings = Mappings,
    };
}
