using System.Runtime.CompilerServices;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>
/// An asynchronous reader over a DBN record stream: what <see cref="TimeseriesClient.GetRangeAsync"/> and
/// <see cref="TimeseriesClient.GetRangeToFileAsync"/> hand back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than <see cref="DbnDecoder"/>.</b> Upstream's <c>get_range</c> returns
/// an <c>AsyncDbnDecoder</c> (<c>timeseries.rs:88-97</c>), and the faithful port of an async
/// decoder in this repo is not a decoder at all — it is the
/// <see cref="FillBufferAsync"/>/<see cref="TryNextRecord"/> pair, for the reason PORTING.md §1
/// gives and <c>LiveClient</c> already implements: <see cref="RecordRef"/> is a <c>ref struct</c>,
/// an <c>async</c> method cannot return one, and so there is no <c>Task&lt;RecordRef&gt;</c> and
/// never can be. <see cref="DbnDecoder"/> is synchronous by design and says so on its own class
/// comment; pointing it at an HTTP response stream would block a thread pool thread for the length
/// of a multi-gigabyte download. This type drives <see cref="DbnFsm"/> directly, which is what that
/// same class comment says the asynchronous clients do.
/// </para>
/// <para>
/// <b>The read loop is the whole API.</b> Drain, refill, repeat — the example below.
/// </para>
/// <para>
/// The inner loop must run to <see langword="false"/> before each refill. That is not a style
/// preference: a refill may shift the buffer, which is exactly what invalidates a
/// <see cref="RecordRef"/> the caller is still holding, and it is also the ordering
/// <see cref="TryNextRecord"/>'s truncation check depends on. <see cref="ReadRecordsAsync"/> is
/// this loop written once, at the cost of a copy per record.
/// </para>
/// <para>
/// <b>Zstandard is not detected here, because it was requested.</b>
/// <see cref="GetRangeParams.ToFormParameters"/> hard-codes <c>compression=zstd</c> on every
/// request, so the caller — <see cref="TimeseriesClient"/> — knows the framing and unwraps it
/// through <c>Internal/ZstdDecompressor.cs</c> before constructing this type.
/// <see cref="DbnDecoder"/> sniffs the frame magic because it is handed files of unknown
/// provenance; this type is handed one thing only.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> One reader is one cursor over one buffer, the same call
/// <c>LiveClient</c> makes about its record loop. Several readers may be read concurrently; one
/// reader may not.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var reader = await client.Timeseries.GetRangeAsync(request);
///
/// // Every DBN stream opens with a metadata block, and it echoes the request rather than describing
/// // the answer — so it says what was asked for even when nothing came back.
/// Console.WriteLine($"DBN v{reader.Metadata.Version} {reader.Metadata.Dataset}");
///
/// while (true)
/// {
///     while (reader.TryNextRecord(out RecordRef record))
///     {
///         // `record` is valid until the next call on this reader, and no longer.
///         if (record.TryGet(out TradeMsg trade))
///         {
///             Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
///         }
///     }
///
///     if (await reader.FillBufferAsync() == 0)
///     {
///         break;
///     }
/// }
/// </code>
/// <para>
/// <see cref="ReadRecordsAsync"/> is that loop written once, at a copy per record — the right choice
/// whenever a record has to outlive the iteration:
/// </para>
/// <code>
/// await foreach (OwnedRecord record in reader.ReadRecordsAsync())
/// {
///     if (record.TryGet(out TradeMsg trade))
///     {
///         Console.WriteLine(trade.Price);
///     }
/// }
/// </code>
/// </example>
public sealed class TimeseriesReader : IAsyncDisposable
{
    private readonly Stream _source;
    private readonly DbnFsm _fsm;
    private readonly bool _leaveOpen;

    private IDisposable? _alsoDispose;
    private bool _endOfStream;
    private bool _drained;
    private bool _disposed;

