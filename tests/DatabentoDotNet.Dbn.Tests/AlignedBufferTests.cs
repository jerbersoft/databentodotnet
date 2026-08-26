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
    public void Consume_PerformsNoCopy_ProvenByUnchangedAddressAndIndices()
    {
        var buffer = new AlignedBuffer(DbnConstants.MaxRecordLength);
        "abcdefghijkl"u8.CopyTo(buffer.Space);
        buffer.Fill(12);

        // The byte at logical offset 10 ('k') will still be readable after consuming 10 bytes.
        // Capture ITS PHYSICAL ADDRESS before consuming: if Consume ever copied bytes (a shift
        // in disguise, or a reallocation), that address would change. If it only moves the
        // position index, the physical byte never moves and this address is identical after.
        var addressOfKBefore = AddressOf(buffer.Data.Slice(10, 1));
        var capacityBefore = buffer.Capacity;
        var availableSpaceBefore = buffer.AvailableSpace;

        var consumed = buffer.Consume(10);

        // Index proof: `end` -- and therefore AvailableSpace and Capacity -- is untouched; only
        // `position` moved, by exactly the amount requested.
        Assert.Equal(10, consumed);
        Assert.Equal(2, buffer.AvailableData);
        Assert.Equal(availableSpaceBefore, buffer.AvailableSpace);
        Assert.Equal(capacityBefore, buffer.Capacity);

        // Backing-storage proof: the physical address of the surviving byte 'k' -- now the
        // first byte of Data -- is bit-for-bit the same address as before. A memmove (or a
        // reallocation) would have moved it; an index-only Consume cannot.
        var addressOfKAfter = AddressOf(buffer.Data.Slice(0, 1));
        Assert.Equal(addressOfKBefore, addressOfKAfter);
        Assert.Equal((byte)'k', buffer.Data[0]);
    }

    /// <summary>
    /// Returns the physical address of <paramref name="span"/>'s first byte, pinning the
    /// backing array for the duration of the measurement via <c>fixed</c>.
    /// </summary>
    /// <remarks>
    /// Pinning is load-bearing, not decorative. Taking the address of managed memory without
    /// <c>fixed</c> is only meaningful for the instant it is computed -- nothing stops the GC
    /// from relocating the object around that computation, so an "aligned" result from an
    /// unpinned read would prove nothing beyond having gotten lucky this run. <c>fixed</c> pins
    /// the object for the whole measurement, so the address returned here is the true, stable
    /// address of the live backing store -- the same address a real reinterpret-cast consumer
    /// (<c>MemoryMarshal.AsRef&lt;T&gt;</c>) would use.
    /// </remarks>
    private static unsafe nint AddressOf(ReadOnlySpan<byte> span)
    {
        fixed (byte* pointer = span)
        {
            return (nint)pointer;
        }
    }
}
