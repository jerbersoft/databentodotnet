using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A read/write byte buffer backed by <c>ulong[]</c>, guaranteeing that its byte view starts
/// 8-byte aligned so records can later be reinterpreted in place over it.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the Rust <c>dbn</c> crate's <c>AlignedBuffer</c>
/// (<c>decode/dbn/aligned_buffer.rs</c>), itself forked from the <c>oval</c> ring-buffer crate.
/// Rust gets 8-byte alignment from <c>Box&lt;[u64]&gt;</c>; the direct .NET analogue is
/// <c>ulong[]</c> viewed as bytes through <see cref="MemoryMarshal.AsBytes{T}(Span{T})"/>. Every
/// array's element storage is aligned to at least its element type's own alignment, so a
/// <c>ulong[]</c>'s byte view starts 8-byte aligned by construction. A <c>byte[]</c> carries no
/// such guarantee in .NET — using one here would be silently wrong on x64 today (it happens to
/// work) and a crash or a torn read on an architecture that faults on misaligned access.
/// </para>
/// <para>
/// <b>Shifts are explicit.</b> <see cref="Consume"/> and <see cref="Fill"/> move only the
/// internal <c>position</c>/<c>end</c> indices — neither ever copies a byte. Callers reclaim the
/// consumed prefix by calling <see cref="Shift"/> or <see cref="ShiftForSpace"/> at a point of
/// their choosing (typically a refill boundary), so the one operation that actually moves memory
/// is paid there and is visible in a profile, rather than hidden inside every record read.
/// </para>
/// <para>
/// Invariant, maintained by every member: <c>0 &lt;= position &lt;= end &lt;= Capacity</c>.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification =
        "The disposable field is the ByteView memory manager, and MemoryManager<T> is IDisposable "
        + "only because managers over native memory need to be. This one projects a managed "
        + "ulong[] the buffer already owns and its Dispose is empty, so there is nothing to "
        + "release. Honouring the rule would make AlignedBuffer IDisposable, which forces "
        + "IDisposable onto DbnFsm, DbnDecoder and the M2 live client in turn — a contract "
        + "promising cleanup that never happens, spread across the whole public surface.")]
public sealed class AlignedBuffer
{
    /// <summary>The capacity used when none is supplied: 64 KiB.</summary>
    /// <remarks>
    /// Matches upstream's <c>DbnFsm::DEFAULT_BUF_SIZE</c> (<c>fsm.rs:105</c>) — the default main
    /// buffer size the incremental decoder allocates.
    /// </remarks>
    public const int DefaultCapacity = 64 * 1024;

    private readonly ByteView _byteView;

    private ulong[] _memory;
    private int _position;
    private int _end;

    /// <summary>Allocates a buffer with <see cref="DefaultCapacity"/> usable bytes.</summary>
    public AlignedBuffer()
        : this(DefaultCapacity)
    {
    }

