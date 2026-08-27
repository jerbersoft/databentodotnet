using System.Buffers.Binary;
using System.Text;
using NodaTime;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Decodes the <see cref="Metadata"/> block that opens a DBN stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>The primitive takes a span, not a stream.</b> The incremental decoder reaches its metadata
/// state holding a buffer it has already filled from the socket; handing it only a
/// <see cref="Stream"/> API would force it to copy those bytes back out through a stream shim for
/// no reason. <see cref="Decode(ReadOnlySpan{byte}, VersionUpgradePolicy)"/> is therefore the real
/// entry point and <see cref="Decode(Stream, VersionUpgradePolicy)"/> is a convenience over it.
/// </para>
/// <para>
/// <b>Two steps, because the length is inside the bytes.</b> A reader cannot know how many bytes
/// the metadata occupies until it has read the 8-byte prelude, so
/// <see cref="DecodePrelude(ReadOnlySpan{byte}, out byte, out int)"/> and
/// <see cref="DecodeAfterPrelude(ReadOnlySpan{byte}, byte, VersionUpgradePolicy)"/> are exposed
/// separately: read 8 bytes, learn the length, wait for that many more, then decode. The combined
/// <c>Decode</c> overloads are for callers that already hold the whole block.
/// </para>
/// <para>
/// Every multi-byte field is little-endian, and this decoder reads each one explicitly through
/// <see cref="BinaryPrimitives"/> rather than reinterpreting the header as a struct. The header is
/// not a fixed layout across versions, so there is no struct to reinterpret.
/// </para>
/// </remarks>
public static class MetadataDecoder
{
    /// <summary>
    /// Strict UTF-8: an invalid sequence throws rather than becoming U+FFFD.
    /// </summary>
    /// <remarks>
    /// Silent replacement would be the wrong default here twice over. It hides corruption behind a
    /// plausible-looking symbol, and it breaks round-tripping, since the replacement character is
    /// not ASCII and the encoder rejects non-ASCII symbols outright. Upstream's
    /// <c>str::from_utf8</c> is likewise fallible at this exact point.
    /// </remarks>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// How much of the metadata body <see cref="Decode(Stream, VersionUpgradePolicy)"/> is willing
    /// to allocate before the stream has demonstrated it has that much to deliver.
    /// </summary>
    /// <remarks>
    /// Two properties pick the value. It is large enough that ordinary metadata — anything short
    /// of an <c>ALL_SYMBOLS</c> query — is read in one pass with no growth at all, and small
    /// enough to sit under the 85,000-byte large-object-heap threshold, so the common case neither
    /// grows nor lands on the LOH. A forged prelude costs this, not the up-to-512-MiB it asked
    /// for.
    /// </remarks>
    private const int InitialBodyCapacity = 64 * 1024;

