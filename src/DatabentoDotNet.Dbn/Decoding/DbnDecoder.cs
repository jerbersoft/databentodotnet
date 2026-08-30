using System.Buffers.Binary;
using DatabentoDotNet.Dbn.Internal;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Decodes a DBN stream — a file, a memory buffer, a socket — into metadata and records, handling
/// Zstandard framing transparently.
/// </summary>
/// <remarks>
/// <para>
/// The thin I/O layer over <see cref="DbnFsm"/>: it reads from a <see cref="Stream"/> into the
/// state machine's own buffer and drives it. All the decoding lives in the state machine, which
/// knows nothing about streams; this type knows nothing about DBN beyond "read more when asked".
/// </para>
/// <para>
/// <b>Compression is detected, not declared.</b> The first four bytes are compared against the
/// Zstandard frame magic and then handed straight back to whichever reader is chosen, so nothing
/// is consumed by the test itself — see <see cref="PrefixedStream"/>. A stream that starts with
/// the magic is read through the Zstandard seam; anything else is read as raw DBN.
/// </para>
/// <para>
/// Synchronous by design. <see cref="RecordRef"/> is a <c>ref struct</c> and cannot cross an
/// <c>await</c>, which is what keeps records pointing at live buffer bytes rather than at a copy.
/// The asynchronous client sits above this and drives <see cref="DbnFsm"/> directly.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using DatabentoDotNet.Dbn;
///
/// // .dbn or .dbn.zst — the Zstandard frame magic is detected, not declared.
/// using var decoder = new DbnDecoder(File.OpenRead("data.dbn.zst"));
///
/// Metadata metadata = decoder.Metadata!;
/// Console.WriteLine($"DBN v{metadata.Version} {metadata.Dataset}, compressed: {decoder.IsCompressed}");
///
/// var trades = 0;
/// while (decoder.TryNextRecord(out RecordRef record))
/// {
///     // `record` points into the decoder's own buffer and is valid only until the next call on it.
///     if (record.TryGet(out TradeMsg trade))
///     {
///         Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
///         trades++;
///     }
/// }
///
/// Console.WriteLine($"{trades} trade(s)");
/// </code>
/// <para>
/// A DBN <em>fragment</em> — a bare run of records with no magic prelude and no metadata block — has
/// to say so, because there is nothing in the bytes to detect:
/// </para>
/// <code>
/// using var fragment = new DbnDecoder(
///     File.OpenRead("data.dbn.frag"), skipMetadata: true, inputDbnVersion: 3);
/// </code>
/// </example>
public sealed class DbnDecoder : IDisposable
{
    /// <summary>
    /// Magic number that begins every Zstandard frame, read little-endian
    /// (<c>decode/zstd.rs:8</c>).
    /// </summary>
    private const uint ZstdFrameMagic = 0xFD2FB528;

    private readonly Stream _stream;
    private readonly DbnFsm _fsm;

