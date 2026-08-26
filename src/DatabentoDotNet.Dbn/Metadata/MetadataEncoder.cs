using System.Buffers.Binary;
using System.Text;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Encodes a <see cref="Metadata"/> back into the DBN header bytes that open a stream.
/// </summary>
/// <remarks>
/// <para>
/// The encoder exists to close the loop on the decoder: a header decoded and re-encoded under
/// <see cref="VersionUpgradePolicy.AsIs"/> must reproduce the original bytes exactly. That is a
/// far stronger check than comparing fields, because a decoder that quietly drops a reserved run
/// or mis-sizes the length field still compares equal field by field.
/// </para>
/// <para>
/// <b>The length field and the write sequence must agree.</b> The prelude's length is computed up
/// front by <see cref="EncodedLength"/> and then the body is written; nothing in the language ties
/// the two together, which is exactly the drift upstream warns about. This encoder checks the
/// bytes it actually wrote against the length it declared before returning, so a future edit that
/// changes one and not the other fails immediately instead of producing a stream that reads eight
/// bytes off from the second record onward.
/// </para>
/// </remarks>
public static class MetadataEncoder
{
    /// <summary>
    /// The smallest a DBN metadata block can be: 8 bytes of prelude, the 100-byte fixed section,
    /// and the five 32-bit counts that stand in for five empty variable-length sections.
    /// </summary>
    public const int MinEncodedLength =
        DbnConstants.MetadataPreludeLength + DbnConstants.MetadataFixedLength + (sizeof(uint) * 5);

    /// <summary>
    /// Returns the exact number of bytes <see cref="Encode(Metadata, Span{byte})"/> will write,
    /// prelude included.
    /// </summary>
    /// <param name="metadata">The metadata to measure.</param>
    /// <returns>The total encoded size in bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnEncodeException">
    /// The block would be larger than <see cref="int.MaxValue"/> bytes. That is the only thing
    /// measuring can reject: an out-of-range version, a zero symbol width, and an over-long or
    /// non-ASCII symbol are all rejected by <see cref="Encode(Metadata, Span{byte})"/> when the
    /// bytes are actually written, not here — this method sums widths and never inspects content.
    /// </exception>
    public static int EncodedLength(Metadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return CalculateLength(metadata, out _) + DbnConstants.MetadataPreludeLength;
    }

    /// <summary>
    /// Encodes <paramref name="metadata"/> into a new array.
    /// </summary>
    /// <param name="metadata">The metadata to encode.</param>
    /// <returns>The encoded bytes, prelude included.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnEncodeException">The metadata cannot be encoded as DBN.</exception>
    public static byte[] Encode(Metadata metadata)
    {
        var buffer = new byte[EncodedLength(metadata)];
        Encode(metadata, buffer);
        return buffer;
    }