    /// <summary>
    /// Reads the 8-byte prelude: the magic string, the DBN version, and the byte length of the
    /// metadata that follows it.
    /// </summary>
    /// <remarks>
    /// <paramref name="length"/> <b>excludes these 8 bytes.</b> It counts the fixed section plus
    /// the variable section (plus any version-3 end padding), so the first record begins at
    /// <see cref="DbnConstants.MetadataPreludeLength"/> + <paramref name="length"/>. Treating it
    /// as the total instead shifts every record in the stream by 8 bytes, which does not fail
    /// loudly — it decodes garbage.
    /// </remarks>
    /// <param name="source">At least <see cref="DbnConstants.MetadataPreludeLength"/> bytes, starting at the magic.</param>
    /// <param name="version">Receives the DBN version byte.</param>
    /// <param name="length">Receives the length in bytes of the metadata block following the prelude.</param>
    /// <exception cref="DbnDecodeException">
    /// <paramref name="source"/> is too short, does not start with the DBN magic, states a version
    /// this library cannot decode, or states a length outside
    /// <see cref="DbnConstants.MetadataFixedLength"/>..<see cref="DbnConstants.MaxMetadataLength"/>.
    /// </exception>
    public static void DecodePrelude(ReadOnlySpan<byte> source, out byte version, out int length)
    {
        if (source.Length < DbnConstants.MetadataPreludeLength)
        {
            throw new DbnDecodeException(
                $"Invalid DBN metadata: the prelude is {DbnConstants.MetadataPreludeLength} bytes but only {source.Length} were available.");
        }

        if (!source[..DbnConstants.MagicLength].SequenceEqual(DbnConstants.Magic))
        {
            throw new DbnDecodeException("Invalid DBN header: the stream does not start with the magic string \"DBN\".");
        }

        version = source[DbnConstants.MagicLength];
        ValidateVersion(version);

        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(source[(DbnConstants.MagicLength + 1)..]);
        if (rawLength > int.MaxValue)
        {
            throw new DbnDecodeException($"Invalid DBN metadata: the stated length {rawLength} is larger than this runtime can address.");
        }

        length = (int)rawLength;
        if (length < DbnConstants.MetadataFixedLength)
        {
            throw new DbnDecodeException(
                $"Invalid DBN metadata: the stated length {length} is shorter than the {DbnConstants.MetadataFixedLength}-byte fixed section.");
        }

        if (length > DbnConstants.MaxMetadataLength)
        {
            // The ceiling, and the only bound above. Eight bytes off a socket otherwise decide
            // an allocation size before anything about the block has been validated: a declared
            // length near int.MaxValue is a multi-gigabyte allocation, an OverflowException, or
            // an ArgumentOutOfRangeException depending on exactly which value was chosen — none
            // of them the DbnDecodeException every caller is told to expect from malformed DBN.
            // See DbnConstants.MaxMetadataLength for why 512 MiB and not less or more. This
            // overload's own caller no longer leans on it — ReadBody allocates as the stream
            // delivers, which is issue #12 — but DbnFsm still sizes its buffer from this field,
            // which is issue #31, so the ceiling is still doing work for someone.
            throw new DbnDecodeException(
                $"Invalid DBN metadata: the stated length {length} exceeds the " +
                $"{DbnConstants.MaxMetadataLength}-byte maximum metadata size.");
        }
    }

    /// <summary>
    /// Decodes a complete metadata block — prelude included — from the start of
    /// <paramref name="source"/>.
    /// </summary>
    /// <param name="source">
    /// The stream's bytes from the magic onwards. Anything past the metadata block is ignored, so
    /// passing a whole file is fine.
    /// </param>
    /// <param name="upgradePolicy">
    /// How to present data from an older DBN version. The default matches upstream's default and
    /// reports every stream as version 3.
    /// </param>
    /// <returns>The decoded metadata.</returns>
    /// <exception cref="DbnDecodeException">The bytes are not valid DBN metadata.</exception>
    public static Metadata Decode(ReadOnlySpan<byte> source, VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3)
    {
        DecodePrelude(source, out var version, out var length);

        if (source.Length - DbnConstants.MetadataPreludeLength < length)
        {
            throw new DbnDecodeException(
                $"Invalid DBN metadata: the prelude states {length} bytes of metadata but only " +
                $"{source.Length - DbnConstants.MetadataPreludeLength} follow the prelude.");
        }

        return DecodeAfterPrelude(source.Slice(DbnConstants.MetadataPreludeLength, length), version, upgradePolicy);
    }

