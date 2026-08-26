using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Conformance tests for the DBN metadata header: the block that opens every DBN file and every
/// live stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>Byte-identity is the real assertion here.</b> Comparing decoded fields one by one passes
/// even when the decoder silently drops a reserved run, mis-sizes the prelude's length field, or
/// forgets version 3's end padding — every one of which corrupts the stream from the first record
/// onward. Re-encoding and comparing bytes catches all three.
/// </para>
/// <para>
/// <b>Round-tripping is asserted under <see cref="VersionUpgradePolicy.AsIs"/>, not the default.</b>
/// The library default is <see cref="VersionUpgradePolicy.UpgradeToV3"/>, matching upstream, and
/// under it a v1 or v2 header is deliberately re-encoded as v3 — wider symbol fields, a different
/// fixed-section layout, a different length. Byte-identity is impossible by construction there, so
/// asserting it under the default would test nothing but the assertion's own absence.
/// </para>
/// </remarks>
public class MetadataTests
{
    // Every field of test_data.mbo.*, which the hand-built blocks below reproduce byte for byte.
    private const string MboDataset = "GLBX.MDP3";
    private const ulong MboStart = 1_609_160_400_000_000_000UL;
    private const ulong MboEnd = 1_609_200_000_000_000_000UL;
    private const ulong MboLimit = 2UL;
    private const string MboSymbol = "ESH1";
    private const string MboResolvedSymbol = "5482";
    private const int HandBuiltV1BodyLength = 198;
    private const int HandBuiltV2BodyLength = 345;

    private static readonly DateOnly MboIntervalStart = new(2020, 12, 28);
    private static readonly DateOnly MboIntervalEnd = new(2020, 12, 29);

    [Fact]
    public void Decode_EveryNonFragmentFixture_Succeeds()
    {
        var failures = new List<string>();
        var byVersion = new Dictionary<byte, int>();

        foreach (var fixture in TestFixtures.NonFragments)
        {
            try
            {
                var metadata = MetadataDecoder.Decode(TestFixtures.ReadDecompressed(fixture), VersionUpgradePolicy.AsIs);
                Assert.Equal(fixture.Version, metadata.Version);
                byVersion[metadata.Version] = byVersion.GetValueOrDefault(metadata.Version) + 1;
            }
            catch (DbnException e)
            {
                failures.Add($"{fixture.Name}: {e.Message}");
            }
        }

        Assert.Empty(failures);

        // The corpus census, restated as an assertion: if a re-vendor ever changes the mix, the
        // v1-specific paths below could quietly stop being covered at all.
        Assert.Equal(64, TestFixtures.NonFragments.Count());
        Assert.Equal(12, byVersion[1]);
        Assert.Equal(34, byVersion[2]);
        Assert.Equal(18, byVersion[3]);
    }