    /// <summary>
    /// Encodes <paramref name="metadata"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="metadata">The metadata to encode.</param>
    /// <param name="destination">
    /// A buffer of at least <see cref="EncodedLength"/> bytes. Bytes past the encoded block are
    /// left untouched.
    /// </param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    /// <exception cref="DbnEncodeException">The metadata cannot be encoded as DBN.</exception>
    public static int Encode(Metadata metadata, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var version = metadata.Version;
        if (version is < 1 or > DbnConstants.Version)
        {
            // Version 0 is legacy DBZ and is never emitted. Upstream clamps it to 1 instead, which
            // writes a version-1 prelude in front of a version-2 body; refusing is the honest
            // answer, and the only DBN versions that exist are 1 through 3 anyway.
            throw new DbnEncodeException(
                $"Cannot encode DBN metadata with version {version}: only versions 1 to {DbnConstants.Version} are valid.");
        }

        var symbolCstrLength = metadata.SymbolCstrLength;
        if (symbolCstrLength < 1)
        {
            throw new DbnEncodeException(
                $"Cannot encode DBN metadata with a symbol width of {symbolCstrLength}: a symbol field must have room for a NUL terminator.");
        }

        var length = CalculateLength(metadata, out var endPadding);
        var total = length + DbnConstants.MetadataPreludeLength;
        if (destination.Length < total)
        {
            throw new ArgumentException(
                $"The destination is {destination.Length} bytes; encoding this metadata needs {total}.",
                nameof(destination));
        }

        var pos = 0;

        DbnConstants.Magic.CopyTo(destination);
        pos += DbnConstants.MagicLength;
        destination[pos++] = version;
        WriteUInt32(destination, ref pos, (uint)length);

        WriteCstr(destination, ref pos, DbnConstants.MetadataDatasetCstrLength, metadata.Dataset, "dataset");
        WriteUInt16(destination, ref pos, metadata.Schema is { } schema ? (ushort)schema : DbnConstants.NullSchema);
        WriteUInt64(destination, ref pos, metadata.Start);
        WriteUInt64(destination, ref pos, metadata.End ?? DbnConstants.UndefTimestamp);
        WriteUInt64(destination, ref pos, metadata.Limit ?? DbnConstants.NullLimit);

        if (version == 1)
        {
            // The deprecated v1 record_count. Upstream always writes the null sentinel here rather
            // than a real count, and every v1 stream in the conformance corpus holds exactly that,
            // so writing it back is what makes a v1 header round-trip byte for byte.
            WriteUInt64(destination, ref pos, DbnConstants.NullRecordCount);
        }

        destination[pos++] = metadata.StypeIn is { } stypeIn ? (byte)stypeIn : DbnConstants.NullStype;
        destination[pos++] = (byte)metadata.StypeOut;
        destination[pos++] = metadata.TsOut ? (byte)1 : (byte)0;

        if (version > 1)
        {
            WriteUInt16(destination, ref pos, (ushort)symbolCstrLength);
        }

        var reservedLength = version == 1 ? DbnConstants.MetadataReservedLengthV1 : DbnConstants.MetadataReservedLength;
        destination.Slice(pos, reservedLength).Clear();
        pos += reservedLength;

        // schema_definition_length. Always zero: this codec neither writes nor reads schema
        // definitions, and the decoder rejects a non-zero value rather than skipping past it.
        WriteUInt32(destination, ref pos, 0);

        WriteRepeatedCstr(destination, ref pos, symbolCstrLength, metadata.Symbols, "symbols");
        WriteRepeatedCstr(destination, ref pos, symbolCstrLength, metadata.Partial, "partial");
        WriteRepeatedCstr(destination, ref pos, symbolCstrLength, metadata.NotFound, "not_found");
        WriteMappings(destination, ref pos, symbolCstrLength, metadata.Mappings);

        if (endPadding > 0)
        {
            destination.Slice(pos, endPadding).Clear();
            pos += endPadding;
        }

        if (pos != total)
        {
            throw new DbnEncodeException(
                $"Internal error encoding DBN metadata: declared {length} bytes in the prelude but wrote {pos - DbnConstants.MetadataPreludeLength}.");
        }

        return pos;
    }