    private TimeseriesReader(Stream source, DbnFsm fsm, bool leaveOpen)
    {
        _source = source;
        _fsm = fsm;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// The stream's metadata block, decoded before this object existed and already presented
    /// according to the upgrade policy.
    /// </summary>
    /// <remarks>
    /// Never <see langword="null"/>, unlike <see cref="DbnDecoder.Metadata"/>: that one is nullable
    /// because it also decodes bare fragments, and <c>timeseries.get_range</c> never returns one.
    /// A response with no records still carries a full metadata block — the API answers an empty
    /// range with <c>200</c>, a metadata block, no records, and an <c>X-Warning</c> saying it found
    /// nothing.
    /// </remarks>
    public Metadata Metadata { get; private init; } = null!;

    /// <summary>
    /// Reads the metadata block, then hands back a stream positioned at the first record.
    /// </summary>
    /// <param name="source">
    /// The decompressed DBN stream, positioned at its first byte. Read forward only; never seeked.
    /// </param>
    /// <param name="upgradePolicy">How to present records from an older DBN version.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="source"/> open when this reader is disposed.
    /// </param>
    /// <param name="bufferSize">The read buffer's size in bytes.</param>
    /// <param name="cancellationToken">Cancels the metadata read.</param>
    /// <returns>A reader positioned at the first record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnDecodeException">
    /// The stream does not begin with valid DBN metadata, or ends part-way through it.
    /// </exception>
    public static async ValueTask<TimeseriesReader> OpenAsync(
        Stream source,
        VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3,
        bool leaveOpen = false,
        int bufferSize = DbnFsm.DefaultBufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var fsm = new DbnFsm(upgradePolicy, skipMetadata: false, inputDbnVersion: null, tsOut: false, bufferSize);

        try
        {
            var metadata = await ReadMetadataAsync(source, fsm, cancellationToken).ConfigureAwait(false);
            return new TimeseriesReader(source, fsm, leaveOpen) { Metadata = metadata };
        }
        catch
        {
            if (!leaveOpen)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Reads more bytes from the source into the decode buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this only when <see cref="TryNextRecord"/> has answered <see langword="false"/>. It may
    /// shift the buffer, which invalidates every <see cref="RecordRef"/> handed out since the last
    /// call — the same contract <c>LiveClient.FillBufferAsync</c> carries, and the reason neither
    /// takes a record as a parameter it could invalidate.
    /// </para>
    /// <para>
    /// <b>This is where a truncated download is reported</b>, because this is where the read loop
    /// ends. The natural loop breaks the moment this returns zero and never calls
    /// <see cref="TryNextRecord"/> again, so a check that lived only there would be unreachable
    /// from the one shape every caller writes.
    /// </para>
    /// <para>
    /// The condition is exact rather than heuristic, and fires only when all three hold:
    /// <see cref="TryNextRecord"/> has already answered <see langword="false"/> at least once since
    /// the last refill, the source has just reported end of data, and
    /// <see cref="DbnFsm.BufferedByteCount"/> is non-zero. Those bytes are the front of a record
    /// whose tail never arrived; there is no other way to reach that state. A caller who refills
    /// <em>without</em> draining first has not established that the leftover bytes are incomplete,
    /// so nothing is thrown — the check declines rather than guesses.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many bytes were read; zero at the end of the source.</returns>
    /// <exception cref="ObjectDisposedException">This reader has been disposed.</exception>
    /// <exception cref="DbnDecodeException">
    /// The source ended part-way through a record — the download was truncated.
    /// </exception>
    /// <exception cref="IOException">
    /// The connection dropped mid-body. A chunked response whose terminating chunk never arrives
    /// fails here rather than ending quietly, which is the other half of "a truncated download is
    /// an exception".
    /// </exception>
    public ValueTask<int> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_endOfStream)
        {
            ThrowIfTruncated();
            return new ValueTask<int>(0);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var pending = _source.ReadAsync(_fsm.SpaceMemory(), cancellationToken);

        return pending.IsCompletedSuccessfully
            ? new ValueTask<int>(Complete(pending.Result))
            : AwaitFillAsync(pending);
    }

    /// <summary>
    /// Decodes the next record already sitting in the buffer.
    /// </summary>
    /// <param name="record">
    /// Receives the decoded record. Valid only until the next call on this stream — the bytes it
    /// points at live in the decode buffer, which the next <see cref="FillBufferAsync"/> may move.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a record was decoded; <see langword="false"/> when the buffer
    /// holds no complete record, which means either "refill and ask again" or "the stream is
    /// finished" depending on whether <see cref="FillBufferAsync"/> has since returned zero.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Unlike <see cref="DbnDecoder.TryNextRecord"/>, this throws on a truncated stream.</b> That
    /// decoder documents a trailing partial record as not an error and silently drops it, which is
    /// the right call for a local file that may legitimately be a fragment. It is the wrong call
    /// for a download: bytes the server promised and did not deliver are a failed request, and
    /// silently returning the records that did arrive turns a network fault into a short answer the
    /// caller has no way to distinguish from a complete one. #31 drew the same line on the metadata
    /// path.
    /// </para>
    /// <para>
    /// The check is exact rather than heuristic. It fires only when all three hold: the machine has
    /// no complete record, <see cref="FillBufferAsync"/> has reported end of source, and
    /// <see cref="DbnFsm.BufferedByteCount"/> is non-zero. Those bytes are the front of a record
    /// whose tail never arrived; there is no other way to reach that state.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This reader has been disposed.</exception>
    /// <exception cref="DbnDecodeException">
    /// The source ended part-way through a record, or its bytes are not valid DBN.
    /// </exception>
    public bool TryNextRecord(out RecordRef record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_fsm.TryNextRecord(out record))
        {
            return true;
        }

        // The caller has drained to the point where the buffer holds no complete record. That is
        // the fact FillBufferAsync needs and cannot establish for itself without consuming one.
        _drained = true;
        ThrowIfTruncated();
        return false;
    }

    /// <summary>
    /// Every record in the stream, as an <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <remarks>
    /// <b>The convenient surface, and it copies — necessarily.</b> <c>yield return</c> carries the
    /// same restriction <c>await</c> does, so a <c>ref struct</c> cannot leave an iterator. Each
    /// record arrives as an <see cref="OwnedRecord"/>, whose own documentation states the cost.
    /// Callers who need the zero-copy guarantee want the
    /// <see cref="FillBufferAsync"/>/<see cref="TryNextRecord"/> pair, which this is written in
    /// terms of and does not bypass — including its truncation check. The same split
    /// <c>LiveClient</c> offers.
    /// </remarks>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    /// <returns>Every record, in order, until the stream ends.</returns>
    /// <exception cref="DbnDecodeException">The stream was truncated, or its bytes are not valid DBN.</exception>
    public async IAsyncEnumerable<OwnedRecord> ReadRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            while (true)
            {
                // Copied inside the inner scope so that no RecordRef is live across the yield —
                // the compiler enforces this (CS4013), which is the lifetime rule stated as a
                // build error rather than as a comment.
                OwnedRecord owned;
                if (TryNextRecord(out var record))
                {
                    owned = OwnedRecord.CopyOf(record);
                }
                else
                {
                    break;
                }

                yield return owned;
            }

            if (await FillBufferAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Disposes the source stream unless it was left open, and with it the HTTP response it was
    /// reading from, when there is one.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_leaveOpen)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }

        // After the stream, never before: disposing the response tears down the body stream this
        // was decoding, and doing it first would race the decompressor's own teardown.
        _alsoDispose?.Dispose();
        _alsoDispose = null;
    }

    /// <summary>
    /// Ties an <see cref="HttpResponseMessage"/>'s lifetime to this reader's.
    /// </summary>
    /// <remarks>
    /// <c>GetRangeAsync</c> cannot dispose the response it read from — the caller is about to read
    /// the body — and cannot leak it either. Handing it here is what makes a single
    /// <c>await using</c> on the returned reader release the socket as well.
    /// </remarks>
    /// <param name="response">The response to dispose alongside this reader.</param>
    /// <returns>This reader, so the call reads as part of the return.</returns>
    internal TimeseriesReader OwningResponse(IDisposable response)
    {
        _alsoDispose = response;
        return this;
    }

    private async ValueTask<int> AwaitFillAsync(ValueTask<int> pending) =>
        Complete(await pending.ConfigureAwait(false));

    private int Complete(int read)
    {
        if (read == 0)
        {
            _endOfStream = true;
            ThrowIfTruncated();
            return 0;
        }

        _fsm.Fill(read);

        // Fresh bytes may complete the record that was partial a moment ago, so whatever the last
        // TryNextRecord concluded no longer holds.
        _drained = false;
        return read;
    }

    /// <summary>
    /// Fails when the source has ended leaving the front of a record behind.
    /// </summary>
    /// <remarks>
    /// Called from both entry points because either can be the one that completes the picture: the
    /// read that returns zero, or the drain that proves the leftover bytes are not a record. Both
    /// conditions must hold, and neither alone means anything.
    /// </remarks>
    private void ThrowIfTruncated()
    {
        if (_endOfStream && _drained && _fsm.BufferedByteCount > 0)
        {
            throw new DbnDecodeException(
                $"The DBN stream ended part-way through a record, with {_fsm.BufferedByteCount} "
                + "byte(s) of it received. The download was truncated.");
        }
    }

    /// <summary>
    /// Drives the machine until it yields the metadata block, reading as needed.
    /// </summary>
    /// <remarks>
    /// The asynchronous twin of <c>DbnDecoder.DecodeMetadata</c>, and it makes the same two
    /// judgements: a record before the metadata block is a malformed stream, and a stream that ends
    /// inside the block is truncated rather than empty. Neither is a case
    /// <see cref="TryNextRecord"/> could report later, because there would be no metadata to hand
    /// back.
    /// </remarks>
    private static async ValueTask<Metadata> ReadMetadataAsync(
        Stream source,
        DbnFsm fsm,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = fsm.Process(out _, out _);

            if (status == ProcessStatus.Metadata)
            {
                // Process reported Metadata, so the machine has one.
                return fsm.Metadata!;
            }

            if (status == ProcessStatus.Record)
            {
                throw new DbnDecodeException(
                    "Invalid DBN stream: a record was decoded before the metadata block.");
            }

            var read = await source.ReadAsync(fsm.SpaceMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new DbnDecodeException(
                    "Unexpected end of stream while decoding the DBN metadata block.");
            }

            fsm.Fill(read);
        }
    }
}