    [Fact]
    public void Encode_EveryNonFragmentFixtureDecodedAsIs_RoundTripsByteIdentically()
    {
        var mismatches = new List<string>();
        var matched = 0;

        foreach (var fixture in TestFixtures.NonFragments)
        {
            var bytes = TestFixtures.ReadDecompressed(fixture);
            MetadataDecoder.DecodePrelude(bytes, out _, out var length);
            var original = bytes.AsSpan(0, DbnConstants.MetadataPreludeLength + length);

            var metadata = MetadataDecoder.Decode(bytes, VersionUpgradePolicy.AsIs);
            var reencoded = MetadataEncoder.Encode(metadata);

            if (original.SequenceEqual(reencoded))
            {
                matched++;
                continue;
            }

            mismatches.Add($"{fixture.Name}: {DescribeFirstDifference(original, reencoded)}");
        }

        Assert.Equal(64, matched + mismatches.Count);
        Assert.True(
            mismatches.Count == 0,
            $"{matched} of {matched + mismatches.Count} fixtures round-tripped. Failures:{Environment.NewLine}" +
            string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void EncodedLength_EveryNonFragmentFixture_MatchesThePreludeLengthPlusEight()
    {
        Assert.All(TestFixtures.NonFragments, fixture =>
        {
            var bytes = TestFixtures.ReadDecompressed(fixture);
            MetadataDecoder.DecodePrelude(bytes, out _, out var length);

            var metadata = MetadataDecoder.Decode(bytes, VersionUpgradePolicy.AsIs);

            // The length field and the write sequence are computed separately, exactly as
            // upstream computes them; this is the assertion that keeps them from drifting apart.
            Assert.Equal(DbnConstants.MetadataPreludeLength + length, MetadataEncoder.EncodedLength(metadata));
            Assert.Equal(
                MetadataEncoder.EncodedLength(metadata),
                MetadataEncoder.Encode(metadata, new byte[MetadataEncoder.EncodedLength(metadata)]));
        });
    }

    [Fact]
    public void DecodePrelude_MetadataLengthExcludesThePrelude()
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v1.dbn.zst"));

        MetadataDecoder.DecodePrelude(bytes, out var version, out var length);

        Assert.Equal(1, version);

        // 198, not 206: the prelude's own 8 bytes are not counted. Reading it as the total would
        // start the record stream 8 bytes early and decode garbage rather than fail.
        Assert.Equal(HandBuiltV1BodyLength, length);
        Assert.Equal((uint)HandBuiltV1BodyLength, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void Decode_V1Fixture_ReadsEveryFieldOfTheVersion1Layout()
    {
        var metadata = MetadataDecoder.Decode(
            TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v1.dbn.zst")),
            VersionUpgradePolicy.AsIs);

        AssertMboMetadata(metadata, expectedVersion: 1, expectedSymbolCstrLength: DbnConstants.SymbolCstrLengthV1);
    }

    [Fact]
    public void Decode_V2Fixture_ReadsSymbolCstrLengthFromTheWire()
    {
        var metadata = MetadataDecoder.Decode(
            TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v2.dbn.zst")),
            VersionUpgradePolicy.AsIs);

        AssertMboMetadata(metadata, expectedVersion: 2, expectedSymbolCstrLength: DbnConstants.SymbolCstrLength);
    }

    [Fact]
    public void Decode_V3Fixture_ReadsEveryFieldAndIgnoresTheEndPadding()
    {
        var metadata = MetadataDecoder.Decode(
            TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn")),
            VersionUpgradePolicy.AsIs);

        AssertMboMetadata(metadata, expectedVersion: 3, expectedSymbolCstrLength: DbnConstants.SymbolCstrLength);
    }

    [Fact]
    public void Decode_V1FixtureWithUpgradeToV3_ReportsVersion3AndTheWiderSymbolField()
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v1.dbn.zst"));

        var upgraded = MetadataDecoder.Decode(bytes, VersionUpgradePolicy.UpgradeToV3);

        Assert.Equal(3, upgraded.Version);

        // v1 has no symbol_cstr_len on the wire at all, so the upgrade is where a v1 stream gains
        // one: 22 by definition before, 71 after.
        Assert.Equal(DbnConstants.SymbolCstrLength, upgraded.SymbolCstrLength);
        Assert.Equal(DbnConstants.SymbolCstrLengthV1, MetadataDecoder.Decode(bytes, VersionUpgradePolicy.AsIs).SymbolCstrLength);

        // Nothing but the version and the field width changes.
        AssertMboMetadata(upgraded, expectedVersion: 3, expectedSymbolCstrLength: DbnConstants.SymbolCstrLength);
    }

    [Fact]
    public void Decode_V2FixtureWithUpgradeToV3_BumpsTheVersionAndLeavesTheSymbolWidthAlone()
    {
        var upgraded = MetadataDecoder.Decode(
            TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v2.dbn.zst")),
            VersionUpgradePolicy.UpgradeToV3);

        Assert.Equal(3, upgraded.Version);

        // v2 and v3 share a symbol width, so this upgrade is a version bump and nothing else.
        Assert.Equal(DbnConstants.SymbolCstrLength, upgraded.SymbolCstrLength);
    }

    [Fact]
    public void Decode_V1FixtureWithUpgradeToV2_ReportsVersion2()
    {
        var upgraded = MetadataDecoder.Decode(
            TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v1.dbn.zst")),
            VersionUpgradePolicy.UpgradeToV2);

        Assert.Equal(2, upgraded.Version);
        Assert.Equal(DbnConstants.SymbolCstrLength, upgraded.SymbolCstrLength);
    }

    [Theory]
    [InlineData("test_data.mbo.v1.dbn.zst")]
    [InlineData("test_data.mbo.v2.dbn.zst")]
    public void Encode_UpgradedToV3_ProducesTheVendoredV3HeaderExactly(string name)
    {
        // test_data.mbo.v1, .v2 and .v3 describe the same query, so upgrading either older header
        // must land on the v3 file's bytes: the same fields, the v3 fixed-section layout, the
        // wider symbol fields, and the 7 bytes of end padding v3 adds and v2 does not.
        var upgraded = MetadataDecoder.Decode(TestFixtures.ReadDecompressed(Fixture(name)), VersionUpgradePolicy.UpgradeToV3);

        var v3Bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn"));
        MetadataDecoder.DecodePrelude(v3Bytes, out _, out var v3Length);

        Assert.Equal(
            v3Bytes.AsSpan(0, DbnConstants.MetadataPreludeLength + v3Length).ToArray(),
            MetadataEncoder.Encode(upgraded));
    }

    [Fact]
    public void Encode_Version3_RoundsTheBlockUpToAnEightByteBoundaryWithZeros()
    {
        var v3 = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn"));
        var v2 = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v2.dbn.zst"));

        var v3Length = MetadataEncoder.EncodedLength(MetadataDecoder.Decode(v3, VersionUpgradePolicy.AsIs));
        var v2Length = MetadataEncoder.EncodedLength(MetadataDecoder.Decode(v2, VersionUpgradePolicy.AsIs));

        // Same content, seven bytes apart: v3 pads so the first record starts 8-byte aligned,
        // which is what lets records be reinterpreted in place. v2 does not pad at all.
        Assert.Equal(360, v3Length);
        Assert.Equal(353, v2Length);
        Assert.Equal(0, (v3Length - DbnConstants.MetadataPreludeLength) % 8);
        Assert.Equal(1, (v2Length - DbnConstants.MetadataPreludeLength) % 8);

        var encoded = MetadataEncoder.Encode(MetadataDecoder.Decode(v3, VersionUpgradePolicy.AsIs));
        Assert.All(encoded[^7..], padding => Assert.Equal((byte)0, padding));
    }

    [Fact]
    public void EncodedLength_MetadataWithNoSymbolsOrMappings_IsTheMinimumEncodedLength()
    {
        // test_data.statistics.* carries no symbols and no mappings, so it is exactly the floor:
        // 8 prelude + 100 fixed + five 32-bit counts standing in for five empty sections.
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.statistics.v2.dbn.zst"));
        var metadata = MetadataDecoder.Decode(bytes, VersionUpgradePolicy.AsIs);

        Assert.Empty(metadata.Symbols);
        Assert.Empty(metadata.Partial);
        Assert.Empty(metadata.NotFound);
        Assert.Empty(metadata.Mappings);
        Assert.Equal(128, MetadataEncoder.MinEncodedLength);
        Assert.Equal(MetadataEncoder.MinEncodedLength, MetadataEncoder.EncodedLength(metadata));
    }

    [Fact]
    public void Decode_DefinitionFixture_ReadsEveryMappingInterval()
    {
        var metadata = MetadataDecoder.Decode(
            TestFixtures.ReadDecompressed(Fixture("test_data.definition.v1.dbn.zst")),
            VersionUpgradePolicy.AsIs);

        var mapping = Assert.Single(metadata.Mappings);
        Assert.Equal(62, mapping.Intervals.Count);

        // YYYYMMDD decimal digits packed into a u32 — not days since an epoch and not nanoseconds.
        Assert.Equal(new DateOnly(2021, 10, 4), mapping.Intervals[0].StartDate);
        Assert.Equal(new DateOnly(2021, 10, 5), mapping.Intervals[0].EndDate);

        // Half-open: each interval's end is the next one's start, so a date belongs to exactly one.
        for (var i = 1; i < mapping.Intervals.Count; i++)
        {
            Assert.Equal(mapping.Intervals[i - 1].EndDate, mapping.Intervals[i].StartDate);
        }
    }

    [Fact]
    public void Decode_HandBuiltV1Block_MatchesTheVendoredV1Fixture()
    {
        // Written byte by byte from the v1 offset table rather than by re-encoding anything, so
        // this compares the table against upstream's own output. A decoder bug cannot hide here:
        // nothing in the library produced either side.
        var handBuilt = HandBuiltV1Block();

        var fixture = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v1.dbn.zst"));

        Assert.Equal(
            fixture.AsSpan(0, DbnConstants.MetadataPreludeLength + HandBuiltV1BodyLength).ToArray(),
            handBuilt);
    }

    [Fact]
    public void Decode_HandBuiltV2Block_MatchesTheVendoredV2Fixture()
    {
        var handBuilt = HandBuiltV2Block();

        var fixture = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v2.dbn.zst"));

        Assert.Equal(
            fixture.AsSpan(0, DbnConstants.MetadataPreludeLength + HandBuiltV2BodyLength).ToArray(),
            handBuilt);
    }

    [Fact]
    public void Decode_HandBuiltV1Block_ReadsRecordCountWhereV2ReadsSymbolCstrLength()
    {
        var handBuilt = HandBuiltV1Block();

        var metadata = MetadataDecoder.Decode(handBuilt, VersionUpgradePolicy.AsIs);

        AssertMboMetadata(metadata, expectedVersion: 1, expectedSymbolCstrLength: DbnConstants.SymbolCstrLengthV1);
        Assert.Equal(handBuilt, MetadataEncoder.Encode(metadata));

        // The 8 bytes at relative offset 42 are v1's deprecated record_count, not the start of
        // stype_in. Reading them as v2 would land stype_in on the low byte of the sentinel (0xFF,
        // "no stype_in") and shift everything after it.
        Assert.Equal(
            DbnConstants.NullRecordCount,
            BinaryPrimitives.ReadUInt64LittleEndian(handBuilt.AsSpan(DbnConstants.MetadataPreludeLength + 42)));
        Assert.Equal((byte)SType.RawSymbol, handBuilt[DbnConstants.MetadataPreludeLength + 50]);
    }

    [Fact]
    public void Decode_HandBuiltV2Block_ReadsSymbolCstrLengthFromRelativeOffset45()
    {
        var handBuilt = HandBuiltV2Block();

        var metadata = MetadataDecoder.Decode(handBuilt, VersionUpgradePolicy.AsIs);

        AssertMboMetadata(metadata, expectedVersion: 2, expectedSymbolCstrLength: DbnConstants.SymbolCstrLength);
        Assert.Equal(handBuilt, MetadataEncoder.Encode(metadata));
        Assert.Equal(
            DbnConstants.SymbolCstrLength,
            BinaryPrimitives.ReadUInt16LittleEndian(handBuilt.AsSpan(DbnConstants.MetadataPreludeLength + 45)));
    }

    [Fact]
    public void DecodePrelude_Version4_ThrowsDbnDecodeException()
    {
        var prelude = HandBuiltV2Block();
        prelude[3] = 4;

        var exception = Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(prelude));

        Assert.Contains("version 4", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<DbnException>(exception);
    }

    [Fact]
    public void DecodePrelude_Version0_ThrowsDbnDecodeException()
    {
        // Version 0 denotes a legacy DBZ file. Upstream's prelude decoder lets it through and then
        // treats it as v2-shaped on decode but v1-shaped on encode, producing a 98-byte fixed
        // section labelled version 1; this port refuses it instead, matching DbnVersion's own
        // 1..=3 range.
        var block = HandBuiltV2Block();
        block[3] = 0;

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block));
    }