    /// <summary>
    /// Computes the value of the prelude's length field, and how much of it is version-3 end
    /// padding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result <b>excludes</b> the 8-byte prelude — that is what the field on the wire means.
    /// </para>
    /// <para>
    /// The five 32-bit counts are the <c>schema_definition_length</c> and the element counts of
    /// <c>symbols</c>, <c>partial</c>, <c>not_found</c> and <c>mappings</c>. They sit outside the
    /// 100-byte fixed section even though the first of them looks like it belongs to it.
    /// </para>
    /// <para>
    /// Version 3 rounds the whole block up to a multiple of 8 with zero bytes. That is not
    /// cosmetic: the first record starts immediately after, and records are reinterpreted in place
    /// over the read buffer, which requires 8-byte alignment. Versions 1 and 2 get no such
    /// padding, so their blocks routinely end on an odd offset.
    /// </para>
    /// </remarks>
    private static int CalculateLength(Metadata metadata, out int endPadding)
    {
        var symbolCstrLength = (long)metadata.SymbolCstrLength;
        var intervalLength = (sizeof(uint) * 2) + symbolCstrLength;

        var cstrCount = (long)metadata.Symbols.Count + metadata.Partial.Count + metadata.NotFound.Count;
        var needed = DbnConstants.MetadataFixedLength + (sizeof(uint) * 5L) + (cstrCount * symbolCstrLength);

        foreach (var mapping in metadata.Mappings)
        {
            needed += symbolCstrLength + sizeof(uint) + (mapping.Intervals.Count * intervalLength);
        }

        var remainder = needed % 8;
        endPadding = metadata.Version < 3 || remainder == 0 ? 0 : (int)(8 - remainder);
        needed += endPadding;

        if (needed > uint.MaxValue || needed > int.MaxValue)
        {
            throw new DbnEncodeException($"Cannot encode DBN metadata: the encoded block would be {needed} bytes.");
        }

        return (int)needed;
    }

    private static void WriteUInt16(Span<byte> destination, ref int pos, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[pos..], value);
        pos += sizeof(ushort);
    }

    private static void WriteUInt32(Span<byte> destination, ref int pos, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[pos..], value);
        pos += sizeof(uint);
    }

    private static void WriteUInt64(Span<byte> destination, ref int pos, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination[pos..], value);
        pos += sizeof(ulong);
    }

    /// <summary>
    /// Writes one fixed-width C-string field: the string's bytes, then NUL padding to the full
    /// width.
    /// </summary>
    /// <remarks>
    /// An over-long or non-ASCII symbol is a hard error, never a truncation — upstream makes the
    /// same call. The usable width is one byte less than the field, because the last byte belongs
    /// to the NUL terminator.
    /// </remarks>
    private static void WriteCstr(Span<byte> destination, ref int pos, int width, string value, string what)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!Ascii.IsValid(value))
        {
            throw new DbnEncodeException($"Cannot encode {what} '{value}' in DBN: symbol fields are ASCII.");
        }

        var maxLength = width - 1;
        if (value.Length > maxLength)
        {
            throw new DbnEncodeException(
                $"Cannot encode {what} '{value}' in DBN: it cannot be longer than {maxLength} characters in this stream.");
        }

        var field = destination.Slice(pos, width);
        field.Clear();
        Ascii.FromUtf16(value, field, out _);
        pos += width;
    }

    private static void WriteRepeatedCstr(
        Span<byte> destination,
        ref int pos,
        int symbolCstrLength,
        IReadOnlyList<string> values,
        string what)
    {
        WriteUInt32(destination, ref pos, (uint)values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            WriteCstr(destination, ref pos, symbolCstrLength, values[i], $"{what}[{i}]");
        }
    }

    private static void WriteMappings(
        Span<byte> destination,
        ref int pos,
        int symbolCstrLength,
        IReadOnlyList<SymbolMapping> mappings)
    {
        WriteUInt32(destination, ref pos, (uint)mappings.Count);
        for (var i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            WriteCstr(destination, ref pos, symbolCstrLength, mapping.RawSymbol, $"mappings[{i}].raw_symbol");

            var intervals = mapping.Intervals;
            WriteUInt32(destination, ref pos, (uint)intervals.Count);
            for (var j = 0; j < intervals.Count; j++)
            {
                var interval = intervals[j];
                WriteUInt32(destination, ref pos, EncodeDate(interval.StartDate));
                WriteUInt32(destination, ref pos, EncodeDate(interval.EndDate));
                WriteCstr(destination, ref pos, symbolCstrLength, interval.Symbol, $"mappings[{i}].intervals[{j}].symbol");
            }
        }
    }

    /// <summary>
    /// Packs a date into the <c>u32</c> the wire uses: the decimal digits <c>YYYYMMDD</c>.
    /// </summary>
    private static uint EncodeDate(DateOnly date)
        => (uint)((date.Year * 10_000) + (date.Month * 100) + date.Day);
}
