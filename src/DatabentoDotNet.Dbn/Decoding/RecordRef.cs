using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A non-owning, runtime-polymorphic view over one DBN record that still sits in the decoder's
/// read buffer. Downcast it to a concrete record struct with <see cref="Has{T}"/> plus
/// <see cref="Get{T}"/>, or with <see cref="TryGet{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The port of upstream's <c>RecordRef</c> (<c>record_ref.rs</c>). Upstream stores a raw
/// <c>NonNull&lt;RecordHeader&gt;</c> plus a <c>PhantomData</c> lifetime and marks the
/// constructor <c>unsafe</c>; the .NET equivalent of that lifetime is a
/// <see cref="ReadOnlySpan{T}"/> field in a <see langword="ref"/> <see langword="struct"/>, which
/// the compiler enforces rather than the programmer promising. A <c>ref struct</c> cannot be
/// boxed, stored in a field, captured by a lambda, or carried across an <c>await</c> — which is
/// exactly the set of things that would let a record outlive the buffer bytes it points at.
/// </para>
/// <para>
/// <b>That restriction is the design, not a limitation.</b> The whole reason this codec exists is
/// that records are reinterpreted in place over the read buffer rather than copied out of it, and
/// the decoder is free to overwrite or move those bytes on the next refill. The async I/O layer
/// therefore sits <em>above</em> the decoder and calls <c>Fill</c> itself; nothing here is or
/// becomes <c>async</c>.
/// </para>
/// <para>
/// <b>Alignment is a precondition.</b> Records are reinterpreted with
/// <see cref="MemoryMarshal.AsRef{T}(ReadOnlySpan{byte})"/>, so the buffer a record is read from
/// must start 8-byte aligned. <see cref="AlignedBuffer"/> guarantees that for everything the decoder
/// produces; a caller constructing a <see cref="RecordRef"/> over its own storage must too.
/// Upstream pins the same requirement with a <c>debug_assert_eq!</c> on
/// <c>align_offset</c> (<c>record_ref.rs:109-114</c>); the <see cref="Debug.Assert(bool, string)"/>
/// below is its direct counterpart and, like upstream's, costs nothing in a release build.
/// </para>
/// <para>
/// Every multi-byte field read through this type is little-endian, because the reinterpret is
/// raw. .NET has no supported big-endian target, so no byte swapping is done; on a hypothetical
/// big-endian host every numeric field would be wrong.
/// </para>
/// </remarks>
public readonly ref struct RecordRef
{
    /// <summary>
    /// The size of the common record header in bytes — 16 — and so the fewest bytes any record
    /// can legitimately declare. Upstream's <c>DbnFsm::HEADER_LEN</c> (<c>fsm.rs:107</c>).
    /// </summary>
    internal static readonly int HeaderLength = Unsafe.SizeOf<RecordHeader>();

    private readonly ReadOnlySpan<byte> _bytes;
    private readonly bool _hasTsOut;

    /// <summary>
    /// Wraps the record that begins at the start of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">
    /// Bytes starting at a <see cref="RecordHeader"/> and at least as long as the length that
    /// header declares. Anything past the record is ignored, so passing the whole read buffer is
    /// fine. Must start 8-byte aligned — see the remarks on <see cref="RecordRef"/>.
    /// </param>
    /// <param name="hasTsOut">
    /// Whether the stream appends an 8-byte <c>ts_out</c> send timestamp to every record. This is
    /// a property of the stream, stated once in the DBN metadata, not something recoverable from
    /// the record's own bytes — which is why it has to be passed in.
    /// </param>
    /// <exception cref="DbnDecodeException">
    /// <paramref name="buffer"/> is shorter than a record header, the header declares a length
    /// shorter than the header itself, <paramref name="buffer"/> is shorter than that declared
    /// length, or <paramref name="hasTsOut"/> is set on a record with no room for the timestamp.
    /// </exception>
    public RecordRef(ReadOnlySpan<byte> buffer, bool hasTsOut = false)
    {
        if (buffer.Length < HeaderLength)
        {
            throw new DbnDecodeException(
                $"Invalid DBN record: a record header is {HeaderLength} bytes but only {buffer.Length} were available.");
        }

        var declared = buffer[0] * DbnConstants.RecordLengthMultiplier;
        if (declared < HeaderLength)
        {
            throw new DbnDecodeException(
                $"Invalid DBN record: the declared length {declared} is shorter than the {HeaderLength}-byte header.");
        }

        if (buffer.Length < declared)
        {
            throw new DbnDecodeException(
                $"Invalid DBN record: the header declares {declared} bytes but only {buffer.Length} were available.");
        }

        if (hasTsOut && declared < HeaderLength + sizeof(ulong))
        {
            throw new DbnDecodeException(
                $"Invalid DBN record: the stream appends ts_out but the record is only {declared} bytes, " +
                $"leaving no room for it after the {HeaderLength}-byte header.");
        }

        _bytes = buffer[..declared];
        _hasTsOut = hasTsOut;

        AssertAligned(_bytes);
    }

    /// <summary>The record's bytes, exactly <see cref="SizeInBytes"/> long, <c>ts_out</c> included.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// The record's total length on the wire in bytes, <c>ts_out</c> included. This is what the
    /// header's <see cref="RecordHeader.Length"/> word count multiplies out to, and what the
    /// decoder advances the read buffer by.
    /// </summary>
    public int SizeInBytes => _bytes.Length;

    /// <summary>
    /// The length of the record struct itself: <see cref="SizeInBytes"/> minus the 8 bytes of
    /// <c>ts_out</c> when the stream appends one. <b>This</b> — never <see cref="SizeInBytes"/> —
    /// is what gets compared against <see cref="IRecord{TSelf}.WireSize"/>.
    /// </summary>
    public int StructSize => _bytes.Length - (_hasTsOut ? sizeof(ulong) : 0);

    /// <summary><see langword="true"/> when the stream appends an 8-byte <c>ts_out</c> to every record.</summary>
    public bool HasTsOut => _hasTsOut;

    /// <summary>
    /// The live gateway's send timestamp appended after the record, in nanoseconds since the UNIX
    /// epoch.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The stream does not append <c>ts_out</c> — see <see cref="HasTsOut"/>. The eight bytes
    /// after a record on a stream without <c>ts_out</c> belong to the <em>next</em> record, so
    /// reading them as a timestamp would return a plausible-looking number that is not one.
    /// </exception>
    public ulong TsOut => _hasTsOut
        ? BinaryPrimitives.ReadUInt64LittleEndian(_bytes[^sizeof(ulong)..])
        : throw new InvalidOperationException("This stream does not append ts_out to its records.");

    /// <summary>The common record header, read in place.</summary>
    public ref readonly RecordHeader Header => ref MemoryMarshal.AsRef<RecordHeader>(_bytes);

    /// <summary>
    /// Reports whether this record is a <typeparamref name="T"/>: its <c>rtype</c> is one
    /// <typeparamref name="T"/> decodes <em>and</em> its <see cref="StructSize"/> is exactly
    /// <see cref="IRecord{TSelf}.WireSize"/>.
    /// </summary>
    /// <typeparam name="T">The record struct to test for.</typeparam>
    /// <returns><see langword="true"/> if <see cref="Get{T}"/> would succeed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Both halves are load-bearing, and the size comparison is exact.</b> An <c>rtype</c>
    /// alone does not identify a record: <see cref="RType.InstrumentDef"/>,
    /// <see cref="RType.SymbolMapping"/>, <see cref="RType.Error"/>, <see cref="RType.System"/>
    /// and <see cref="RType.Statistics"/> each decode to a different struct depending on the
    /// record's length, because those layouts changed between DBN versions. A <c>&gt;=</c>
    /// comparison would let a 520-byte v3 <c>InstrumentDefMsg</c> answer <see langword="true"/>
    /// for the 360-byte <c>InstrumentDefMsgV1</c> and decode as the wrong version — silently,
    /// since a reinterpret cannot fail. Upstream's own downcast (<c>record_ref.rs:236-251</c>)
    /// uses <c>&gt;=</c>; the size-dependent dispatch it relies on elsewhere
    /// (<c>rtype_dispatch_base!</c>, <c>macros.rs:14-69</c>) picks the version by comparing the
    /// record size against each struct's size, and exact equality is the same rule stated
    /// without the ordering assumption.
    /// </para>
    /// <para>
    /// This is a deliberate narrowing of upstream's <c>has()</c>, which checks only the
    /// <c>rtype</c> and carries a documentation warning about exactly this hazard. Here
    /// <see cref="Has{T}"/> is <see langword="true"/> if and only if <see cref="Get{T}"/>
    /// succeeds, so there is no gap between the two for a caller to fall into.
    /// </para>
    /// </remarks>
    public bool Has<T>()
        where T : unmanaged, IRecord<T>
        => T.HasRType((RType)_bytes[1]) && StructSize == T.WireSize;

    /// <summary>
    /// Reinterprets this record as a <typeparamref name="T"/> in place — no copy, no allocation.
    /// </summary>
    /// <typeparam name="T">The record struct to read this record as.</typeparam>
    /// <returns>A read-only reference into the decoder's buffer.</returns>
    /// <exception cref="DbnDecodeException">
    /// This record is not a <typeparamref name="T"/> — see <see cref="Has{T}"/>.
    /// </exception>
    /// <remarks>
    /// The zero-copy accessor, and the one to use on the hot path. Pair it with
    /// <see cref="Has{T}"/> when the record type is not already known, or use
    /// <see cref="TryGet{T}"/> when a copied value is more convenient than a reference.
    /// </remarks>
    public ref readonly T Get<T>()
        where T : unmanaged, IRecord<T>
    {
        if (!Has<T>())
        {
            throw new DbnDecodeException(
                $"This record is not a {typeof(T).Name}: rtype {_bytes[1]} at {StructSize} bytes, " +
                $"where {typeof(T).Name} is {T.WireSize} bytes.");
        }

        return ref MemoryMarshal.AsRef<T>(_bytes);
    }

    /// <summary>
    /// Copies this record out as a <typeparamref name="T"/> if it is one.
    /// </summary>
    /// <typeparam name="T">The record struct to read this record as.</typeparam>
    /// <param name="record">
    /// Receives a copy of the record, or <see langword="default"/> when this is not a
    /// <typeparamref name="T"/>.
    /// </param>
    /// <returns><see langword="true"/> if this record is a <typeparamref name="T"/>.</returns>
    /// <remarks>
    /// The counterpart of upstream's <c>try_get()</c>: a type mismatch is an ordinary, expected
    /// outcome on a mixed-schema stream, so it is reported rather than thrown. Unlike
    /// <see cref="Get{T}"/> this copies the struct onto the caller's stack (at most 528 bytes,
    /// never a heap allocation), which frees the value from the buffer's lifetime; prefer
    /// <see cref="Has{T}"/> plus <see cref="Get{T}"/> where that copy is not wanted.
    /// </remarks>
    public bool TryGet<T>(out T record)
        where T : unmanaged, IRecord<T>
    {
        if (!Has<T>())
        {
            record = default;
            return false;
        }

        record = MemoryMarshal.AsRef<T>(_bytes);
        return true;
    }

    [Conditional("DEBUG")]
    private static unsafe void AssertAligned(ReadOnlySpan<byte> bytes)
    {
        // Mirrors upstream's `debug_assert_eq!(raw_ptr.align_offset(align_of::<RecordHeader>()),
        // 0)` (record_ref.rs:109-114). Records are reinterpreted in place, so a misaligned buffer
        // is either a fault or a torn read on a machine that cares — and silently fine on x64,
        // which is what makes it worth asserting rather than hoping.
        ref var origin = ref MemoryMarshal.GetReference(bytes);
        var address = (nint)Unsafe.AsPointer(ref origin);
        Debug.Assert(
            address % sizeof(ulong) == 0,
            "Unaligned buffer passed to RecordRef: DBN records are reinterpreted in place and require 8-byte alignment.");
    }
}