    [Fact]
    public void Encode_VersionOutsideOneToThree_ThrowsDbnEncodeException()
    {
        // Version 0 is legacy DBZ. Upstream clamps it to 1 on the way out, which writes a
        // version-1 prelude in front of a version-2 body; refusing is the honest answer.
        Assert.Throws<DbnEncodeException>(() => MetadataEncoder.Encode(BuildMetadata(version: 0)));
        Assert.Throws<DbnEncodeException>(() => MetadataEncoder.Encode(BuildMetadata(version: 4)));
    }

    [Fact]
    public void DecodePrelude_BadMagic_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();
        block[0] = (byte)'X';

        var exception = Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block));

        Assert.Contains("magic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePrelude_LengthShorterThanTheFixedSection_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), DbnConstants.MetadataFixedLength - 1);

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block));
    }

    [Fact]
    public void DecodePrelude_TooFewBytesForThePrelude_ThrowsDbnDecodeException()
    {
        Assert.Throws<DbnDecodeException>(
            () => MetadataDecoder.DecodePrelude("DBN"u8, out _, out _));
    }

    [Fact]
    public void Decode_BodyShorterThanThePreludeStates_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block.AsSpan(0, block.Length - 1)));
    }

    [Fact]
    public void Decode_NonZeroSchemaDefinitionLength_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(DbnConstants.MetadataPreludeLength + DbnConstants.MetadataFixedLength),
            1);

        var exception = Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));

        Assert.Contains("schema definitions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_SymbolCountLargerThanTheBlock_ThrowsDbnDecodeException()
    {
        // A corrupt count is attacker-controlled and every element is variable-width, so believing
        // it long enough to size a list from it is the whole risk.
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(DbnConstants.MetadataPreludeLength + DbnConstants.MetadataFixedLength + 4),
            uint.MaxValue);

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));
    }

    [Fact]
    public void Decode_MappingIntervalCountLargerThanTheBlock_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(DbnConstants.MetadataPreludeLength + 262), uint.MaxValue);

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));
    }

    [Fact]
    public void Decode_ZeroSymbolCstrLength_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(DbnConstants.MetadataPreludeLength + 45), 0);

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));
    }

    [Fact]
    public void Decode_UndefinedSchema_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(DbnConstants.MetadataPreludeLength + 16), 9_000);

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));
    }

    [Fact]
    public void Decode_NullSchemaSentinel_YieldsNoSchema()
    {
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt16LittleEndian(
            block.AsSpan(DbnConstants.MetadataPreludeLength + 16),
            DbnConstants.NullSchema);

        var metadata = MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs);

        // A mixed-schema stream, which is the normal case for live data.
        Assert.Null(metadata.Schema);
        Assert.Equal(block, MetadataEncoder.Encode(metadata));
    }

    [Fact]
    public void Decode_NullStypeSentinel_YieldsNoInputSymbology()
    {
        var block = HandBuiltV2Block();
        block[DbnConstants.MetadataPreludeLength + 42] = DbnConstants.NullStype;

        var metadata = MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs);

        Assert.Null(metadata.StypeIn);
        Assert.Equal(SType.InstrumentId, metadata.StypeOut);
        Assert.Equal(block, MetadataEncoder.Encode(metadata));
    }

    [Fact]
    public void Decode_UndefEndAndNullLimit_YieldNullAndRoundTrip()
    {
        // The two 64-bit "unset" sentinels are not the same value: end is u64::MAX, limit is 0.
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt64LittleEndian(
            block.AsSpan(DbnConstants.MetadataPreludeLength + 26),
            DbnConstants.UndefTimestamp);
        BinaryPrimitives.WriteUInt64LittleEndian(
            block.AsSpan(DbnConstants.MetadataPreludeLength + 34),
            DbnConstants.NullLimit);

        var metadata = MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs);

        Assert.Null(metadata.End);
        Assert.Null(metadata.Limit);
        Assert.Equal(MboStart, metadata.Start);
        Assert.Equal(block, MetadataEncoder.Encode(metadata));
    }

    [Fact]
    public void Decode_ZeroEnd_YieldsNullAndReEncodesAsTheUndefSentinel()
    {
        // Pinned deliberately: upstream maps a raw zero end to "no end" as well, so re-encoding
        // writes u64::MAX back and this one field does not round-trip byte-identically. No stream
        // in the corpus carries a zero here, and both spellings mean the same thing to a reader —
        // but the divergence is real, so it is asserted rather than left to be discovered.
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(DbnConstants.MetadataPreludeLength + 26), 0);

        var metadata = MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs);

        Assert.Null(metadata.End);
        Assert.Equal(
            DbnConstants.UndefTimestamp,
            BinaryPrimitives.ReadUInt64LittleEndian(
                MetadataEncoder.Encode(metadata).AsSpan(DbnConstants.MetadataPreludeLength + 26)));
    }

    [Fact]
    public void Decode_InvalidDayInAMappingInterval_ThrowsDbnDecodeException()
    {
        // Upstream's own invalid-date case: 20100600 is June of year 2010, day zero.
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(DbnConstants.MetadataPreludeLength + 266),
            20_100_600);

        var exception = Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));

        Assert.Contains("20100600", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_InvalidMonthInAMappingInterval_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(DbnConstants.MetadataPreludeLength + 266),
            20_101_305);

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));
    }

    [Fact]
    public void Decode_V3FixtureWithUpgradeToV2_ThrowsDbnDecodeException()
    {
        // The policies only move forward; asking to present v3 data as v2 has no meaning.
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn"));

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(bytes, VersionUpgradePolicy.UpgradeToV2));
    }

    [Fact]
    public void Decode_Stream_MatchesTheSpanFormAndStopsOnTheFirstRecord()
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v2.dbn.zst"));
        using var stream = new MemoryStream(bytes);

        var fromStream = MetadataDecoder.Decode(stream, VersionUpgradePolicy.AsIs);

        MetadataDecoder.DecodePrelude(bytes, out _, out var length);
        Assert.Equal(DbnConstants.MetadataPreludeLength + length, stream.Position);
        Assert.Equal(
            MetadataEncoder.Encode(MetadataDecoder.Decode(bytes, VersionUpgradePolicy.AsIs)),
            MetadataEncoder.Encode(fromStream));
    }

    [Fact]
    public void Decode_StreamThatEndsMidBlock_ThrowsDbnDecodeException()
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v2.dbn.zst"));
        using var stream = new MemoryStream(bytes[..64]);

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(stream, VersionUpgradePolicy.AsIs));
    }

    [Fact]
    public void Encode_SymbolLongerThanTheFieldAllows_ThrowsDbnEncodeException()
    {
        // The last byte of the field belongs to the NUL terminator, so a 22-byte field holds 21
        // characters. Upstream fails here too rather than truncating: a shortened symbol would
        // corrupt the symbology silently.
        var metadata = BuildMetadata(version: 1, symbols: [new string('A', DbnConstants.SymbolCstrLengthV1)]);

        var exception = Assert.Throws<DbnEncodeException>(() => MetadataEncoder.Encode(metadata));

        Assert.Contains(
            (DbnConstants.SymbolCstrLengthV1 - 1).ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_SymbolExactlyOneShorterThanTheField_Succeeds()
    {
        var metadata = BuildMetadata(version: 1, symbols: [new string('A', DbnConstants.SymbolCstrLengthV1 - 1)]);

        var encoded = MetadataEncoder.Encode(metadata);

        Assert.Equal(encoded, MetadataEncoder.Encode(MetadataDecoder.Decode(encoded, VersionUpgradePolicy.AsIs)));
    }

    [Fact]
    public void Encode_NonAsciiSymbol_ThrowsDbnEncodeException()
    {
        var metadata = BuildMetadata(version: 2, symbols: ["ESH1É"]);

        Assert.Throws<DbnEncodeException>(() => MetadataEncoder.Encode(metadata));
    }

    [Fact]
    public void Decode_NonUtf8Symbol_ThrowsDbnDecodeException()
    {
        var block = HandBuiltV2Block();

        // 0xFF is not a valid UTF-8 lead byte. Substituting U+FFFD instead of failing would hide
        // the corruption behind a plausible symbol and break re-encoding, since the replacement
        // character is not ASCII.
        block[DbnConstants.MetadataPreludeLength + 108] = 0xFF;

        Assert.Throws<DbnDecodeException>(() => MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs));
    }

    [Fact]
    public void Encode_DestinationTooSmall_ThrowsArgumentException()
    {
        var metadata = MetadataDecoder.Decode(
            TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v2.dbn.zst")),
            VersionUpgradePolicy.AsIs);

        Assert.Throws<ArgumentException>(
            () => MetadataEncoder.Encode(metadata, new byte[MetadataEncoder.EncodedLength(metadata) - 1]));
    }

    [Fact]
    public void SymbolCstrLengthForVersion_MatchesTheWidthEachVersionUses()
    {
        Assert.Equal(DbnConstants.SymbolCstrLengthV1, Metadata.SymbolCstrLengthForVersion(1));
        Assert.Equal(DbnConstants.SymbolCstrLength, Metadata.SymbolCstrLengthForVersion(2));
        Assert.Equal(DbnConstants.SymbolCstrLength, Metadata.SymbolCstrLengthForVersion(3));
    }

    [Fact]
    public void MetadataFixedLength_IsOneHundredInEveryVersionDespiteDifferentLayouts()
    {
        // v1 spends 8 bytes on record_count and 47 on reserved; v2 and v3 spend 2 on
        // symbol_cstr_len and 53 on reserved. Both land on 100, which is why one offset table
        // cannot describe both.
        // dataset(16) + schema(2) + start/end/limit(24) + stype_in/stype_out/ts_out(3).
        var shared = DbnConstants.MetadataDatasetCstrLength + sizeof(ushort) + (sizeof(ulong) * 3) + 3;

        Assert.Equal(
            DbnConstants.MetadataFixedLength,
            shared + sizeof(ulong) + DbnConstants.MetadataReservedLengthV1);
        Assert.Equal(
            DbnConstants.MetadataFixedLength,
            shared + sizeof(ushort) + DbnConstants.MetadataReservedLength);
    }

    private static DbnFixture Fixture(string name)
        => TestFixtures.All.Single(fixture => fixture.Name == name);

    private static Metadata BuildMetadata(byte version, IReadOnlyList<string>? symbols = null) => new()
    {
        Version = version,
        Dataset = MboDataset,
        Schema = Schema.Mbo,
        Start = MboStart,
        End = MboEnd,
        Limit = MboLimit,
        StypeIn = SType.RawSymbol,
        StypeOut = SType.InstrumentId,
        SymbolCstrLength = Metadata.SymbolCstrLengthForVersion(version),
        Symbols = symbols ?? [],
    };

    private static void AssertMboMetadata(Metadata metadata, byte expectedVersion, int expectedSymbolCstrLength)
    {
        Assert.Equal(expectedVersion, metadata.Version);
        Assert.Equal(expectedSymbolCstrLength, metadata.SymbolCstrLength);
        Assert.Equal(MboDataset, metadata.Dataset);
        Assert.Equal(Schema.Mbo, metadata.Schema);
        Assert.Equal(MboStart, metadata.Start);
        Assert.Equal(MboEnd, metadata.End);
        Assert.Equal(MboLimit, metadata.Limit);
        Assert.Equal(SType.RawSymbol, metadata.StypeIn);
        Assert.Equal(SType.InstrumentId, metadata.StypeOut);
        Assert.False(metadata.TsOut);
        Assert.Equal(new[] { MboSymbol }, metadata.Symbols);
        Assert.Empty(metadata.Partial);
        Assert.Empty(metadata.NotFound);

        var mapping = Assert.Single(metadata.Mappings);
        Assert.Equal(MboSymbol, mapping.RawSymbol);
        Assert.Equal(
            new MappingInterval(MboIntervalStart, MboIntervalEnd, MboResolvedSymbol),
            Assert.Single(mapping.Intervals));
    }

    /// <summary>
    /// The DBN v1 metadata block of <c>test_data.mbo.v1</c>, written from the offset table rather
    /// than produced by this library.
    /// </summary>
    private static byte[] HandBuiltV1Block()
    {
        var block = new byte[DbnConstants.MetadataPreludeLength + HandBuiltV1BodyLength];
        var span = block.AsSpan();

        "DBN"u8.CopyTo(span);
        span[3] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], HandBuiltV1BodyLength);

        var body = span[DbnConstants.MetadataPreludeLength..];
        WriteAscii(body[..16], MboDataset);                                         //   0  dataset
        BinaryPrimitives.WriteUInt16LittleEndian(body[16..], (ushort)Schema.Mbo); //  16  schema
        BinaryPrimitives.WriteUInt64LittleEndian(body[18..], MboStart);             //  18  start
        BinaryPrimitives.WriteUInt64LittleEndian(body[26..], MboEnd);               //  26  end
        BinaryPrimitives.WriteUInt64LittleEndian(body[34..], MboLimit);             //  34  limit
        BinaryPrimitives.WriteUInt64LittleEndian(body[42..], DbnConstants.NullRecordCount); // 42  record_count (v1 only)
        body[50] = (byte)SType.RawSymbol;                                           //  50  stype_in
        body[51] = (byte)SType.InstrumentId;                                        //  51  stype_out
        body[52] = 0;                                                               //  52  ts_out
                                                                                    //  53  reserved (47 zero bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(body[100..], 0);                    // 100  schema_definition_length
        BinaryPrimitives.WriteUInt32LittleEndian(body[104..], 1);                    // 104  symbols count
        WriteAscii(body.Slice(108, 22), MboSymbol);                                  // 108  symbols[0]
        BinaryPrimitives.WriteUInt32LittleEndian(body[130..], 0);                    // 130  partial count
        BinaryPrimitives.WriteUInt32LittleEndian(body[134..], 0);                    // 134  not_found count
        BinaryPrimitives.WriteUInt32LittleEndian(body[138..], 1);                    // 138  mappings count
        WriteAscii(body.Slice(142, 22), MboSymbol);                                  // 142  mappings[0].raw_symbol
        BinaryPrimitives.WriteUInt32LittleEndian(body[164..], 1);                    // 164  mappings[0].interval count
        BinaryPrimitives.WriteUInt32LittleEndian(body[168..], 20_201_228);           // 168  start_date, YYYYMMDD
        BinaryPrimitives.WriteUInt32LittleEndian(body[172..], 20_201_229);           // 172  end_date, YYYYMMDD
        WriteAscii(body.Slice(176, 22), MboResolvedSymbol);                          // 176  interval symbol

        return block;
    }

    /// <summary>
    /// The DBN v2 metadata block of <c>test_data.mbo.v2</c>, written from the offset table rather
    /// than produced by this library.
    /// </summary>
    private static byte[] HandBuiltV2Block()
    {
        var block = new byte[DbnConstants.MetadataPreludeLength + HandBuiltV2BodyLength];
        var span = block.AsSpan();

        "DBN"u8.CopyTo(span);
        span[3] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], HandBuiltV2BodyLength);

        var body = span[DbnConstants.MetadataPreludeLength..];
        WriteAscii(body[..16], MboDataset);                                         //   0  dataset
        BinaryPrimitives.WriteUInt16LittleEndian(body[16..], (ushort)Schema.Mbo); //  16  schema
        BinaryPrimitives.WriteUInt64LittleEndian(body[18..], MboStart);             //  18  start
        BinaryPrimitives.WriteUInt64LittleEndian(body[26..], MboEnd);               //  26  end
        BinaryPrimitives.WriteUInt64LittleEndian(body[34..], MboLimit);             //  34  limit
        body[42] = (byte)SType.RawSymbol;                                           //  42  stype_in
        body[43] = (byte)SType.InstrumentId;                                        //  43  stype_out
        body[44] = 0;                                                               //  44  ts_out
        BinaryPrimitives.WriteUInt16LittleEndian(body[45..], (ushort)DbnConstants.SymbolCstrLength); // 45 symbol_cstr_len
                                                                                    //  47  reserved (53 zero bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(body[100..], 0);                    // 100  schema_definition_length
        BinaryPrimitives.WriteUInt32LittleEndian(body[104..], 1);                    // 104  symbols count
        WriteAscii(body.Slice(108, 71), MboSymbol);                                  // 108  symbols[0]
        BinaryPrimitives.WriteUInt32LittleEndian(body[179..], 0);                    // 179  partial count
        BinaryPrimitives.WriteUInt32LittleEndian(body[183..], 0);                    // 183  not_found count
        BinaryPrimitives.WriteUInt32LittleEndian(body[187..], 1);                    // 187  mappings count
        WriteAscii(body.Slice(191, 71), MboSymbol);                                  // 191  mappings[0].raw_symbol
        BinaryPrimitives.WriteUInt32LittleEndian(body[262..], 1);                    // 262  mappings[0].interval count
        BinaryPrimitives.WriteUInt32LittleEndian(body[266..], 20_201_228);           // 266  start_date, YYYYMMDD
        BinaryPrimitives.WriteUInt32LittleEndian(body[270..], 20_201_229);           // 270  end_date, YYYYMMDD
        WriteAscii(body.Slice(274, 71), MboResolvedSymbol);                          // 274  interval symbol

        return block;
    }

    // System.Text.Ascii, not Encoding.ASCII: inside DatabentoDotNet.Dbn.Tests the bare name
    // `Encoding` binds to the DBN wire enum in the parent namespace, not to System.Text.Encoding.
    private static void WriteAscii(Span<byte> field, string value)
    {
        field.Clear();
        Ascii.FromUtf16(value, field, out _);
    }

    private static string DescribeFirstDifference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (expected.Length != actual.Length)
        {
            return $"length {actual.Length}, expected {expected.Length}";
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return $"first difference at offset {i}: expected 0x{expected[i]:X2}, got 0x{actual[i]:X2}";
            }
        }

        return "no difference";
    }
}