    /// <summary>
    /// Decodes a metadata block whose prelude has already been read and consumed.
    /// </summary>
    /// <param name="body">
    /// Exactly the bytes the prelude's length field counted: the fixed section, the variable
    /// section, and any version-3 end padding.
    /// </param>
    /// <param name="version">The version byte from the prelude.</param>
    /// <param name="upgradePolicy">How to present data from an older DBN version.</param>
    /// <returns>The decoded metadata.</returns>
    /// <exception cref="DbnDecodeException">The bytes are not valid DBN metadata.</exception>
    public static Metadata DecodeAfterPrelude(
        ReadOnlySpan<byte> body,
        byte version,
        VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3)
    {
        ValidateVersion(version);
        ValidateCompatibility(upgradePolicy, version);

        if (body.Length < DbnConstants.MetadataFixedLength)
        {
            throw new DbnDecodeException(
                $"Invalid DBN metadata: the block is {body.Length} bytes, shorter than the " +
                $"{DbnConstants.MetadataFixedLength}-byte fixed section.");
        }

        var pos = 0;

        var dataset = ReadCstr(body, ref pos, DbnConstants.MetadataDatasetCstrLength, "dataset");

        var rawSchema = ReadUInt16(body, ref pos, "schema");
        Schema? schema = null;
        if (rawSchema != DbnConstants.NullSchema)
        {
            if (!EnumValues.TryFromSchema(rawSchema, out var decodedSchema))
            {
                throw new DbnDecodeException($"Invalid DBN metadata: {rawSchema} is not a defined schema.");
            }

            schema = decodedSchema;
        }

        var start = ReadUInt64(body, ref pos, "start");
        var rawEnd = ReadUInt64(body, ref pos, "end");
        var rawLimit = ReadUInt64(body, ref pos, "limit");

        if (version == 1)
        {
            // v1 carries a deprecated record_count here that v2 replaced with symbol_cstr_len
            // further down. Upstream reads and discards it; the encoder writes NullRecordCount
            // back, and every v1 stream in the wild already holds exactly that.
            Advance(body, ref pos, sizeof(ulong), "record_count");
        }

        var rawStypeIn = ReadByte(body, ref pos, "stype_in");
        SType? stypeIn = null;
        if (rawStypeIn != DbnConstants.NullStype)
        {
            if (!EnumValues.TryFromSType(rawStypeIn, out var decodedStypeIn))
            {
                throw new DbnDecodeException($"Invalid DBN metadata: {rawStypeIn} is not a defined stype_in.");
            }

            stypeIn = decodedStypeIn;
        }

        var rawStypeOut = ReadByte(body, ref pos, "stype_out");
        if (!EnumValues.TryFromSType(rawStypeOut, out var stypeOut))
        {
            throw new DbnDecodeException($"Invalid DBN metadata: {rawStypeOut} is not a defined stype_out.");
        }

        var tsOut = ReadByte(body, ref pos, "ts_out") != 0;

        int symbolCstrLength;
        if (version == 1)
        {
            // Not on the wire at all in v1 — the width is fixed by the version.
            symbolCstrLength = DbnConstants.SymbolCstrLengthV1;
        }
        else
        {
            symbolCstrLength = ReadUInt16(body, ref pos, "symbol_cstr_len");
            if (symbolCstrLength < 1)
            {
                // A zero-width symbol field has no room for even the NUL terminator. Rejecting it
                // is also what keeps the count-driven loops below bounded: with a positive width,
                // a symbol count can never exceed the bytes remaining.
                throw new DbnDecodeException("Invalid DBN metadata: symbol_cstr_len is zero.");
            }
        }

        Advance(
            body,
            ref pos,
            version == 1 ? DbnConstants.MetadataReservedLengthV1 : DbnConstants.MetadataReservedLength,
            "reserved");

        var schemaDefinitionLength = ReadUInt32(body, ref pos, "schema_definition_length");
        if (schemaDefinitionLength != 0)
        {
            throw new DbnDecodeException("Invalid DBN metadata: this version of the codec cannot parse schema definitions.");
        }

        var symbols = ReadRepeatedCstr(body, ref pos, symbolCstrLength, "symbols");
        var partial = ReadRepeatedCstr(body, ref pos, symbolCstrLength, "partial");
        var notFound = ReadRepeatedCstr(body, ref pos, symbolCstrLength, "not_found");
        var mappings = ReadMappings(body, ref pos, symbolCstrLength);

        // Anything left is the version-3 end padding, which exists only to keep the first record
        // 8-byte aligned and carries no fields. It is skipped wholesale, exactly as upstream does.
        var metadata = new Metadata
        {
            Version = version,
            Dataset = dataset,
            Schema = schema,
            Start = start,
            End = rawEnd == DbnConstants.UndefTimestamp || rawEnd == 0 ? null : rawEnd,
            Limit = rawLimit == DbnConstants.NullLimit ? null : rawLimit,
            StypeIn = stypeIn,
            StypeOut = stypeOut,
            TsOut = tsOut,
            SymbolCstrLength = symbolCstrLength,
            Symbols = symbols,
            Partial = partial,
            NotFound = notFound,
            Mappings = mappings,
        };

        return metadata.Upgrade(upgradePolicy);
    }

