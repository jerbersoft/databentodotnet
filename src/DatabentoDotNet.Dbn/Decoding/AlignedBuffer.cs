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
public sealed class AlignedBuffer
{
    /// <summary>The capacity used when none is supplied: 64 KiB.</summary>
    /// <remarks>
    /// Matches upstream's <c>DbnFsm::DEFAULT_BUF_SIZE</c> (<c>fsm.rs:105</c>) — the default main
    /// buffer size the incremental decoder allocates.
    /// </remarks>
    public const int DefaultCapacity = 64 * 1024;

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
    }

    /// <summary>The currently readable bytes: the slice from <c>position</c> to <c>end</c>.</summary>
    public ReadOnlySpan<byte> Data => DataMut;

    /// <summary>A mutable view of the currently readable bytes.</summary>
    public Span<byte> DataMut => AsBytes().Slice(_position, _end - _position);

    /// <summary>
    /// The writable tail: the slice from <c>end</c> to <see cref="Capacity"/>. Does not shift —
    /// call <see cref="ShiftForSpace"/> or <see cref="Shift"/> first for more contiguous room.
    /// </summary>
    public Span<byte> Space => AsBytes().Slice(_end, Capacity - _end);

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

    private static int ToUlongLength(int byteCapacity) => (byteCapacity + 7) / sizeof(ulong);
}