    /// <summary>
    /// Opens a DBN stream, decoding its metadata block immediately unless
    /// <paramref name="skipMetadata"/> says there is none.
    /// </summary>
    /// <param name="source">
    /// The stream to read, positioned at its first byte. Read forward only; never seeked.
    /// </param>
    /// <param name="upgradePolicy">
    /// How to present records from an older DBN version. The default converts v1 and v2 records
    /// to v3 as they are decoded.
    /// </param>
    /// <param name="skipMetadata">
    /// <see langword="true"/> when <paramref name="source"/> is a DBN <em>fragment</em>: a bare
    /// run of records with no magic prelude and no metadata block.
    /// </param>
    /// <param name="inputDbnVersion">
    /// The fragment's DBN version, when known. Ignored unless <paramref name="skipMetadata"/> is
    /// set, since a metadata block states the version itself.
    /// </param>
    /// <param name="tsOut">
    /// Whether every record carries an appended 8-byte <c>ts_out</c>. Ignored unless
    /// <paramref name="skipMetadata"/> is set.
    /// </param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="source"/> open when this decoder is
    /// disposed.
    /// </param>
    /// <param name="bufferSize">The read buffer's size in bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnDecodeException">
    /// The stream does not begin with valid DBN metadata, or ends part-way through it. A stream
    /// that ends between <em>records</em> is not an error — see <see cref="TryNextRecord"/>.
    /// </exception>
    public DbnDecoder(
        Stream source,
        VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3,
        bool skipMetadata = false,
        byte? inputDbnVersion = null,
        bool tsOut = false,
        bool leaveOpen = false,
        int bufferSize = DbnFsm.DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(source);

        Span<byte> peek = stackalloc byte[sizeof(uint)];
        var peeked = source.ReadAtLeast(peek, peek.Length, throwOnEndOfStream: false);
        IsCompressed = peeked == peek.Length && BinaryPrimitives.ReadUInt32LittleEndian(peek) == ZstdFrameMagic;

        // Both branches get the peeked bytes back. Nothing is consumed by the detection.
        Stream stream = new PrefixedStream(peek[..peeked].ToArray(), source, leaveOpen);

        // Everything that could throw from here on happens inside the try, and `stream` always
        // names the outermost wrapper built so far — so the catch disposes the whole chain
        // whether the failure was the decompressor refusing the frame or the metadata refusing to
        // decode. A stream that turns out not to be DBN at all is the common case here, and the
        // caller has no handle on the wrappers this constructor just built around their stream.
        try
        {
            if (IsCompressed)
            {
                stream = ZstdDecompressor.Decompress(stream);
            }

            _stream = stream;
            _fsm = new DbnFsm(upgradePolicy, skipMetadata, inputDbnVersion, tsOut, bufferSize);

            if (!skipMetadata)
            {
                DecodeMetadata();
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary><see langword="true"/> when the source stream is Zstandard-compressed.</summary>
    public bool IsCompressed { get; }

    /// <summary>
    /// The stream's metadata, or <see langword="null"/> for a fragment. Already presented
    /// according to the upgrade policy.
    /// </summary>
    public Metadata? Metadata => _fsm.Metadata;

    /// <summary>
    /// Decodes the next record, reading from the source stream as needed.
    /// </summary>
    /// <param name="record">
    /// Receives the decoded record. Valid only until the next call on this decoder — the bytes it
    /// points at live in the decoder's own buffer, which the next read may move.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a record was decoded; <see langword="false"/> at the end of the
    /// stream.
    /// </returns>
    /// <exception cref="DbnDecodeException">The stream's bytes are not valid DBN.</exception>
    /// <remarks>
    /// A stream ending is not an error, including one that ends part-way through a record: the
    /// trailing partial bytes are simply never yielded. That is what makes this the
    /// <c>Try</c>-shaped member and not a throwing one.
    /// </remarks>
    public bool TryNextRecord(out RecordRef record)
    {
        while (true)
        {
            if (_fsm.TryNextRecord(out record))
            {
                return true;
            }

            // Safe to call Space() here and only here: the state machine just told us it has no
            // complete record, so there is no live RecordRef for the shift to invalidate.
            var read = _stream.Read(_fsm.Space());
            if (read == 0)
            {
                record = default;
                return false;
            }

            _fsm.Fill(read);
        }
    }

    /// <summary>Disposes the decompressor, if any, and the source stream unless it was left open.</summary>
    public void Dispose() => _stream.Dispose();

    private void DecodeMetadata()
    {
        while (true)
        {
            var status = _fsm.Process(out _, out _);
            if (status == ProcessStatus.Metadata)
            {
                return;
            }

            if (status == ProcessStatus.Record)
            {
                throw new DbnDecodeException("Invalid DBN stream: a record was decoded before the metadata block.");
            }

            var read = _stream.Read(_fsm.Space());
            if (read == 0)
            {
                // Unlike a stream ending between records, this one is an error: a DBN stream that
                // stops inside its own header is truncated, and there is no partial metadata to
                // hand back.
                throw new DbnDecodeException("Unexpected end of stream while decoding the DBN metadata block.");
            }

            _fsm.Fill(read);
        }
    }
}