    /// <summary>
    /// Reads and decodes a metadata block from the current position of <paramref name="source"/>,
    /// leaving the stream positioned on the first record.
    /// </summary>
    /// <remarks>
    /// A convenience over the span form for callers that already hold a stream — a file, say. It
    /// buffers the block, because the block's own length is only known after the prelude, and
    /// decoding needs it contiguous. It buffers it <em>incrementally</em>, for the reasons set out
    /// on <see cref="ReadBody"/>: the prelude's length is not trusted to size an allocation.
    /// </remarks>
    /// <param name="source">A readable stream positioned at the DBN magic.</param>
    /// <param name="upgradePolicy">How to present data from an older DBN version.</param>
    /// <returns>The decoded metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnDecodeException">The stream ends early, or its bytes are not valid DBN metadata.</exception>
    public static Metadata Decode(Stream source, VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3)
    {
        ArgumentNullException.ThrowIfNull(source);

        Span<byte> prelude = stackalloc byte[DbnConstants.MetadataPreludeLength];
        ReadExactly(source, prelude, "prelude");
        DecodePrelude(prelude, out var version, out var length);

        return DecodeAfterPrelude(ReadBody(source, length), version, upgradePolicy);
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes of metadata body, allocating only as fast as
    /// the stream proves it has bytes to give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The declared length does not get to size an allocation.</b> It is four bytes off the
    /// wire, read before anything about the block has been validated, so <c>new byte[length]</c>
    /// — what this did until issue #12 — hands a remote peer the allocator: eight bytes buy up to
    /// <see cref="DbnConstants.MaxMetadataLength"/> of memory, and the failure mode is an
    /// <see cref="OutOfMemoryException"/>. That is not a <see cref="DbnDecodeException"/>, so it
    /// is outside the contract every caller of this class is handed, and it destabilises the
    /// process rather than the one stream that carried the bad bytes.
    /// </para>
    /// <para>
    /// <b>A tighter cap is not the fix, which is why the buffer grows instead.</b> Metadata size
    /// scales with symbology, and a legitimate <c>ALL_SYMBOLS</c> definition query runs to tens or
    /// hundreds of megabytes — the argument is set out in full on
    /// <see cref="DbnConstants.MaxMetadataLength"/>. Doubling from
    /// <see cref="InitialBodyCapacity"/>, and only once the previous capacity has actually been
    /// filled, makes memory track bytes <em>delivered</em> rather than bytes <em>claimed</em>: a
    /// prelude that lies costs one chunk, and a prelude that tells the truth is paid for by the
    /// peer sending the bytes it promised.
    /// </para>
    /// <para>
    /// <see cref="Decode(ReadOnlySpan{byte}, VersionUpgradePolicy)"/> never had the problem — the
    /// span is its own bound — and is unchanged.
    /// </para>
    /// </remarks>
    /// <param name="source">A readable stream positioned on the first byte after the prelude.</param>
    /// <param name="length">The body length the prelude declared, already range-checked by <see cref="DecodePrelude"/>.</param>
    /// <returns>An array of exactly <paramref name="length"/> bytes.</returns>
    /// <exception cref="DbnDecodeException">The stream ended before <paramref name="length"/> bytes arrived.</exception>
    private static byte[] ReadBody(Stream source, int length)
    {
        var body = new byte[Math.Min(length, InitialBodyCapacity)];
        var filled = 0;

        while (filled < length)
        {
            if (filled == body.Length)
            {
                // The widening is deliberate. `length` is capped well inside int range by
                // DecodePrelude, so the double cannot overflow today; the line should be correct
                // on its own terms rather than only because of a check in another method — the
                // same reasoning as DbnFsm.DecodePrelude's `required`.
                Array.Resize(ref body, (int)Math.Min((long)body.Length * 2, length));
            }

            // Short reads are ordinary on a network stream and this is the loop that absorbs
            // them; `Read` returning zero is the one thing that means end-of-stream.
            var read = source.Read(body.AsSpan(filled));
            if (read == 0)
            {
                throw new DbnDecodeException(
                    $"Invalid DBN metadata: the prelude states {length} bytes of metadata but the stream " +
                    $"ended after {filled}.");
            }

            filled += read;
        }

        return body;
    }

    private static void ReadExactly(Stream source, Span<byte> destination, string what)
    {
        try
        {
            source.ReadExactly(destination);
        }
        catch (EndOfStreamException e)
        {
            // A stream that simply ended is not exceptional; a stream that ended in the middle of
            // a header is, because there is no valid DBN prefix of those bytes.
            throw new DbnDecodeException($"Invalid DBN metadata: the stream ended in the middle of the {what}.", e);
        }
    }

    private static void ValidateVersion(byte version)
    {
        if (version > DbnConstants.Version)
        {
            throw new DbnDecodeException(
                $"Cannot decode a newer version of DBN: this decoder supports up to version {DbnConstants.Version}, the input states version {version}.");
        }

        if (version == 0)
        {
            // Upstream's DbnVersion::try_from defines the valid range as 1..=DBN_VERSION, and
            // version 0 denotes a legacy DBZ file, which this codec does not read. Upstream's
            // prelude decoder happens to let a 0 through and then treats it as v2-shaped on
            // decode but v1-shaped on encode, which produces a 98-byte fixed section labelled
            // version 1 — a header no reader can parse. Rejecting it here is narrower and safer
            // than reproducing that.
            throw new DbnDecodeException("Cannot decode DBN version 0: version 0 denotes a legacy DBZ file.");
        }
    }

    private static void ValidateCompatibility(VersionUpgradePolicy upgradePolicy, byte version)
    {
        if (version > 2 && upgradePolicy == VersionUpgradePolicy.UpgradeToV2)
        {
            throw new DbnDecodeException(
                $"Invalid combination of VersionUpgradePolicy.UpgradeToV2 and input version {version}: " +
                "the policies only move forward. Use AsIs or UpgradeToV3.");
        }
    }

    private static void Advance(ReadOnlySpan<byte> body, ref int pos, int count, string what)
    {
        Require(body, pos, count, what);
        pos += count;
    }

    private static void Require(ReadOnlySpan<byte> body, int pos, int count, string what)
    {
        if (body.Length - pos < count)
        {
            throw new DbnDecodeException(
                $"Invalid DBN metadata: reached the end of the block while reading {what} " +
                $"({count} bytes needed at offset {pos}, {body.Length - pos} available).");
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> body, ref int pos, string what)
    {
        Require(body, pos, sizeof(byte), what);
        return body[pos++];
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> body, ref int pos, string what)
    {
        Require(body, pos, sizeof(ushort), what);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
        pos += sizeof(ushort);
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> body, ref int pos, string what)
    {
        Require(body, pos, sizeof(uint), what);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        pos += sizeof(uint);
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> body, ref int pos, string what)
    {
        Require(body, pos, sizeof(ulong), what);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(body[pos..]);
        pos += sizeof(ulong);
        return value;
    }

    /// <summary>
    /// Reads one fixed-width C-string field, trimming <b>trailing</b> NUL padding only.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same rule as the C-string fields inside records, which cut at the
    /// first NUL. Upstream metadata uses <c>trim_end_matches('\0')</c>, so a field holding
    /// <c>"AB\0C"</c> plus padding decodes as <c>"AB\0C"</c> here and as <c>"AB"</c> there. Only
    /// trailing-trim round-trips: the encoder writes the string's bytes verbatim and then pads,
    /// so cutting at the first NUL would silently shorten the field on re-encode.
    /// </remarks>
    private static string ReadCstr(ReadOnlySpan<byte> body, ref int pos, int width, string what)
    {
        Require(body, pos, width, what);
        var field = body.Slice(pos, width);
        pos += width;

        var end = field.Length;
        while (end > 0 && field[end - 1] == 0)
        {
            end--;
        }

        try
        {
            return StrictUtf8.GetString(field[..end]);
        }
        catch (DecoderFallbackException e)
        {
            throw new DbnDecodeException($"Invalid DBN metadata: {what} is not valid UTF-8.", e);
        }
    }

    private static List<string> ReadRepeatedCstr(ReadOnlySpan<byte> body, ref int pos, int symbolCstrLength, string what)
    {
        var count = ReadCount(body, pos, symbolCstrLength, what);
        pos += sizeof(uint);

        // Bounded by the block: the count was just checked against the bytes remaining, so this
        // capacity can never exceed the input's own size.
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadCstr(body, ref pos, symbolCstrLength, $"{what}[{i}]"));
        }

        return result;
    }

