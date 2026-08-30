using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A record copied out of the decoder's buffer and onto the heap, free of the buffer's lifetime.
/// The counterpart of <see cref="RecordRef"/>: same bytes, same accessors, opposite ownership.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because <see cref="RecordRef"/> is a <c>ref struct</c>, and a
/// <c>ref struct</c> cannot cross an <c>await</c> or a <c>yield return</c></b> (CS4007). That
/// rules it out as the element type of an <see cref="IAsyncEnumerable{T}"/>, which is the
/// ergonomic surface most callers of a live stream will reach for. Something has to be copied for
/// that surface to exist at all, and this is the copy — made once, explicitly, at a boundary the
/// caller chose, rather than smuggled into the zero-copy path.
/// </para>
/// <para>
/// <b>The price, stated rather than hidden:</b> two allocations per record — the storage and this
/// object — where <see cref="RecordRef"/> costs none. On the low-level
/// <c>FillBufferAsync</c>/<c>TryNextRecord</c> pair nothing here is touched, which is what keeps
/// that path at zero managed bytes per record. Reach for this when convenience is worth the
/// allocation, and for the zero-copy path when it is not.
/// </para>
/// <para>
/// <b>Storage is a <see langword="ulong"/> array, not a <see langword="byte"/> array,</b> for the
/// same reason <see cref="AlignedBuffer"/> is: records are reinterpreted in place by
/// <c>MemoryMarshal.AsRef&lt;T&gt;</c>, which needs 8-byte alignment for correctness on platforms
/// that enforce it. The CLR aligns a <see langword="ulong"/> array's elements to eight bytes;
/// it makes no such promise about where a <see langword="byte"/> array's payload starts. A
/// <see langword="byte"/> array would be silently fine on x64 and a fault or a torn read
/// elsewhere — the failure mode this codec's alignment assertions exist to catch.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // The zero-copy path: nothing allocated per record, and each record dies at the next call.
/// while (client.TryNextRecord(out RecordRef record))
/// {
///     Process(record);
/// }
///
/// // The owning path: two allocations per record, and the records outlive the loop.
/// var trades = new List&lt;OwnedRecord&gt;();
/// await foreach (OwnedRecord record in client.RecordsAsync())
/// {
///     if (record.Has&lt;TradeMsg&gt;())
///     {
///         trades.Add(record);
///     }
/// }
///
/// // Same accessors on both types, and AsRef() hands back a zero-copy view over the copy.
/// foreach (OwnedRecord record in trades)
/// {
///     ref readonly TradeMsg trade = ref record.Get&lt;TradeMsg&gt;();
///     Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
/// }
/// </code>
/// </example>
public sealed class OwnedRecord
{
    private readonly ulong[] _storage;
    private readonly int _length;
    private readonly bool _hasTsOut;

    private OwnedRecord(ulong[] storage, int length, bool hasTsOut)
    {
        _storage = storage;
        _length = length;
        _hasTsOut = hasTsOut;
    }

    /// <summary>
    /// Copies <paramref name="record"/>'s bytes onto the heap.
    /// </summary>
    /// <param name="record">The record to copy. It is not retained.</param>
    /// <returns>An independent copy, valid for as long as the caller keeps it.</returns>
    /// <remarks>
    /// A static factory rather than a constructor because C# forbids a <c>ref struct</c>
    /// parameter on a constructor of a non-<c>ref</c> type in some positions and, more usefully,
    /// because <c>CopyOf</c> says at the call site what the call costs.
    /// </remarks>
    public static OwnedRecord CopyOf(RecordRef record)
    {
        var bytes = record.Bytes;

        // Rounded up to whole words: the last record on a stream can be any multiple of four
        // bytes, and RecordLengthMultiplier is 4, so a 20-byte record needs three words, not two
        // and a half. The tail beyond `length` is never read.
        var storage = new ulong[(bytes.Length + sizeof(ulong) - 1) / sizeof(ulong)];
        bytes.CopyTo(MemoryMarshal.AsBytes(storage.AsSpan()));

        return new OwnedRecord(storage, bytes.Length, record.HasTsOut);
    }

    /// <summary>The record's bytes, exactly <see cref="SizeInBytes"/> long, <c>ts_out</c> included.</summary>
    public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes<ulong>(_storage)[.._length];

    /// <summary>The record's total length on the wire in bytes, <c>ts_out</c> included.</summary>
    public int SizeInBytes => _length;

