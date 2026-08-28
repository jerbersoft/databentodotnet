namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Guards <see cref="AlignedBuffer"/>: the 8-byte alignment guarantee that makes
/// <c>MemoryMarshal.AsRef&lt;T&gt;</c> sound over its byte view, and the index-only
/// <see cref="AlignedBuffer.Consume"/>/<see cref="AlignedBuffer.Fill"/> contract that keeps the
/// memmove in <see cref="AlignedBuffer.Shift"/>/<see cref="AlignedBuffer.ShiftForSpace"/> an
/// explicit, opt-in cost rather than a hidden per-record one.
/// </summary>
public class AlignedBufferTests
{
    // -----------------------------------------------------------------------------------------
    // Alignment: proven with a pinned pointer, not inferred from the requested capacity.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]        // below the floor -> floors to MaxRecordLength (528, already a multiple of 8)
    [InlineData(528)]      // exactly the floor
    [InlineData(529)]      // not a multiple of 8 -> rounds up to 536
    [InlineData(601)]      // not a multiple of 8 -> rounds up to 608
    [InlineData(65536)]    // the default capacity, already a multiple of 8
    [InlineData(70003)]    // not a multiple of 8 -> rounds up to 70008
    public void Space_StartsAtAn8ByteAlignedAddress_AcrossCapacities(int requestedCapacity)
    {
        var buffer = new AlignedBuffer(requestedCapacity);

        // Capacity being a multiple of 8 proves nothing about ADDRESS alignment by itself -- it
        // only proves the byte count is a multiple of 8. The claim this class exists to make is
        // about where the bytes physically live, so the array is pinned (via AddressOf's `fixed`)
        // and the resulting pointer is inspected directly.
        //
        // Inspecting it after the pin has been released is sound HERE, unlike the paired
        // comparisons further down: relocation cannot break alignment, because every address a
        // ulong[] is ever given is 8-byte aligned. A single address is being tested for a
        // property, not against another address. See the remarks on AddressOf.
        var address = AddressOf(buffer.Space);

        Assert.Equal(0, (int)(address % 8));
        Assert.Equal(0, buffer.Capacity % 8);
        Assert.True(buffer.Capacity >= DbnConstants.MaxRecordLength);
        Assert.True(buffer.Capacity >= requestedCapacity);
    }

    [Fact]
    public void Data_StartsAtAn8ByteAlignedAddress_AfterShift()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "12345678"u8.CopyTo(buffer.Space);
        buffer.Fill(8);
        buffer.Consume(3); // position is now 3 -- deliberately not 8-aligned

        buffer.Shift();