    private static List<SymbolMapping> ReadMappings(ReadOnlySpan<byte> body, ref int pos, int symbolCstrLength)
    {
        // The smallest possible mapping is a raw symbol plus a zero interval count.
        var count = ReadCount(body, pos, symbolCstrLength + sizeof(uint), "mappings");
        pos += sizeof(uint);

        var result = new List<SymbolMapping>(count);
        for (var i = 0; i < count; i++)
        {
            var rawSymbol = ReadCstr(body, ref pos, symbolCstrLength, $"mappings[{i}].raw_symbol");

            var intervalLength = (sizeof(uint) * 2) + symbolCstrLength;
            var intervalCount = ReadCount(body, pos, intervalLength, $"mappings[{i}].intervals");
            pos += sizeof(uint);

            var intervals = new MappingInterval[intervalCount];
            for (var j = 0; j < intervalCount; j++)
            {
                var startDate = ReadDate(body, ref pos, $"mappings[{i}].intervals[{j}].start_date");
                var endDate = ReadDate(body, ref pos, $"mappings[{i}].intervals[{j}].end_date");
                var symbol = ReadCstr(body, ref pos, symbolCstrLength, $"mappings[{i}].intervals[{j}].symbol");
                intervals[j] = new MappingInterval(startDate, endDate, symbol);
            }

            result.Add(new SymbolMapping { RawSymbol = rawSymbol, Intervals = intervals });
        }

        return result;
    }