    /// <summary>
    /// The length of the record struct itself: <see cref="SizeInBytes"/> minus the 8 bytes of
    /// <c>ts_out</c> when the stream appends one.
    /// </summary>
    public int StructSize => _length - (_hasTsOut ? sizeof(ulong) : 0);

    /// <summary><see langword="true"/> when the stream appends an 8-byte <c>ts_out</c> to every record.</summary>
    public bool HasTsOut => _hasTsOut;

    /// <summary>
    /// The live gateway's send timestamp appended after the record, in nanoseconds since the UNIX
    /// epoch.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The stream does not append <c>ts_out</c> — see <see cref="HasTsOut"/>.
    /// </exception>
    public ulong TsOut => AsRef().TsOut;

    /// <summary>The common record header, read in place off this object's own storage.</summary>
    public ref readonly RecordHeader Header => ref MemoryMarshal.AsRef<RecordHeader>(Bytes);

    /// <summary>
    /// The record's index timestamp: the one to sort by, and the one to key a symbol map with.
    /// Nanoseconds since the UNIX epoch. See <see cref="RecordRef.IndexTs"/>.
    /// </summary>
    public ulong IndexTs => AsRef().IndexTs;

    /// <summary>
    /// A <see cref="RecordRef"/> over this object's storage, for the accessors that are only
    /// worth writing once.
    /// </summary>
    /// <remarks>
    /// Safe to hold for as long as this object is reachable, unlike one obtained from a decoder:
    /// the span points at a heap array this object owns and nothing ever moves or reuses it. It
    /// is still a <c>ref struct</c>, so it cannot cross an <c>await</c> — hold the
    /// <see cref="OwnedRecord"/> across the await and call this afterwards.
    /// </remarks>
    /// <returns>A reference to the copied bytes.</returns>
    public RecordRef AsRef() => new(Bytes, _hasTsOut);

    /// <summary>
    /// Reports whether this record is a <typeparamref name="T"/>. See
    /// <see cref="RecordRef.Has{T}"/>.
    /// </summary>
    /// <typeparam name="T">The record struct to test for.</typeparam>
    /// <returns><see langword="true"/> if <see cref="Get{T}"/> would succeed.</returns>
    public bool Has<T>()
        where T : unmanaged, IRecord<T>
        => AsRef().Has<T>();

    /// <summary>
    /// Reinterprets this record as a <typeparamref name="T"/> in place — no further copy.
    /// </summary>
    /// <typeparam name="T">The record struct to read this record as.</typeparam>
    /// <returns>A read-only reference into this object's own storage.</returns>
    /// <exception cref="DbnDecodeException">
    /// This record is not a <typeparamref name="T"/> — see <see cref="Has{T}"/>.
    /// </exception>
    /// <remarks>
    /// Written out rather than forwarded to <see cref="RecordRef.Get{T}"/> like its neighbours,
    /// because a <see langword="ref"/> <see langword="readonly"/> returned through the temporary
    /// <see cref="RecordRef"/> that <see cref="AsRef"/> produces cannot outlive it as far as the
    /// compiler's ref-safety analysis is concerned. Reading off <see cref="Bytes"/> directly ties
    /// the lifetime to this object, which is where it actually belongs. The neighbours forward
    /// freely because they all return by value.
    /// </remarks>
    public ref readonly T Get<T>()
        where T : unmanaged, IRecord<T>
    {
        if (!Has<T>())
        {
            throw new DbnDecodeException(
                $"This record is not a {typeof(T).Name}: rtype {Header.RawRType} at {StructSize} bytes, " +
                $"where {typeof(T).Name} is {T.WireSize} bytes.");
        }

        return ref MemoryMarshal.AsRef<T>(Bytes);
    }

    /// <summary>
    /// Copies this record out as a <typeparamref name="T"/> if it is one. See
    /// <see cref="RecordRef.TryGet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The record struct to read this record as.</typeparam>
    /// <param name="record">
    /// Receives a copy of the record, or <see langword="default"/> when this is not a
    /// <typeparamref name="T"/>.
    /// </param>
    /// <returns><see langword="true"/> if this record is a <typeparamref name="T"/>.</returns>
    public bool TryGet<T>(out T record)
        where T : unmanaged, IRecord<T>
        => AsRef().TryGet(out record);
}