        var address = AddressOf(buffer.Data);
        Assert.Equal(0, (int)(address % 8));
        Assert.Equal("45678"u8.ToArray(), buffer.Data.ToArray());
    }

    // -----------------------------------------------------------------------------------------
    // Capacity floor and default.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(527)]
    [InlineData(528)]
    public void Constructor_CapacityAtOrBelowMaxRecordLength_FloorsToMaxRecordLength(int requested)
    {
        var buffer = new AlignedBuffer(requested);

        Assert.Equal(DbnConstants.MaxRecordLength, buffer.Capacity);
    }

    [Fact]
    public void Constructor_NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlignedBuffer(-1));
    }

    [Fact]
    public void Constructor_Parameterless_UsesDefaultCapacity()
    {
        var buffer = new AlignedBuffer();

        Assert.Equal(AlignedBuffer.DefaultCapacity, buffer.Capacity);
        Assert.Equal(64 * 1024, buffer.Capacity);
    }

    // -----------------------------------------------------------------------------------------
    // fill / consume / fill, with no shift anywhere -- the index-only hot path.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void FillConsumeFill_WithoutShift_MovesIndicesOnly()
    {
        // Mirrors dbn's own aligned_buffer.rs::test_basic_ops, scaled up because AlignedBuffer
        // floors capacity to DbnConstants.MaxRecordLength -- the relative arithmetic is
        // identical, only the starting capacity is bigger.
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        Assert.Equal(0, buffer.AvailableData);
        Assert.Equal(DbnConstants.MaxRecordLength, buffer.AvailableSpace);

        "abcd"u8.CopyTo(buffer.Space);
        buffer.Fill(4);
        Assert.Equal(4, buffer.AvailableData);
        Assert.Equal("abcd"u8.ToArray(), buffer.Data.ToArray());

        buffer.Consume(2);
        Assert.Equal(2, buffer.AvailableData);
        Assert.Equal("cd"u8.ToArray(), buffer.Data.ToArray());

        // Fill again -- still no shift anywhere in this test.
        "ef"u8.CopyTo(buffer.Space);
        buffer.Fill(2);
        Assert.Equal(4, buffer.AvailableData);
        Assert.Equal("cdef"u8.ToArray(), buffer.Data.ToArray());
        Assert.Equal(DbnConstants.MaxRecordLength - 6, buffer.AvailableSpace);
    }

    // -----------------------------------------------------------------------------------------
    // Shift.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Shift_PreservesUnconsumedBytes()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "abcdefghijkl"u8.CopyTo(buffer.Space);
        buffer.Fill(12);
        buffer.Consume(10);
        Assert.Equal("kl"u8.ToArray(), buffer.Data.ToArray());

        buffer.Shift();

        Assert.Equal(2, buffer.AvailableData);
        Assert.Equal(DbnConstants.MaxRecordLength - 2, buffer.AvailableSpace);
        Assert.Equal("kl"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void Shift_NoOp_WhenPositionIsZero()
    {
        // The authoritative Rust source guards `shift` with `if self.position > 0`
        // (aligned_buffer.rs) -- contrary to decoder.md's summary, which calls the move
        // "unconditional." The guard has no *observable* effect here (copying [0, end) onto
        // itself is a no-op regardless of the guard), but this exercises the position == 0 path
        // explicitly rather than only ever touching it incidentally through other tests.
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "abcd"u8.CopyTo(buffer.Space);
        buffer.Fill(4);

        buffer.Shift();

        Assert.Equal(4, buffer.AvailableData);
        Assert.Equal("abcd"u8.ToArray(), buffer.Data.ToArray());
    }

    // -----------------------------------------------------------------------------------------
    // ShiftForSpace: shifts only when it must, never grows.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ShiftForSpace_IsNoOp_WhenAvailableSpaceAlreadyMeetsTheRequest()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "abcdefghijkl"u8.CopyTo(buffer.Space);
        buffer.Fill(12);
        buffer.Consume(10);
        var spaceBefore = buffer.AvailableSpace;

        buffer.ShiftForSpace(2);

        Assert.Equal(spaceBefore, buffer.AvailableSpace);
        Assert.Equal("kl"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void ShiftForSpace_Shifts_WhenRequestExceedsAvailableSpaceAndSomethingWasConsumed()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "abcdefghijkl"u8.CopyTo(buffer.Space);
        buffer.Fill(12);
        buffer.Consume(10);
        var spaceBefore = buffer.AvailableSpace; // Capacity - 12

        buffer.ShiftForSpace(spaceBefore + 1);

        // Shifting reclaims exactly the 10 consumed bytes: available space grows by 10.
        Assert.Equal(spaceBefore + 10, buffer.AvailableSpace);
        Assert.Equal("kl"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void ShiftForSpace_NoOp_WhenNothingHasBeenConsumed()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "abcd"u8.CopyTo(buffer.Space);
        buffer.Fill(4);
        var spaceBefore = buffer.AvailableSpace;

        // `needed` vastly exceeds capacity, but position == 0 -- nothing to reclaim, so this
        // must stay a no-op rather than "shifting" already front-aligned data.
        buffer.ShiftForSpace(1_000_000);

        Assert.Equal(spaceBefore, buffer.AvailableSpace);
        Assert.Equal(4, buffer.AvailableData);
        Assert.Equal("abcd"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void ShiftForSpace_NegativeNeeded_Throws()
    {
        var buffer = new AlignedBuffer();

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ShiftForSpace(-1));
    }

    // -----------------------------------------------------------------------------------------
    // Grow.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Grow_ReallocatesAndPreservesBytes_WhenRequestedSizeExceedsCapacity()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "test"u8.CopyTo(buffer.Space);
        buffer.Fill(4);

        var grown = buffer.Grow(DbnConstants.MaxRecordLength * 4);

        Assert.True(grown);
        Assert.True(buffer.Capacity >= DbnConstants.MaxRecordLength * 4);
        Assert.Equal(0, buffer.Capacity % 8);
        Assert.Equal(4, buffer.AvailableData);
        Assert.Equal("test"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void Grow_NoOp_WhenAlreadyLargeEnough()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength * 2);
        var capacityBefore = buffer.Capacity;

        var grown = buffer.Grow(DbnConstants.MaxRecordLength);

        Assert.False(grown);
        Assert.Equal(capacityBefore, buffer.Capacity);
    }

    [Fact]
    public void Grow_NegativeSize_Throws()
    {
        var buffer = new AlignedBuffer();

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Grow(-1));
    }

    // -----------------------------------------------------------------------------------------
    // Reset.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Reset_ClearsIndices_KeepsCapacity()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "data"u8.CopyTo(buffer.Space);
        buffer.Fill(4);
        var capacityBefore = buffer.Capacity;

        buffer.Reset();

        Assert.Equal(0, buffer.AvailableData);
        Assert.Equal(capacityBefore, buffer.Capacity);
        Assert.Equal(capacityBefore, buffer.AvailableSpace);
        Assert.True(buffer.IsEmpty);
    }

    // -----------------------------------------------------------------------------------------
    // Consume / Fill clamping and validation.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Consume_ClampsToAvailableData_AndReturnsActualCount()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "ab"u8.CopyTo(buffer.Space);
        buffer.Fill(2);

        var consumed = buffer.Consume(1_000);

        Assert.Equal(2, consumed);
        Assert.Equal(0, buffer.AvailableData);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Fill_ClampsToAvailableSpace_AndReturnsActualCount()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);

        var filled = buffer.Fill(1_000_000);

        Assert.Equal(DbnConstants.MaxRecordLength, filled);
        Assert.Equal(DbnConstants.MaxRecordLength, buffer.AvailableData);
        Assert.Equal(0, buffer.AvailableSpace);
    }

    [Fact]
    public void Consume_NegativeCount_Throws()
    {
        var buffer = new AlignedBuffer();

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Consume(-1));
    }

    [Fact]
    public void Fill_NegativeCount_Throws()
    {
        var buffer = new AlignedBuffer();

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Fill(-1));
    }

    // -----------------------------------------------------------------------------------------
    // IsEmpty and DataMut.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void IsEmpty_TracksPositionEqualsEnd()
    {
        var buffer = new AlignedBuffer();
        Assert.True(buffer.IsEmpty);

        "x"u8.CopyTo(buffer.Space);
        buffer.Fill(1);
        Assert.False(buffer.IsEmpty);

        buffer.Consume(1);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void DataMut_WritesAreVisibleThroughData()
    {
        var buffer = new AlignedBuffer();
        "xy"u8.CopyTo(buffer.Space);
        buffer.Fill(2);

        buffer.DataMut[0] = (byte)'Z';

        Assert.Equal((byte)'Z', buffer.Data[0]);
    }

    // -----------------------------------------------------------------------------------------
    // The load-bearing proof: Consume performs no copy.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public unsafe void Consume_PerformsNoCopy_ProvenByUnchangedAddressAndIndices()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "abcdefghijkl"u8.CopyTo(buffer.Space);
        buffer.Fill(12);

        var capacityBefore = buffer.Capacity;
        var availableSpaceBefore = buffer.AvailableSpace;

        // Held for the whole measurement, because two addresses are about to be compared. See
        // the remarks on AddressOf: outside a pin, the GC may relocate the array between the two
        // reads and this test would then fail for a reason that has nothing to do with Consume.
        fixed (byte* held = buffer.Data)
        {
            // The byte at logical offset 10 ('k') will still be readable after consuming 10
            // bytes. Capture ITS PHYSICAL ADDRESS before consuming: if Consume ever copied bytes
            // (a shift in disguise, or a reallocation), that address would change. If it only
            // moves the position index, the physical byte never moves and this address is
            // identical after.
            var addressOfKBefore = (nint)held + 10;

            var consumed = buffer.Consume(10);

            // Index proof: `end` -- and therefore AvailableSpace and Capacity -- is untouched;
            // only `position` moved, by exactly the amount requested.
            Assert.Equal(10, consumed);
            Assert.Equal(2, buffer.AvailableData);
            Assert.Equal(availableSpaceBefore, buffer.AvailableSpace);
            Assert.Equal(capacityBefore, buffer.Capacity);

            CompactTheHeap();

            // Backing-storage proof: the physical address of the surviving byte 'k' -- now the
            // first byte of Data -- is bit-for-bit the same address as before. A memmove (or a
            // reallocation) would have moved it; an index-only Consume cannot.
            var addressOfKAfter = AddressOf(buffer.Data.Slice(0, 1));
            Assert.Equal(addressOfKBefore, addressOfKAfter);
            Assert.Equal((byte)'k', buffer.Data[0]);
        }
    }

    // -----------------------------------------------------------------------------------------
    // SpaceMemory: the async read seam (#15). Span and Memory must be two views of one array,
    // not two arrays that usually agree.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public unsafe void SpaceMemory_PinsToTheSameAddressAsSpace_AndIs8ByteAligned()
    {
        var buffer = new AlignedBuffer();

        // This is the whole claim of the seam: the pointer an async read receives is the aligned
        // buffer itself, not a detached view that gets copied back afterwards. Anything short of
        // comparing the pinned address to the span's address would leave that unproven.
        //
        // The pin comes first and outlives both reads, which is what makes comparing them legal
        // -- this test needs no `fixed` region of its own, because Pin() already is one.
        //
        // And the collection is what makes it prove that. Pin() returning the array's address is
        // half the contract; HOLDING the array there is the other half, and an async read into a
        // buffer the GC may move underneath it is memory corruption, not a wrong answer. Measured
        // both ways while repairing #43: a Pin() that computes the right address and then frees
        // its handle passes this test without the line below, and fails with it.
        using var pin = buffer.SpaceMemory.Pin();
        var pinned = (nint)pin.Pointer;

        CompactTheHeap();

        Assert.Equal(AddressOf(buffer.Space), pinned);
        Assert.Equal(0, (int)(pinned % 8));
    }

    [Fact]
    public unsafe void SpaceMemory_PinsAtTheWriteOffset_NotAtTheStartOfTheBuffer()
    {
        var buffer = new AlignedBuffer();

        // `end` is still 0, so Space starts at the array's base and `held` is the address every
        // assertion below is relative to. Taking it inside the `fixed` region rather than before
        // it is the whole repair from #43: the earlier version read this address, then pinned,
        // then asserted a 24-byte relationship between two addresses the GC was free to have
        // separated by a whole heap segment in between.
        fixed (byte* held = buffer.Space)
        {
            var start = (nint)held;
            buffer.Fill(24);

            CompactTheHeap();

            using var pin = buffer.SpaceMemory.Pin();

            Assert.Equal(start + 24, (nint)pin.Pointer);
        }
    }

    [Fact]
    public void SpaceMemory_WritesLandInData_ExactlyAsSpaceWritesDo()
    {
        var buffer = new AlignedBuffer();

        "async"u8.CopyTo(buffer.SpaceMemory.Span);
        buffer.Fill(5);
        "sync"u8.CopyTo(buffer.Space);
        buffer.Fill(4);

        Assert.Equal(buffer.Space.Length, buffer.SpaceMemory.Length);
        Assert.Equal("asyncsync"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public unsafe void SpaceMemory_TakenBeforeGrow_ResolvesToTheGrownArray()
    {
        // The memory manager holds the buffer, not the array, precisely so this works: Grow
        // replaces the storage, and a Memory handed out beforehand must follow it. A manager that
        // captured the array would write into the abandoned one and lose the bytes in silence.
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        var beforeGrow = buffer.SpaceMemory;

        Assert.True(buffer.Grow(DbnConstants.MaxRecordLength * 4));

        // The hold is taken AFTER the growth, and it has to be: Grow replaces the array
        // outright, so pinning beforehand would hold the abandoned one in place while the new
        // array -- the one both addresses below resolve to -- stayed free to move.
        fixed (byte* held = buffer.Space)
        {
            CompactTheHeap();

            Assert.Equal((nint)held, AddressOf(beforeGrow.Span));
        }

        "grown"u8.CopyTo(beforeGrow.Span);
        buffer.Fill(5);
        Assert.Equal("grown"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void SpaceMemory_OnAFullBuffer_IsEmptyAndStillPinnable()
    {
        // A full buffer's tail starts one past its last byte, and Memory.Pin forwards that index
        // to the manager. An off-by-one bounds check there turns an ordinary "the buffer is full"
        // read into an ArgumentOutOfRangeException from inside the socket stack.
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        buffer.Fill(buffer.Capacity);

        var tail = buffer.SpaceMemory;
        Assert.Equal(0, tail.Length);

        using var pin = tail.Pin();
    }

    /// <summary>
    /// Returns the physical address of <paramref name="span"/>'s first byte, pinning the backing
    /// array via <c>fixed</c> for as long as it takes to read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinning is load-bearing, not decorative. Taking the address of managed memory without
    /// <c>fixed</c> is only meaningful for the instant it is computed -- nothing stops the GC
    /// from relocating the object around that computation, so an "aligned" result from an
    /// unpinned read would prove nothing beyond having gotten lucky this run. Inside the
    /// <c>fixed</c> region below, the address is the true address of the live backing store --
    /// the same one a real reinterpret-cast consumer (<c>MemoryMarshal.AsRef&lt;T&gt;</c>) would
    /// use.
    /// </para>
    /// <para>
    /// <b>The pin ends when this method returns, and the returned value outlives it.</b> That
    /// makes the result safe to assert a property <em>of</em> -- alignment survives relocation,
    /// because every address a <c>ulong[]</c> can occupy is 8-byte aligned -- and unsafe to
    /// compare against a second address taken outside the same pin. #43 was exactly that: an
    /// address read here, the array relocated by a collection before the second read, and an
    /// assertion comparing the old location against the new one. One failure in eight full
    /// solution runs, none in fourteen narrower ones. <b>Two addresses are comparable only when
    /// both are read while the same pin is held</b>, which is why every test above that compares
    /// a pair opens its own <c>fixed</c> region around the whole measurement instead of calling
    /// this twice.
    /// </para>
    /// </remarks>
    /// <param name="span">The span whose first byte's address is wanted.</param>
    /// <returns>The address, valid only for as long as the array stays where it was.</returns>
    private static unsafe nint AddressOf(ReadOnlySpan<byte> span)
    {
        fixed (byte* pointer = span)
        {
            return (nint)pointer;
        }
    }

    /// <summary>
    /// Forces a blocking, compacting gen-2 collection -- the event an address comparison has to
    /// survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the <c>fixed</c> regions that repair #43 would be decorative: deleting one
    /// would leave every assertion passing on almost every run, which is the state the bug was
    /// found in. With it, an address read outside the pin is compared against an array that has
    /// actually moved, so the repair is checked rather than assumed. Measured while writing #43,
    /// on a buffer at <see cref="AlignedBuffer.DefaultCapacity"/>: the unpinned array relocated
    /// on 5 of 5 forced collections, by several megabytes each time.
    /// </para>
    /// <para>
    /// <b>Nothing asserts that it moved</b>, and nothing should. The GC is under no obligation to
    /// relocate any particular object, so a run where the array stays put simply proves less --
    /// it can never turn this into a failure, which is the property that separates this from the
    /// flake it replaces.
    /// </para>
    /// </remarks>
    private static void CompactTheHeap() =>
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
}