    /// <summary>
    /// Reads a <c>u32</c> element count at <paramref name="pos"/> and checks it against the bytes
    /// actually remaining, without advancing.
    /// </summary>
    /// <remarks>
    /// The check is the point. Every one of these counts is attacker-controlled and each element
    /// is variable-width, so a corrupt count of four billion would otherwise be believed long
    /// enough to size a list from it. Upstream makes the same check for the same reason, noting
    /// that a variable-length <c>SymbolMapping</c> "requires frequent bounds checks".
    /// </remarks>
    private static int ReadCount(ReadOnlySpan<byte> body, int pos, int elementLength, string what)
    {
        Require(body, pos, sizeof(uint), $"{what} count");
        var rawCount = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);

        var remaining = body.Length - pos - sizeof(uint);
        if (rawCount > (uint)(remaining / elementLength))
        {
            throw new DbnDecodeException(
                $"Invalid DBN metadata: {what} states {rawCount} entries of {elementLength} bytes, " +
                $"but only {remaining} bytes remain in the block.");
        }

        return (int)rawCount;
    }

    /// <summary>
    /// Reads a mapping-interval date: a <c>u32</c> holding the decimal digits <c>YYYYMMDD</c>.
    /// </summary>
    /// <remarks>
    /// Not days-since-epoch and not nanoseconds. <c>20151031</c> is 2015-10-31. This is the only
    /// date representation in DBN metadata that is not a UNIX nanosecond timestamp — the
    /// <see cref="Metadata.Start"/>/<see cref="Metadata.End"/> pair is the other kind, and
    /// confusing them yields dates around 1970 for everything.
    /// </remarks>
    private static LocalDate ReadDate(ReadOnlySpan<byte> body, ref int pos, string what)
    {
        var raw = ReadUInt32(body, ref pos, what);

        var year = (int)(raw / 10_000);
        var remainder = raw % 10_000;
        var month = (int)(remainder / 100);
        var day = (int)(remainder % 100);

        // Order matters and the short-circuit is load-bearing: GetDaysInMonth throws on a year or
        // month outside the calendar's range, so both are bounds-checked before it is reached.
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > CalendarSystem.Iso.GetDaysInMonth(year, month))
        {
            throw new DbnDecodeException($"Invalid DBN metadata: {raw} is not a valid YYYYMMDD date, reading {what}.");
        }

        return new LocalDate(year, month, day);
    }
}
