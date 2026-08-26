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
    public const int MetadataFixedLength = 100;

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

    /// <summary>Sentinel written to the metadata header when no schema applies (mixed-schema streams).</summary>
    public const ushort NullSchema = ushort.MaxValue;

    /// <summary>Sentinel written to the metadata header when no symbol type applies.</summary>
    public const byte NullStype = byte.MaxValue;

    /// <summary>Sentinel written to the metadata header when the record count is unknown.</summary>
    public const ulong NullRecordCount = ulong.MaxValue;

    /// <summary>Length of a symbol C-string in DBN v3.</summary>
    public const int SymbolCstrLength = 71;

    /// <summary>Length of a symbol C-string in DBN v1. Retained for decoding older streams.</summary>
    public const int SymbolCstrLengthV1 = 22;
}
