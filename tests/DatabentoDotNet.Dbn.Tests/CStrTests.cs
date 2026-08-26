using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Guards the fixed C-string fields: what they decode to, and — the part that matters for the
/// library's reason to exist — when they decode at all.
/// </summary>
public class CStrTests
{
    [Fact]
    public void CStr_DropsTheNulPadding_WhenTheSymbolIsShorterThanTheField()
    {
        var field = FieldOf("ESH4"u8, CStr71.Length);

        Assert.Equal("ESH4", field.ToString());
        Assert.Equal(4, field.AsTextSpan().Length);

        // The raw view keeps every wire byte, padding included; only the text view trims.
        Assert.Equal(71, field.AsSpan().Length);
        Assert.Equal(0, field.AsSpan()[4]);
    }

    [Fact]
    public void CStr_DecodesEveryByte_WhenTheSymbolExactlyFillsTheField()
    {
        // 71 characters, leaving no room for a terminator. Upstream's Rust helper rejects this
        // case outright; the port returns the whole field, which is what the bytes actually say.
        var symbol = new string('A', CStr71.Length);
        var bytes = System.Text.Encoding.ASCII.GetBytes(symbol);
        Assert.Equal(CStr71.Length, bytes.Length);

        var field = FieldOf(bytes, CStr71.Length);

        Assert.Equal(symbol, field.ToString());
        Assert.Equal(CStr71.Length, field.AsTextSpan().Length);
    }

    [Fact]
    public void CStr_DecodesToAnEmptyString_WhenTheFieldIsAllNul()
    {
        var field = default(CStr71);

        Assert.Equal(string.Empty, field.ToString());
        Assert.Equal(0, field.AsTextSpan().Length);
        Assert.Equal(71, field.AsSpan().Length);
    }

    [Fact]
    public void CStr_StopsAtTheFirstNul_NotTheLast()
    {
        // A field that was written twice, the second symbol shorter than the first. Everything
        // past the first terminator is stale and must not surface.
        Span<byte> raw = stackalloc byte[CStr71.Length];
        raw.Clear();
        "AB"u8.CopyTo(raw);
        "STALE"u8.CopyTo(raw[10..]);

        var field = MemoryMarshal.Read<CStr71>(raw);

        Assert.Equal("AB", field.ToString());
    }

    [Fact]
    public void CStr_ExpressesBothTheV1AndTheV2SymbolWidths()
    {
        Assert.Equal(DbnConstants.SymbolCstrLengthV1, CStr22.Length);
        Assert.Equal(DbnConstants.SymbolCstrLength, CStr71.Length);
        Assert.Equal(22, Unsafe.SizeOf<CStr22>());
        Assert.Equal(71, Unsafe.SizeOf<CStr71>());
    }

    [Fact]
    public void CStr_AllocatesNothingUntilAskedForAString()
    {
        // Decoding a record must not allocate per record — that is the whole zero-copy premise.
        // Reinterpreting the buffer and reading the symbol back as bytes is the path a decoder
        // takes; ToString is the opt-in that costs something.
        var buffer = new byte[SymbolMappingMsg.WireSize];
        buffer[17] = (byte)'E';
        buffer[18] = (byte)'S';
        buffer[19] = (byte)'H';
        buffer[20] = (byte)'4';

        // Warm up: first-call JIT work allocates and would swamp the measurement.
        for (var i = 0; i < 200; i++)
        {
            _ = SymbolLength(buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var i = 0; i < 200; i++)
        {
            total += SymbolLength(buffer);
        }

        var allocatedWhileReading = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(800, total);
        Assert.Equal(0, allocatedWhileReading);

        // And the contrast: asking for the string does allocate, which is why decode never does.
        var beforeToString = GC.GetAllocatedBytesForCurrentThread();
        var symbol = Symbol(buffer);
        var allocatedWhileDecoding = GC.GetAllocatedBytesForCurrentThread() - beforeToString;

        Assert.Equal("ESH4", symbol);
        Assert.True(
            allocatedWhileDecoding > 0,
            $"expected ToString to allocate, but it allocated {allocatedWhileDecoding} bytes");
    }

    private static int SymbolLength(byte[] buffer)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SymbolMappingMsg>((ReadOnlySpan<byte>)buffer);
        return msg.StypeInSymbol.AsTextSpan().Length;
    }

    private static string Symbol(byte[] buffer)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SymbolMappingMsg>((ReadOnlySpan<byte>)buffer);
        return msg.StypeInSymbol.ToString();
    }

    private static CStr71 FieldOf(ReadOnlySpan<byte> text, int width)
    {
        Span<byte> raw = stackalloc byte[width];
        raw.Clear();
        text.CopyTo(raw);
        return MemoryMarshal.Read<CStr71>(raw);
    }
}
