using System.Runtime.CompilerServices;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Guards the DBN wire layout.
/// </summary>
/// <remarks>
/// The expected sizes are the ones <c>databento-cpp</c> pins with <c>static_assert</c> against
/// the Rust reference implementation. Because records are reinterpreted directly over the read
/// buffer, a layout mistake is silent data corruption rather than an exception — these
/// assertions are what turn it back into a build failure.
/// </remarks>
public class RecordLayoutTests
{
    [Fact]
    public void RecordHeader_MatchesWireSize()
    {
        Assert.Equal(16, Unsafe.SizeOf<RecordHeader>());
    }

    [Fact]
    public void MaxRecordLength_CoversLargestRecordPlusTsOut()
    {
        // InstrumentDefMsg (520) + ts_out (8). The read buffer is sized off this.
        Assert.Equal(528, DbnConstants.MaxRecordLength);
    }

    [Fact]
    public void RecordHeader_LengthIsExpressedIn32BitWords()
    {
        // A 56-byte MboMsg is encoded as length=14.
        var header = CreateHeader(length: 14);
        Assert.Equal(56, header.SizeInBytes);
    }

    private static RecordHeader CreateHeader(byte length)
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<RecordHeader>()];
        bytes.Clear();
        bytes[0] = length;
        return System.Runtime.InteropServices.MemoryMarshal.Read<RecordHeader>(bytes);
    }
}