    /// <summary>Allocates a buffer with at least <paramref name="capacity"/> usable bytes.</summary>
    /// <remarks>
    /// The actual capacity is rounded up to a multiple of 8 — so the backing <c>ulong[]</c> has
    /// no partial trailing element — and is never allowed below
    /// <see cref="DbnConstants.MaxRecordLength"/>: a buffer smaller than the largest possible
    /// record could never hand the decoder a complete record's worth of contiguous space, no
    /// matter how it is shifted. This floor is a .NET-side addition, not something upstream
    /// enforces: <c>AlignedBuffer::with_capacity</c> allocates exactly what is asked, leaving the
    /// floor to be upheld by callers — the DbnFsm builder docs merely warn that a too-small
    /// buffer "must be at least the size of the largest record" for the zero-copy path to work,
    /// they do not clamp it. Enforcing the floor here removes that foot-gun for every caller at
    /// no cost, since real callers never want a buffer smaller than this anyway.
    /// </remarks>
    /// <param name="capacity">The minimum number of usable bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public AlignedBuffer(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must not be negative.");
        }

        var floored = Math.Max(capacity, DbnConstants.MaxRecordLength);
        _memory = new ulong[ToUlongLength(floored)];
        _byteView = new ByteView(this);
    }

    /// <summary>The currently readable bytes: the slice from <c>position</c> to <c>end</c>.</summary>
    public ReadOnlySpan<byte> Data => DataMut;

    /// <summary>A mutable view of the currently readable bytes.</summary>
    public Span<byte> DataMut => AsBytes().Slice(_position, _end - _position);

    /// <summary>
    /// The writable tail: the slice from <c>end</c> to <see cref="Capacity"/>. Does not shift —
    /// call <see cref="ShiftForSpace"/> or <see cref="Shift"/> first for more contiguous room.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="SpaceMemory"/> rather than sliced independently. The two describe
    /// the same bytes, and a caller that mixes them — a synchronous read here, an asynchronous
    /// read there — must be able to rely on that; deriving one from the other makes it true by
    /// construction instead of by a test that has to remember to check.
    /// </remarks>
    public Span<byte> Space => SpaceMemory.Span;

    /// <summary>
    /// The writable tail as a <see cref="Memory{T}"/>: exactly the bytes <see cref="Space"/>
    /// describes, in the form asynchronous I/O requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> <c>stream.ReadAsync(buffer.Space)</c> does not compile:
    /// there is no <c>ReadAsync(Span&lt;byte&gt;)</c> overload in .NET, and there cannot be, since
    /// a <c>ref struct</c> may not be preserved across an <c>await</c>. Asynchronous reads take a
    /// <c>Memory&lt;byte&gt;</c> — which has no obvious spelling here either: the backing store is
    /// a <c>ulong[]</c> for alignment (see the remarks on <see cref="AlignedBuffer"/>),
    /// <see cref="MemoryMarshal.Cast{TFrom, TTo}(Span{TFrom})"/> reinterprets spans only, and the
    /// BCL has no <c>Memory</c> equivalent. A <see cref="MemoryManager{T}"/> is the sanctioned way
    /// to project a byte view over storage the BCL cannot type-pun for you, and that is what backs
    /// this property.
    /// </para>
    /// <para>
    /// <b>The projection is live, not a snapshot.</b> The manager resolves the array on every
    /// call rather than caching it, so a <see cref="Memory{T}"/> taken before a
    /// <see cref="Grow"/> still resolves to the grown array afterwards — at the same byte
    /// offsets, which <see cref="Grow"/> preserves. A manager that captured the array instead
    /// would silently keep writing into the abandoned one.
    /// </para>
    /// <para>
    /// <b>An outstanding pin is a different matter.</b> Pinning happens per I/O operation, and a
    /// <see cref="System.Buffers.MemoryHandle"/> taken before a <see cref="Grow"/> points into
    /// the old array for as long as it lives. Do not grow the buffer while an asynchronous read
    /// into it is in flight. Nothing in this codec does: growth happens only while decoding the
    /// metadata block, between reads, on the one thread that owns the buffer.
    /// </para>
    /// </remarks>
    public Memory<byte> SpaceMemory => _byteView.Memory.Slice(_end, Capacity - _end);

    /// <summary>The number of bytes currently available to read.</summary>
    public int AvailableData => _end - _position;

    /// <summary>The number of bytes currently available to write.</summary>
    public int AvailableSpace => Capacity - _end;

    /// <summary>The buffer's total byte capacity.</summary>
    public int Capacity => _memory.Length * sizeof(ulong);

    /// <summary><see langword="true"/> when there is no readable data (<c>position == end</c>).</summary>
    public bool IsEmpty => _position == _end;

    /// <summary>
    /// Advances the read position by <paramref name="count"/> bytes, capped to
    /// <see cref="AvailableData"/>. Moves the <c>position</c> index only — never copies a byte.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>The number of bytes actually consumed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public int Consume(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must not be negative.");
        }

        var actual = Math.Min(count, AvailableData);
        _position += actual;
        return actual;
    }

    /// <summary>
    /// Marks <paramref name="count"/> bytes (capped to <see cref="AvailableSpace"/>) as written.
    /// The caller must already have written them into <see cref="Space"/>. Moves the <c>end</c>
    /// index only — never copies a byte.
    /// </summary>
    /// <param name="count">The number of bytes to mark as filled.</param>
    /// <returns>The number of bytes actually filled.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public int Fill(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must not be negative.");
        }

        var actual = Math.Min(count, AvailableSpace);
        _end += actual;
        return actual;
    }

    /// <summary>
    /// Reclaims the consumed prefix by calling <see cref="Shift"/>, but only if
    /// <see cref="AvailableSpace"/> is currently less than <paramref name="needed"/> and there is
    /// something to reclaim (<c>position &gt; 0</c>); a no-op otherwise. Never grows the buffer.
    /// </summary>
    /// <param name="needed">The number of contiguous writable bytes the caller wants.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="needed"/> is negative.</exception>
    public void ShiftForSpace(int needed)
    {
        if (needed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(needed), needed, "Needed must not be negative.");
        }

        if (AvailableSpace < needed && _position > 0)
        {
            Shift();
        }
    }

    /// <summary>
    /// Grows the backing storage to at least <paramref name="newSize"/> bytes (rounded up to a
    /// multiple of 8), preserving every existing byte at its current offset. <c>position</c> and
    /// <c>end</c> are unchanged.
    /// </summary>
    /// <param name="newSize">The minimum number of usable bytes after growth.</param>
    /// <returns>
    /// <see langword="true"/> if the buffer was reallocated; <see langword="false"/> if it was
    /// already at least this large, in which case nothing changed.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="newSize"/> is negative.</exception>
    public bool Grow(int newSize)
    {
        if (newSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newSize), newSize, "New size must not be negative.");
        }

        if (Capacity >= newSize)
        {
            return false;
        }

        var newMemory = new ulong[ToUlongLength(newSize)];
        AsBytes().CopyTo(MemoryMarshal.AsBytes<ulong>(newMemory.AsSpan()));
        _memory = newMemory;
        return true;
    }

    /// <summary>Resets <c>position</c> and <c>end</c> to 0. Keeps the allocated capacity.</summary>
    public void Reset()
    {
        _position = 0;
        _end = 0;
    }

    /// <summary>
    /// Moves the unconsumed bytes (<c>[position, end)</c>) down to offset 0, then sets
    /// <c>position</c> to 0 and <c>end</c> to the moved length. A no-op when <c>position</c> is
    /// already 0. This is the only member of <see cref="AlignedBuffer"/> that copies memory.
    /// </summary>
    public void Shift()
    {
        if (_position <= 0)
        {
            return;
        }

        var length = _end - _position;
        var bytes = AsBytes();
        bytes.Slice(_position, length).CopyTo(bytes);
        _position = 0;
        _end = length;
    }

    private Span<byte> AsBytes() => MemoryMarshal.AsBytes<ulong>(_memory.AsSpan());

    // The round-up is done in 64-bit on purpose. `(byteCapacity + 7)` wraps negative for the top
    // seven int values, and the negative quotient then reaches `new ulong[...]` as an
    // OverflowException — a failure mode that has nothing to do with the caller's mistake and
    // that no `catch (DbnException)` in the decoder would ever see. Widening makes the result
    // always the true ceiling; a capacity the machine genuinely cannot satisfy then fails as an
    // ordinary allocation failure instead.
    private static int ToUlongLength(int byteCapacity) => (int)(((long)byteCapacity + 7) / sizeof(ulong));

    /// <summary>
    /// Projects the owning buffer's <c>ulong[]</c> as a <see cref="Memory{T}"/> of bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The BCL can reinterpret a <see cref="Span{T}"/> across element types and cannot do the
    /// same for a <see cref="Memory{T}"/>, so this is the only way to hand an asynchronous read
    /// the aligned buffer directly instead of a <c>byte[]</c> it would have to copy out of. See
    /// the remarks on <see cref="AlignedBuffer.SpaceMemory"/>.
    /// </para>
    /// <para>
    /// <b>It holds the buffer, not the array.</b> Every member resolves <c>_owner._memory</c> at
    /// call time, so <see cref="AlignedBuffer.Grow"/> replacing the array cannot leave this view
    /// pointing at the abandoned one.
    /// </para>
    /// </remarks>
    private sealed class ByteView : MemoryManager<byte>
    {
        private readonly AlignedBuffer _owner;

        public ByteView(AlignedBuffer owner) => _owner = owner;

        public override Span<byte> GetSpan() => _owner.AsBytes();

        /// <summary>
        /// Pins the backing array and returns a pointer <paramref name="elementIndex"/> bytes
        /// into it, so an asynchronous read can write straight into the buffer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="elementIndex"/> is a byte offset into the whole buffer, not into the
        /// slice being pinned: <see cref="Memory{T}.Pin"/> forwards the slice's start index, and
        /// a slice of length zero starting at the very end of the buffer is legal — an
        /// asynchronous read of a full buffer's empty tail pins exactly that. Hence <c>&gt;</c>
        /// rather than <c>&gt;=</c> in the bounds check.
        /// </para>
        /// <para>
        /// The <see cref="GCHandle"/> is handed to the <see cref="MemoryHandle"/> and no
        /// <see cref="IPinnable"/> alongside it, so disposing the handle frees the pin and
        /// <see cref="Unpin"/> is never reached through that path. Keeping the pin in the handle
        /// rather than in a field is what makes overlapping pins safe; a single
        /// <c>_pinHandle</c> field would have the second pin overwrite the first and leak it.
        /// </para>
        /// </remarks>
        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            var array = _owner._memory;
            var byteLength = array.Length * sizeof(ulong);
            if ((uint)elementIndex > (uint)byteLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elementIndex), elementIndex, "Element index is outside the buffer.");
            }

            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            return new MemoryHandle((byte*)handle.AddrOfPinnedObject() + elementIndex, handle);
        }

        /// <summary>
        /// Unreachable by construction: <see cref="Pin"/> puts the pin in the
        /// <see cref="MemoryHandle"/>'s own <see cref="GCHandle"/> and registers no
        /// <see cref="IPinnable"/>, so <see cref="MemoryHandle.Dispose"/> frees the pin without
        /// coming back here. Implemented as a no-op rather than as a throw because the abstract
        /// member has to exist and a throw would turn a harmless direct call into a crash.
        /// </summary>
        public override void Unpin()
        {
        }

        /// <summary>
        /// Nothing to release: the storage is a managed array owned by the buffer, and the pin
        /// lives in the <see cref="MemoryHandle"/> the caller disposes.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
        }
    }
}
