using System.Buffers.Binary;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The DBN bodies the timeseries tests serve: Databento's own bytes, and streams built by
/// repeating them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here encodes DBN.</b> That is the independent-oracle rule this project's
/// <c>BannedSymbols.txt</c> enforces, and it survives intact: the metadata block and the records
/// come from the vendored corpus verbatim, and the only structure this file understands is the
/// four-byte length in the metadata prelude — which it reads with
/// <see cref="BinaryPrimitives.ReadUInt32LittleEndian"/> straight from the DBN specification, not
/// through the codec. <see cref="SyntheticDbnFragment"/>'s own remarks name this file's approach as
/// the right one for a whole stream: "serve one of the vendored fixtures".
/// </para>
/// <para>
/// <b>Repeating a record is not encoding one.</b> <see cref="Repeating"/> concatenates bytes
/// Databento produced; it never constructs a record, never computes a length field, and would not
/// know how. A stream of a thousand copies of a real trade is a thousand real trades as far as any
/// decoder is concerned, which is exactly what a flat-memory assertion needs and what no
/// hand-written record could safely provide.
/// </para>
/// <para>
/// <b>Compression is this harness's, and always was.</b> The project already references
/// <c>ZstdSharp.Port</c> so the mock gateway can serve zstd-framed JSONL; the library only ever
/// decompresses. Compressing here is the same call for the same reason.
/// </para>
/// </remarks>
public static class TimeseriesFixture
{
    /// <summary>
    /// The zstd-framed DBN stream the tests serve as a <c>get_range</c> body: two real
    /// <c>trades</c> records at DBN v3, so no upgrade runs and the bytes are the API's shape.
    /// </summary>
    public const string CompressedName = "test_data.trades.v3.dbn.zst";

    /// <summary>
    /// The same schema uncompressed and at DBN v2 — the source for streams this file builds, and
    /// for the truncation cases, which need to cut at a known offset before compressing.
    /// </summary>
    public const string PlainName = "test_data.trades.dbn";

    /// <summary>How many records each vendored fixture above carries.</summary>
    /// <remarks>
    /// Asserted by <c>TimeseriesFixtureTests</c> rather than trusted: a fixture that changed
    /// upstream would otherwise quietly weaken every count assertion built on it.
    /// </remarks>
    public const int RecordCount = 2;

    /// <summary>The directory the linked fixtures are copied to.</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");

    /// <summary>The zstd-framed DBN body, exactly as vendored.</summary>
    public static byte[] Compressed() => File.ReadAllBytes(Path.Combine(Directory, CompressedName));

    /// <summary>The uncompressed DBN stream, exactly as vendored.</summary>
    public static byte[] Plain() => File.ReadAllBytes(Path.Combine(Directory, PlainName));

    /// <summary>
    /// <see cref="CompressedName"/> unwrapped: real DBN v3 records, so a decoder reading them runs
    /// no upgrade.
    /// </summary>
    /// <remarks>
    /// The source for <see cref="Repeating"/>, and the reason it is v3 rather than
    /// <see cref="Plain"/>'s v2: the allocation measurement should report what decoding costs, not
    /// what upgrading costs. The truncation fixtures stay on v2 deliberately, so the two paths are
    /// both covered somewhere.
    /// </remarks>
    /// <returns>The decompressed stream.</returns>
    public static byte[] Decompressed()
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(Compressed()).ToArray();
    }

    /// <summary>
    /// The length of <paramref name="stream"/>'s metadata block in bytes, prelude included.
    /// </summary>
    /// <remarks>
    /// Eight bytes of prelude — <c>DBN</c>, a version byte, then a little-endian <c>u32</c> length —
    /// plus that length. Read from the specification rather than from
    /// <c>DatabentoDotNet.Dbn</c>, which is the whole point of this file.
    /// </remarks>
    /// <param name="stream">A complete DBN stream.</param>
    /// <returns>The offset of the first record.</returns>
    public static int MetadataLength(ReadOnlySpan<byte> stream) =>
        8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(stream[4..]);

    /// <summary>
    /// The record bytes of <paramref name="stream"/> — everything past the metadata block.
    /// </summary>
    /// <param name="stream">A complete DBN stream.</param>
    /// <returns>The records, back to back.</returns>
    public static ReadOnlySpan<byte> RecordsOf(ReadOnlySpan<byte> stream) =>
        stream[MetadataLength(stream)..];

    /// <summary>
    /// A DBN stream carrying the vendored metadata block and its records repeated
    /// <paramref name="times"/> over.
    /// </summary>
    /// <param name="times">How many copies of the fixture's record run to append.</param>
    /// <returns>The uncompressed stream, and how many records it holds.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="times"/> is negative.</exception>
    public static (byte[] Bytes, int Records) Repeating(int times)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(times);

        var source = Decompressed();
        var metadata = source.AsSpan(0, MetadataLength(source));
        var records = RecordsOf(source);

        var built = new byte[metadata.Length + (records.Length * times)];
        metadata.CopyTo(built);

        for (var index = 0; index < times; index++)
        {
            records.CopyTo(built.AsSpan(metadata.Length + (index * records.Length)));
        }

        return (built, RecordCount * times);
    }

    /// <summary>Zstandard-compresses <paramref name="body"/>, the way the API serves it.</summary>
    /// <param name="body">The uncompressed DBN stream.</param>
    /// <returns>A single zstd frame.</returns>
    public static byte[] Compress(ReadOnlySpan<byte> body)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(body).ToArray();
    }

    /// <summary>
    /// The vendored stream cut off part-way through its last record, then compressed.
    /// </summary>
    /// <remarks>
    /// <b>The cut is in the DBN, not in the zstd frame</b>, and the difference is the whole reason
    /// this method exists. Truncating the compressed body tests the decompressor's reaction to a
    /// broken frame; truncating before compressing produces a <em>valid</em> frame holding an
    /// invalid stream, which is what isolates the decoder's own truncation check. The mock gateway's
    /// <c>Truncated</c> and <c>Dropped</c> responses cover the other case, at the transport layer
    /// where it belongs.
    /// </remarks>
    /// <param name="missingBytes">How many bytes of the final record to withhold.</param>
    /// <returns>The compressed body, and how many whole records survive in it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="missingBytes"/> is not between one and the final record's length, exclusive
    /// of the length itself — cutting a whole record off is not a truncation, it is a shorter
    /// stream.
    /// </exception>
    public static (byte[] Body, int WholeRecords) TruncatedMidRecord(int missingBytes)
    {
        var source = Plain();
        var records = RecordsOf(source);

        // Every record in this fixture is the same size, so the last one starts a fixed step back
        // from the end; reading its own length field is what keeps that true if the fixture changes.
        var lastRecordLength = records[^(records.Length / RecordCount)] * 4;

        ArgumentOutOfRangeException.ThrowIfLessThan(missingBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(missingBytes, lastRecordLength);

        return (Compress(source.AsSpan(0, source.Length - missingBytes)), RecordCount - 1);
    }
}
