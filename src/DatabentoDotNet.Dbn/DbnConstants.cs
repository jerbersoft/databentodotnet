namespace DatabentoDotNet.Dbn;

/// <summary>
/// Wire-format constants for Databento Binary Encoding (DBN).
/// </summary>
/// <remarks>
/// These mirror the normative definitions in the <c>databento/dbn</c> Rust crate. Changing any
/// value here is a wire-format change, not an implementation detail.
/// </remarks>
public static class DbnConstants
{
    /// <summary>The highest DBN version this library encodes. Versions 1 and 2 remain decodable.</summary>
    public const byte Version = 3;

    /// <summary>Magic prefix at the start of every DBN stream.</summary>
    public static ReadOnlySpan<byte> Magic => "DBN"u8;

    /// <summary>Length of <see cref="Magic"/> in bytes.</summary>
    public const int MagicLength = 3;

    /// <summary>Magic (3) + version (1) + metadata length (4). Read this before anything else.</summary>
    public const int MetadataPreludeLength = 8;

    /// <summary>Length of the fixed portion of the metadata header, following the prelude.</summary>
    /// <remarks>
    /// The same 100 bytes in every DBN version, even though the versions lay out different fields
    /// inside it: v1 spends 8 bytes on a deprecated <c>record_count</c> and 47 on reserved
    /// padding, while v2 and v3 spend 2 bytes on <c>symbol_cstr_len</c> and 53 on padding. Both
    /// add up to 100, so the <em>total</em> is version-independent and the <em>offsets</em> are
    /// not.
    /// </remarks>
    public const int MetadataFixedLength = 100;

    /// <summary>Width of the metadata header's dataset C-string field, NUL padding included.</summary>
    public const int MetadataDatasetCstrLength = 16;

    /// <summary>
    /// The largest metadata block this decoder will make room for: 512 MiB, excluding the
    /// prelude. A stream declaring more is rejected as corrupt or hostile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prelude's length field is four bytes wide and arrives before a single byte of it has
    /// been validated, so without a ceiling eight attacker-chosen bytes on a socket turn straight
    /// into a multi-gigabyte allocation. That is the first byte of an unauthenticated live
    /// stream, so it must fail as a <see cref="DbnDecodeException"/> like every other malformed
    /// input, not as an <see cref="OutOfMemoryException"/>.
    /// </para>
    /// <para>
    /// <b>Why not lower.</b> Metadata is not small in the general case. An <c>ALL_SYMBOLS</c>
    /// definition query over a large venue emits roughly
    /// <c>71·symbols + Σ(75 + 79·intervals)</c> bytes of variable section, which for a query on
    /// the order of a million instruments over a real date range is tens to hundreds of
    /// megabytes. A 16 MiB cap — the obvious first guess — rejects ordinary production data.
    /// </para>
    /// <para>
    /// <b>Why not higher.</b> 2 GiB is where <see cref="int"/> arithmetic stops working and where
    /// a single forged length field is worth mounting as a denial of service. 512 MiB sits
    /// comfortably above the largest legitimate block anyone has a reason to send and comfortably
    /// below the point where honouring it costs the process.
    /// </para>
    /// <para>
    /// This is a bound, not a fix. The structural answer is to read the variable section
    /// incrementally instead of buffering the whole block before validating any of it, which
    /// removes the need to trust the declared length at all — tracked as issue #12. Until then
    /// this ceiling is what keeps a bad length inside the exception contract.
    /// </para>
    /// </remarks>
    public const int MaxMetadataLength = 512 * 1024 * 1024;

    /// <summary>Length of the reserved run in the DBN v2 and v3 metadata header.</summary>
    public const int MetadataReservedLength = 53;

    /// <summary>Length of the reserved run in the DBN v1 metadata header.</summary>
    /// <remarks>
    /// Six bytes shorter than <see cref="MetadataReservedLength"/>: v1 spends 8 bytes on a
    /// deprecated <c>record_count</c> where v2 spends 2 on <c>symbol_cstr_len</c>, and the
    /// reserved run absorbs the 6-byte difference so the fixed section still totals
    /// <see cref="MetadataFixedLength"/>.
    /// </remarks>
    public const int MetadataReservedLengthV1 = 47;

    /// <summary>
    /// Largest possible record: <c>InstrumentDefMsg</c> (520) plus an 8-byte <c>ts_out</c>.
    /// The read buffer must never be smaller than this.
    /// </summary>
    public const int MaxRecordLength = 528;

    /// <summary>
    /// <see cref="RecordHeader.Length"/> is expressed in 32-bit words; multiply by this for bytes.
    /// </summary>
    public const int RecordLengthMultiplier = 4;

    /// <summary>Fixed-precision price scale: every 1 unit is 1e-9.</summary>
    public const long FixedPriceScale = 1_000_000_000;

    /// <summary>Sentinel for an absent price.</summary>
    public const long UndefPrice = long.MaxValue;

    /// <summary>Sentinel for an absent order size.</summary>
    public const uint UndefOrderSize = uint.MaxValue;

    /// <summary>Sentinel for an absent timestamp.</summary>
    public const ulong UndefTimestamp = ulong.MaxValue;

    /// <summary>Sentinel for an absent <c>StatMsg</c> quantity in DBN v3.</summary>
    public const long UndefStatQuantity = long.MaxValue;

    /// <summary>
    /// Sentinel for an absent <c>StatMsg</c> quantity in DBN v1 and v2, where the field is 32-bit.
    /// </summary>
    /// <remarks>
    /// Upgrading a v1 or v2 record must translate this value to
    /// <see cref="UndefStatQuantity"/> rather than widening it. A plain widening turns "no
    /// quantity" into the literal quantity 2,147,483,647, which looks entirely plausible in a
    /// market-data feed and no round-trip test would catch it.
    /// </remarks>
    public const int UndefStatQuantityV1 = int.MaxValue;

    /// <summary>Sentinel written to the metadata header when no schema applies (mixed-schema streams).</summary>
    public const ushort NullSchema = ushort.MaxValue;

    /// <summary>Sentinel written to the metadata header when no symbol type applies.</summary>
    public const byte NullStype = byte.MaxValue;

    /// <summary>Sentinel written to the metadata header when the record count is unknown.</summary>
    public const ulong NullRecordCount = ulong.MaxValue;

    /// <summary>Sentinel written to the metadata header when the query had no record limit.</summary>
    /// <remarks>
    /// Zero, not <see cref="ulong.MaxValue"/>: the metadata header's <c>limit</c> and <c>end</c>
    /// fields are both 64-bit and both nullable, but they use opposite sentinels. Confusing them
    /// turns "no limit" into a limit of 18 quintillion records, or an open-ended query end into
    /// the UNIX epoch.
    /// </remarks>
    public const ulong NullLimit = 0;

    /// <summary>Length of a symbol C-string in DBN v3.</summary>
    public const int SymbolCstrLength = 71;

    /// <summary>Length of a symbol C-string in DBN v1. Retained for decoding older streams.</summary>
    public const int SymbolCstrLengthV1 = 22;
}
