using System.Buffers.Binary;
using System.Globalization;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Decoder tests: the state machine (<see cref="DbnFsm"/>) and the stream layer over it
/// (<see cref="DbnDecoder"/>), against the whole vendored fixture corpus.
/// </summary>
/// <remarks>
/// <para>
/// <b>The record counts are an independent oracle.</b> Every expected count in
/// <see cref="ExpectedRecordCounts"/> was produced by running the upstream <c>dbn</c> CLI
/// (v0.68.0, built from <c>rust/dbn-cli</c>) over the same vendored file and counting its JSON
/// lines — not by running this decoder and writing down what it said. A count that agrees is
/// therefore evidence, not a tautology.
/// </para>
/// <para>
/// <b>The byte-at-a-time test is the one that matters.</b> A TCP socket hands a DBN stream over
/// in pieces that have nothing to do with record boundaries, and one byte is a perfectly ordinary
/// read. Feeding the decoder a single byte per <c>Fill</c> and demanding byte-identical output to
/// a single bulk read is the whole reason the decoder is a state machine rather than a loop.
/// </para>
/// </remarks>
public class DbnDecoderTests
{
    /// <summary>
    /// Records per fixture, as counted by the upstream <c>dbn</c> CLI v0.68.0.
    /// </summary>
    /// <remarks>
    /// Produced with <c>dbn -J FILE | wc -l</c> for ordinary streams and
    /// <c>dbn --input-fragment</c> / <c>--input-zstd-fragment</c> for the metadata-less ones. The
    /// four <c>ohlcv-1d</c> fixtures really do hold zero records — they are metadata and nothing
    /// else, which makes them the corpus's empty-stream case rather than a mistake.
    /// </remarks>
    private static readonly Dictionary<string, int> ExpectedRecordCounts = new(StringComparer.Ordinal)
    {
        ["multi-frame.definition.v1.dbn.frag.zst"] = 8,
        ["test_data.bbo-1m.dbn"] = 4,
        ["test_data.bbo-1m.v2.dbn.zst"] = 4,
        ["test_data.bbo-1m.v3.dbn.zst"] = 4,
        ["test_data.bbo-1s.dbn"] = 4,
        ["test_data.bbo-1s.v2.dbn.zst"] = 4,
        ["test_data.bbo-1s.v3.dbn.zst"] = 4,
        ["test_data.cbbo-1s.dbn"] = 2,
        ["test_data.cbbo-1s.v2.dbn.zst"] = 2,
        ["test_data.cbbo-1s.v3.dbn.zst"] = 2,
        ["test_data.cmbp-1.dbn"] = 2,
        ["test_data.cmbp-1.v2.dbn.zst"] = 2,
        ["test_data.cmbp-1.v3.dbn.zst"] = 2,
        ["test_data.definition.dbn"] = 2,
        ["test_data.definition.dbn.frag.zst"] = 2,
        ["test_data.definition.v1.dbn.frag"] = 2,
        ["test_data.definition.v1.dbn.frag.zst"] = 2,
        ["test_data.definition.v1.dbn.zst"] = 2,
        ["test_data.definition.v2.dbn.frag"] = 2,
        ["test_data.definition.v2.dbn.zst"] = 2,
        ["test_data.definition.v3.dbn.frag"] = 2,
        ["test_data.definition.v3.dbn.frag.zst"] = 2,
        ["test_data.definition.v3.dbn.zst"] = 2,
        ["test_data.imbalance.dbn"] = 2,
        ["test_data.imbalance.v1.dbn.zst"] = 2,
        ["test_data.imbalance.v2.dbn.zst"] = 2,
        ["test_data.imbalance.v3.dbn.zst"] = 2,
        ["test_data.mbo.dbn"] = 2,
        ["test_data.mbo.v1.dbn.zst"] = 2,
        ["test_data.mbo.v2.dbn.zst"] = 2,
        ["test_data.mbo.v3.dbn"] = 2,
        ["test_data.mbo.v3.dbn.zst"] = 2,
        ["test_data.mbp-1.dbn"] = 2,
        ["test_data.mbp-1.v1.dbn.zst"] = 2,
        ["test_data.mbp-1.v2.dbn.zst"] = 2,
        ["test_data.mbp-1.v3.dbn.zst"] = 2,
        ["test_data.mbp-10.dbn"] = 2,
        ["test_data.mbp-10.v1.dbn.zst"] = 2,
        ["test_data.mbp-10.v2.dbn.zst"] = 2,
        ["test_data.mbp-10.v3.dbn.zst"] = 2,
        ["test_data.ohlcv-1d.dbn"] = 0,
        ["test_data.ohlcv-1d.v1.dbn.zst"] = 0,
        ["test_data.ohlcv-1d.v2.dbn.zst"] = 0,
        ["test_data.ohlcv-1d.v3.dbn.zst"] = 0,
        ["test_data.ohlcv-1h.dbn"] = 2,
        ["test_data.ohlcv-1h.v1.dbn.zst"] = 2,
        ["test_data.ohlcv-1h.v2.dbn.zst"] = 2,
        ["test_data.ohlcv-1h.v3.dbn.zst"] = 2,
        ["test_data.ohlcv-1m.dbn"] = 2,
        ["test_data.ohlcv-1m.v1.dbn.zst"] = 2,
        ["test_data.ohlcv-1m.v2.dbn.zst"] = 2,
        ["test_data.ohlcv-1m.v3.dbn.zst"] = 2,
        ["test_data.ohlcv-1s.dbn"] = 2,
        ["test_data.ohlcv-1s.v1.dbn.zst"] = 2,
        ["test_data.ohlcv-1s.v2.dbn.zst"] = 2,
        ["test_data.ohlcv-1s.v3.dbn.zst"] = 2,
        ["test_data.statistics.dbn"] = 2,
        ["test_data.statistics.v1.dbn.zst"] = 2,
        ["test_data.statistics.v2.dbn.zst"] = 2,
        ["test_data.statistics.v3.dbn.zst"] = 2,
        ["test_data.status.dbn"] = 4,
        ["test_data.status.v2.dbn.zst"] = 4,
        ["test_data.status.v3.dbn.zst"] = 4,
        ["test_data.tbbo.dbn"] = 2,
        ["test_data.tbbo.v1.dbn.zst"] = 2,
        ["test_data.tbbo.v2.dbn.zst"] = 2,
        ["test_data.tbbo.v3.dbn.zst"] = 2,
        ["test_data.trades.dbn"] = 2,
        ["test_data.trades.v1.dbn.zst"] = 2,
        ["test_data.trades.v2.dbn.zst"] = 2,
        ["test_data.trades.v3.dbn.zst"] = 2,
    };

    [Fact]
    public void ExpectedRecordCounts_CoverEveryFixture()
    {
        // Guards the oracle itself: a re-vendor that adds a fixture must add its upstream count
        // rather than silently going untested by the two corpus-wide tests below.
        Assert.Equal(TestFixtures.All.Count, ExpectedRecordCounts.Count);
        Assert.All(
            TestFixtures.All,
            fixture => Assert.True(
                ExpectedRecordCounts.ContainsKey(fixture.Name),
                $"{fixture.Name}: no upstream record count recorded."));
    }

    [Fact]
    public void TryNextRecord_EveryFixture_YieldsTheRecordCountUpstreamReports()
    {
        Assert.All(TestFixtures.All, fixture =>
        {
            var records = DecodeThroughStream(fixture);

            Assert.Equal(ExpectedRecordCounts[fixture.Name], records.Count);
            Assert.All(
                records,
                record => Assert.True(
                    record.Length >= 16 && record.Length % 4 == 0,
                    $"{fixture.Name}: decoded a {record.Length}-byte record."));
        });
    }

    /// <summary>
    /// The test that matters most: one byte per <c>Fill</c> must produce byte-identical output to
    /// a single bulk read, for every fixture.
    /// </summary>
    [Fact]
    public void TryNextRecord_OneBytePerFill_MatchesASingleBulkRead()
    {
        Assert.All(TestFixtures.All, fixture =>
        {
            var bulk = DecodeInOneBulkFill(fixture);
            var drip = DecodeOneBytePerFill(fixture);

            Assert.Equal(ExpectedRecordCounts[fixture.Name], bulk.Count);
            Assert.True(
                bulk.Count == drip.Count,
                $"{fixture.Name}: bulk read yielded {bulk.Count} records, byte-at-a-time yielded {drip.Count}.");

            for (var i = 0; i < bulk.Count; i++)
            {
                Assert.True(
                    bulk[i].AsSpan().SequenceEqual(drip[i]),
                    $"{fixture.Name}: record {i} differs between a bulk read and byte-at-a-time feeding.");
            }
        });
    }

    [Fact]
    public void TryNextRecord_OneBytePerStreamRead_MatchesASingleBulkRead()
    {
        // The same claim one layer up, where the drip-feed is the stream rather than the caller —
        // so the zstd decompressor is also driven a byte at a time.
        Assert.All(TestFixtures.All, fixture =>
        {
            var whole = DecodeThroughStream(fixture);
            Assert.Equal(ExpectedRecordCounts[fixture.Name], whole.Count);

            var raw = TestFixtures.Read(fixture.Name);
            using var trickle = new SingleByteStream(raw);
            using var decoder = new DbnDecoder(trickle, skipMetadata: fixture.IsFragment);
            var dripped = DrainRecords(decoder);

            Assert.True(
                whole.Count == dripped.Count,
                $"{fixture.Name}: whole-stream read yielded {whole.Count} records, one-byte reads yielded {dripped.Count}.");

            for (var i = 0; i < whole.Count; i++)
            {
                Assert.True(
                    whole[i].AsSpan().SequenceEqual(dripped[i]),
                    $"{fixture.Name}: record {i} differs when the stream returns one byte per read.");
            }
        });
    }

    [Fact]
    public void TryNextRecord_Fragments_DecodeThroughTheMetadataLessPath()
    {
        var fragments = TestFixtures.Fragments.ToList();
        Assert.Equal(7, fragments.Count);

        Assert.All(fragments, fragment =>
        {
            var fsm = new DbnFsm(skipMetadata: true);

            // A fragment has no prelude and no metadata block at all, so the machine must already
            // be in its record state before a single byte arrives.
            Assert.True(fsm.HasDecodedMetadata);
            Assert.Null(fsm.Metadata);

            var records = DecodeOneBytePerFill(fragment);
            Assert.Equal(ExpectedRecordCounts[fragment.Name], records.Count);
        });
    }

    [Fact]
    public void Metadata_NonFragments_DecodesAndReportsThePolicysVersion()
    {
        Assert.All(TestFixtures.NonFragments, fixture =>
        {
            using var upgraded = OpenFixture(fixture);
            Assert.Equal(3, upgraded.Metadata!.Version);

            using var asIs = OpenFixture(fixture, VersionUpgradePolicy.AsIs);
            Assert.Equal(fixture.Version!.Value, asIs.Metadata!.Version);
            Assert.NotEmpty(asIs.Metadata.Dataset);
        });
    }

    [Fact]
    public void TryNextRecord_V1Definitions_UpgradeToV3SizedRecords()
    {
        var fixture = Fixture("test_data.definition.v1.dbn.zst");

        var upgraded = DecodeThroughStream(fixture);
        Assert.Equal(2, upgraded.Count);
        Assert.All(upgraded, record => Assert.Equal(InstrumentDefMsg.WireSize, record.Length));

        var asIs = DecodeThroughStream(fixture, VersionUpgradePolicy.AsIs);
        Assert.Equal(2, asIs.Count);
        Assert.All(asIs, record => Assert.Equal(InstrumentDefMsgV1.WireSize, record.Length));
    }

    [Fact]
    public void TryNextRecord_V2Definitions_UpgradeToV3SizedRecords()
    {
        var fixture = Fixture("test_data.definition.v2.dbn.zst");

        var upgraded = DecodeThroughStream(fixture);
        Assert.Equal(2, upgraded.Count);
        Assert.All(upgraded, record => Assert.Equal(InstrumentDefMsg.WireSize, record.Length));

        var asIs = DecodeThroughStream(fixture, VersionUpgradePolicy.AsIs);
        Assert.All(asIs, record => Assert.Equal(InstrumentDefMsgV2.WireSize, record.Length));
    }

    [Fact]
    public void TryNextRecord_V1Statistics_UpgradeToV3SizedRecords()
    {
        var fixture = Fixture("test_data.statistics.v1.dbn.zst");

        Assert.All(DecodeThroughStream(fixture), record => Assert.Equal(StatMsg.WireSize, record.Length));
        Assert.All(
            DecodeThroughStream(fixture, VersionUpgradePolicy.AsIs),
            record => Assert.Equal(StatMsgV1.WireSize, record.Length));
    }

    [Fact]
    public void TryNextRecord_UpgradedRecord_DowncastsToTheV3StructAndNotTheV1One()
    {
        using var decoder = OpenFixture(Fixture("test_data.definition.v1.dbn.zst"));

        Assert.True(decoder.TryNextRecord(out var record));
        Assert.True(record.Has<InstrumentDefMsg>());
        Assert.False(record.Has<InstrumentDefMsgV1>());
        Assert.False(record.Has<InstrumentDefMsgV2>());

        // The upgraded record carries the v3 symbol width, which is the field that actually grew.
        ref readonly var definition = ref record.Get<InstrumentDefMsg>();
        Assert.NotEqual(0u, definition.Header.InstrumentId);
    }

    [Fact]
    public void TryNextRecord_FragmentWithoutAVersion_InfersItFromTheRecordSize()
    {
        // No metadata and no version supplied: the only clue to the input version is that a
        // definition smaller than the v3 struct must be an older one.
        var fragment = Fixture("test_data.definition.v1.dbn.frag");

        var fsm = new DbnFsm(skipMetadata: true);
        Assert.Null(fsm.InputDbnVersion);

        var records = FeedWhole(fsm, TestFixtures.ReadDecompressed(fragment));

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(InstrumentDefMsg.WireSize, record.Length));
        Assert.Equal<byte?>(1, fsm.InputDbnVersion);
    }

    [Fact]
    public void TryNextRecord_EveryDefinitionFixture_UpgradesToTheV3StructSize()
    {
        // Definitions are the record family that changed in both v2 and v3, so this is the one
        // corpus-wide claim that would catch an upgrade case quietly going missing.
        var definitions = TestFixtures.All
            .Where(fixture => fixture.Name.Contains("definition", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(11, definitions.Count);
        Assert.All(definitions, fixture =>
        {
            var records = DecodeThroughStream(fixture);

            Assert.NotEmpty(records);
            Assert.All(
                records,
                record => Assert.True(
                    record.Length == InstrumentDefMsg.WireSize,
                    $"{fixture.Name}: decoded a {record.Length}-byte definition, expected {InstrumentDefMsg.WireSize}."));
        });
    }

    [Fact]
    public void TryNextRecord_V2FragmentWithoutAVersion_InfersVersionTwo()
    {
        // The middle rung of the size ladder: 400 bytes is not smaller than the v2 struct, so it
        // is not v1, but it is smaller than the v3 struct, so it is not current either.
        var fragment = Fixture("test_data.definition.v2.dbn.frag");

        var fsm = new DbnFsm(skipMetadata: true);
        var records = FeedWhole(fsm, TestFixtures.ReadDecompressed(fragment));

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(InstrumentDefMsg.WireSize, record.Length));
        Assert.Equal<byte?>(2, fsm.InputDbnVersion);
    }

    [Fact]
    public void TryNextRecord_V1OnlyRecordFamilies_UpgradeWhetherTheVersionIsToldOrInferred()
    {
        // No vendored fixture carries a symbol mapping, an error, a system message or a v1
        // statistic, so these four upgrade cases have no fixture coverage at all — and an upgrade
        // case that silently stops firing produces smaller records, not fewer, which a
        // count-based test would never notice.
        AssertUpgrades<SymbolMappingMsgV1, SymbolMappingMsg>(RType.SymbolMapping);
        AssertUpgrades<ErrorMsgV1, ErrorMsg>(RType.Error);
        AssertUpgrades<SystemMsgV1, SystemMsg>(RType.System);
        AssertUpgrades<StatMsgV1, StatMsg>(RType.Statistics);
    }

    [Fact]
    public void TryNextRecord_V3FragmentWithoutAVersion_PassesRecordsThroughUntouched()
    {
        var fragment = Fixture("test_data.definition.v3.dbn.frag");
        var bytes = TestFixtures.ReadDecompressed(fragment);

        var fsm = new DbnFsm(skipMetadata: true);
        var records = FeedWhole(fsm, bytes);

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(InstrumentDefMsg.WireSize, record.Length));

        // Nothing was rewritten, so the decoded bytes are the file's own bytes.
        Assert.True(bytes.AsSpan(0, InstrumentDefMsg.WireSize).SequenceEqual(records[0]));

        // Upstream's inference only ever concludes "an older version", never "already current".
        Assert.Null(fsm.InputDbnVersion);
    }

    [Fact]
    public void TryNextRecord_TwoUpgradedRecords_AliasBecauseTheCompatBufferIsOneRecordWide()
    {
        // Executable form of the lifetime contract on DbnFsm, and the case that makes it more
        // than a formality. An upgraded record lives in the compat buffer, which is reset to
        // offset 0 as each record is handed out — so the next upgraded record is written straight
        // over the previous one's bytes. No shift, no freed memory: the same span simply becomes a
        // different record. A caller that holds two records across two calls gets the latest one
        // twice, silently.
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.definition.v1.dbn.zst"));

        // What the two records actually are, obtained the correct way: copied out one at a time.
        var copied = DecodeThroughStream(Fixture("test_data.definition.v1.dbn.zst"));
        Assert.Equal(2, copied.Count);
        Assert.False(
            copied[0].AsSpan().SequenceEqual(copied[1]),
            "The fixture's two definitions must differ, or this test proves nothing.");

        var fsm = new DbnFsm(bufferSize: bytes.Length + DbnConstants.MaxRecordLength);
        bytes.AsSpan().CopyTo(fsm.Space());
        fsm.Fill(bytes.Length);

        Assert.True(fsm.TryNextRecord(out var first));
        Assert.True(first.Bytes.SequenceEqual(copied[0]));

        Assert.True(fsm.TryNextRecord(out var second));
        Assert.True(second.Bytes.SequenceEqual(copied[1]));

        // `first` was never touched, yet it now reads as the second record.
        Assert.True(
            first.Bytes.SequenceEqual(copied[1]),
            "An upgraded record must be overwritten by the next one — if this fails, the compat "
                + "buffer no longer aliases and the lifetime documentation on DbnFsm is stale.");
    }

    [Fact]
    public void TryNextRecord_TwoPassThroughRecords_DoNotAliasBecauseTheyStayInTheReadBuffer()
    {
        // The contrast that makes the rule worth stating: a record needing no upgrade is never
        // copied anywhere, so consecutive records occupy distinct stretches of the read buffer and
        // both stay readable — right up until Space() shifts. Same contract, different mechanism,
        // which is exactly why the docs say "the next call" rather than "the next Space()".
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn"));

        var copied = DecodeThroughStream(Fixture("test_data.mbo.v3.dbn"));
        Assert.Equal(2, copied.Count);
        Assert.False(copied[0].AsSpan().SequenceEqual(copied[1]));

        var fsm = new DbnFsm(bufferSize: bytes.Length + DbnConstants.MaxRecordLength);
        bytes.AsSpan().CopyTo(fsm.Space());
        fsm.Fill(bytes.Length);

        Assert.True(fsm.TryNextRecord(out var first));
        Assert.True(fsm.TryNextRecord(out var second));

        Assert.True(first.Bytes.SequenceEqual(copied[0]));
        Assert.True(second.Bytes.SequenceEqual(copied[1]));
    }

    [Fact]
    public void TryNextRecord_MultiFrameZstdFragment_DecodesEveryFrame()
    {
        // Eight records spread across several zstd frames: a decompressor that stops at the first
        // frame boundary would return two.
        var fixture = Fixture("multi-frame.definition.v1.dbn.frag.zst");
        var records = DecodeThroughStream(fixture);

        Assert.Equal(8, records.Count);
        Assert.All(records, record => Assert.Equal(InstrumentDefMsg.WireSize, record.Length));
    }

    [Fact]
    public void IsCompressed_ReflectsTheZstdFrameMagic()
    {
        Assert.All(TestFixtures.All, fixture =>
        {
            using var decoder = OpenFixture(fixture);
            Assert.Equal(fixture.IsCompressed, decoder.IsCompressed);
        });
    }

    [Fact]
    public void Constructor_RawStream_DoesNotConsumeTheBytesItPeeksAt()
    {
        // Detection reads four bytes to compare them against the zstd frame magic. On a raw DBN
        // stream those four bytes are "DBN" plus the version — if the peek swallowed them the
        // metadata decode below would fail outright rather than merely differ.
        var fixture = Fixture("test_data.mbo.dbn");
        using var source = new NonSeekableStream(TestFixtures.Read(fixture.Name));
        using var decoder = new DbnDecoder(source);

        Assert.False(decoder.IsCompressed);
        Assert.NotNull(decoder.Metadata);
        Assert.NotEmpty(decoder.Metadata!.Dataset);
        Assert.Equal(2, DrainRecords(decoder).Count);
    }

    [Fact]
    public void Constructor_TruncatedMetadata_Throws()
    {
        var whole = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.dbn"));
        using var truncated = new MemoryStream(whole.AsSpan(0, 20).ToArray());

        // A stream that stops inside its own header is not "the stream ended", it is broken.
        Assert.Throws<DbnDecodeException>(() => new DbnDecoder(truncated));
    }

    [Fact]
    public void TryNextRecord_TruncatedMidRecord_EndsCleanlyWithoutThrowing()
    {
        var whole = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn"));

        // Metadata, one whole MboMsg, and 20 bytes of the second.
        var metadataLength = DbnConstants.MetadataPreludeLength
            + BinaryPrimitives.ReadInt32LittleEndian(whole.AsSpan(4));
        var truncatedLength = metadataLength + MboMsg.WireSize + 20;
        Assert.True(truncatedLength < whole.Length);

        using var source = new MemoryStream(whole.AsSpan(0, truncatedLength).ToArray());
        using var decoder = new DbnDecoder(source);

        var records = DrainRecords(decoder);

        Assert.Single(records);
        Assert.Equal(MboMsg.WireSize, records[0].Length);

        // Still false, still not an exception, however many times it is asked.
        Assert.False(decoder.TryNextRecord(out _));
        Assert.False(decoder.TryNextRecord(out _));
    }

    [Fact]
    public void Constructor_ThatThrows_DisposesTheWrappersItBuiltAroundTheSourceStream()
    {
        // The constructor wraps the caller's stream in a PrefixedStream and, for a compressed
        // stream, a decompressor on top of that. If it then throws, the caller is holding no
        // reference to either — so it has to unwind them itself.
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.dbn"));
        bytes[0] = (byte)'X';

        var source = new DisposeTrackingStream(bytes);
        Assert.Throws<DbnDecodeException>(() => new DbnDecoder(source));
        Assert.True(source.WasDisposed, "The constructor threw without disposing the source stream.");
    }

    [Fact]
    public void Constructor_ThatThrowsWithLeaveOpen_LeavesTheSourceStreamOpen()
    {
        // The unwind must still honour leaveOpen: the wrappers are the constructor's to dispose,
        // the caller's stream is not.
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.dbn"));
        bytes[0] = (byte)'X';

        var source = new DisposeTrackingStream(bytes);
        Assert.Throws<DbnDecodeException>(() => new DbnDecoder(source, leaveOpen: true));
        Assert.False(source.WasDisposed);
    }

    [Fact]
    public void Constructor_CompressedStreamThatIsNotDbn_ThrowsAndDisposesTheDecompressorToo()
    {
        // A real zstd frame whose payload is not DBN: the failure happens after the decompressor
        // has been built, which is the case that used to leak it.
        var garbage = new byte[256];
        Random.Shared.NextBytes(garbage.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(garbage, 0xFD2FB528);

        var source = new DisposeTrackingStream(garbage);
        Assert.ThrowsAny<Exception>(() => new DbnDecoder(source));
        Assert.True(source.WasDisposed);
    }

    [Fact]
    public void Constructor_BadMagic_Throws()
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.dbn"));
        bytes[0] = (byte)'X';

        using var source = new MemoryStream(bytes);
        Assert.Throws<DbnDecodeException>(() => new DbnDecoder(source));
    }

    [Fact]
    public void Constructor_MetadataLengthNearIntMaxValue_ThrowsDbnDecodeExceptionRatherThanExhaustingMemory()
    {
        // The eight-byte repro: "DBN", version 3, and a declared metadata length of
        // 2,147,483,639. Before the DbnConstants.MaxMetadataLength ceiling this reached
        // AlignedBuffer.Grow, whose round-up (newSize + 7) / 8 wrapped negative and made
        // `new ulong[...]` throw OverflowException — not a DbnException, so a consumer writing
        // `catch (DbnException)` around the decoder would not have caught it. Neighbouring
        // lengths failed differently again (see the theory below), which is worse still: the
        // failure mode depended on which value the sender happened to pick.
        //
        // This is the first eight bytes of an unauthenticated live stream, which is what makes
        // it worth a test of its own rather than one row in a theory.
        byte[] repro = [0x44, 0x42, 0x4E, 0x03, 0xF7, 0xFF, 0xFF, 0x7F];

        using var source = new MemoryStream(repro);
        var exception = Assert.Throws<DbnDecodeException>(() => new DbnDecoder(source));

        Assert.Contains("2147483639", exception.Message, StringComparison.Ordinal);
        Assert.Contains("maximum metadata size", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DbnConstants.MaxMetadataLength + 1)]
    [InlineData(1_000_000_000)]
    [InlineData(2_147_483_632)]
    [InlineData(2_147_483_633)]
    [InlineData(2_147_483_639)]
    [InlineData(2_147_483_640)]
    [InlineData(int.MaxValue)]
    public void Process_MetadataLengthAboveTheCeiling_ThrowsDbnDecodeException(int declared)
    {
        // One row per band of the old behaviour, all of which are now the same
        // DbnDecodeException thrown before anything is allocated. Measured against the pre-fix
        // code, the bands were:
        //
        //   up to    2,147,483,632  no exception at all — Grow quietly allocated the declared
        //                           size, 1,000,032,000 bytes at a billion and 2,147,464,848 at
        //                           the top of the band, until the process ran out of memory;
        //   2,147,483,633 .. ,639   OverflowException — (length + 8 + 7) wrapped negative in
        //                           AlignedBuffer's round-up to a ulong count, so the array
        //                           length went negative;
        //   2,147,483,640 .. ,647   ArgumentOutOfRangeException — `length + 8` itself wrapped
        //                           negative, which Grow rejects as a negative size.
        //
        // Not one of them a DbnDecodeException, which is the only exception DbnDecoder's
        // constructor and DbnFsm.Process/TryNextRecord document for invalid DBN.
        var prelude = new byte[DbnConstants.MetadataPreludeLength];
        "DBN"u8.CopyTo(prelude);
        prelude[3] = DbnConstants.Version;
        BinaryPrimitives.WriteUInt32LittleEndian(prelude.AsSpan(4), (uint)declared);

        var fsm = new DbnFsm();
        var exception = Assert.Throws<DbnDecodeException>(() => FeedWhole(fsm, prelude));

        Assert.Contains(
            declared.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("maximum metadata size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePrelude_MetadataLengthAtTheCeiling_IsAccepted()
    {
        // The other half of the ceiling: 512 MiB exactly is legal and must survive the prelude,
        // which is what keeps an ALL_SYMBOLS definition header of a few hundred megabytes
        // decodable. Asserted at MetadataDecoder rather than by driving the FSM, because
        // driving the FSM this far would have it allocate the 512 MiB the block claims to need
        // — the acceptance is the assertion, not the allocation.
        var prelude = new byte[DbnConstants.MetadataPreludeLength];
        "DBN"u8.CopyTo(prelude);
        prelude[3] = DbnConstants.Version;
        BinaryPrimitives.WriteUInt32LittleEndian(prelude.AsSpan(4), DbnConstants.MaxMetadataLength);

        MetadataDecoder.DecodePrelude(prelude, out var version, out var length);

        Assert.Equal(DbnConstants.Version, version);
        Assert.Equal(DbnConstants.MaxMetadataLength, length);
    }

    [Fact]
    public void TryNextRecord_RecordLengthShorterThanTheHeader_Throws()
    {
        // One 32-bit word: four bytes, where a header alone is sixteen.
        var bytes = new byte[32];
        bytes[0] = 1;

        var fsm = new DbnFsm(skipMetadata: true, inputDbnVersion: DbnConstants.Version);
        var exception = Assert.Throws<DbnDecodeException>(() => FeedWhole(fsm, bytes));
        Assert.Contains("shorter than", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryNextRecord_RecordLengthOverTheMaximum_Throws()
    {
        // 255 words is 1020 bytes; the largest record the format can express is 528.
        var bytes = new byte[32];
        bytes[0] = 255;

        var fsm = new DbnFsm(skipMetadata: true, inputDbnVersion: DbnConstants.Version);
        var exception = Assert.Throws<DbnDecodeException>(() => FeedWhole(fsm, bytes));
        Assert.Contains("maximum record size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_UpgradeToV2AgainstAV3Stream_Throws()
    {
        using var source = new MemoryStream(TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn")));
        Assert.Throws<DbnDecodeException>(() => new DbnDecoder(source, VersionUpgradePolicy.UpgradeToV2));
    }

    [Fact]
    public void TryNextRecord_TsOutStream_ReadsTheTimestampAndStillMatchesTheRecordStruct()
    {
        // Hand-built rather than fixture-driven: no vendored fixture carries ts_out, and this is
        // the one place the wire length is the struct's size plus eight.
        const ulong SendTimestamp = 1_678_486_110_000_000_000;

        var record = new byte[TradeMsg.WireSize + sizeof(ulong)];
        record[0] = (byte)(record.Length / DbnConstants.RecordLengthMultiplier);
        record[1] = (byte)RType.Mbp0;
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(TradeMsg.WireSize), SendTimestamp);

        var fsm = new DbnFsm(
            skipMetadata: true,
            inputDbnVersion: DbnConstants.Version,
            tsOut: true);

        WriteWhole(fsm, record);
        Assert.True(fsm.TryNextRecord(out var decoded));

        Assert.Equal(record.Length, decoded.SizeInBytes);
        Assert.Equal(TradeMsg.WireSize, decoded.StructSize);
        Assert.True(decoded.HasTsOut);
        Assert.Equal(SendTimestamp, decoded.TsOut);

        // The +8 must never be compared against a bare WireSize: it is the struct size, not the
        // wire size, that identifies the record.
        Assert.True(decoded.Has<TradeMsg>());
    }

    [Fact]
    public void TryNextRecord_TsOutUpgrade_ProducesAV3RecordWithTheTimestampCarriedOver()
    {
        // The largest record this codec can ever produce: a v3 definition plus ts_out, exactly
        // the size the compat buffer is dimensioned for.
        const ulong SendTimestamp = 1_678_486_827_000_000_000;

        var v1 = TestFixtures.ReadDecompressed(Fixture("test_data.definition.v1.dbn.frag"))
            .AsSpan(0, InstrumentDefMsgV1.WireSize)
            .ToArray();

        var withTsOut = new byte[InstrumentDefMsgV1.WireSize + sizeof(ulong)];
        v1.CopyTo(withTsOut, 0);
        withTsOut[0] = (byte)(withTsOut.Length / DbnConstants.RecordLengthMultiplier);
        BinaryPrimitives.WriteUInt64LittleEndian(withTsOut.AsSpan(InstrumentDefMsgV1.WireSize), SendTimestamp);

        var fsm = new DbnFsm(skipMetadata: true, inputDbnVersion: 1, tsOut: true);
        WriteWhole(fsm, withTsOut);

        Assert.True(fsm.TryNextRecord(out var decoded));
        Assert.Equal(DbnConstants.MaxRecordLength, decoded.SizeInBytes);
        Assert.Equal(InstrumentDefMsg.WireSize, decoded.StructSize);
        Assert.True(decoded.Has<InstrumentDefMsg>());
        Assert.Equal(SendTimestamp, decoded.TsOut);
    }

    [Fact]
    public void Reset_ReturnsTheMachineToItsStartingStateAndDecodesTheStreamAgain()
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn"));

        var fsm = new DbnFsm();
        Assert.False(fsm.HasDecodedMetadata);

        var first = FeedWhole(fsm, bytes);
        Assert.True(fsm.HasDecodedMetadata);
        Assert.NotNull(fsm.Metadata);

        fsm.Reset();
        Assert.False(fsm.HasDecodedMetadata);
        Assert.Null(fsm.Metadata);
        Assert.Null(fsm.InputDbnVersion);

        var second = FeedWhole(fsm, bytes);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.True(first[i].AsSpan().SequenceEqual(second[i]));
        }
    }

    [Fact]
    public void Reset_OnAFragmentDecoder_ReturnsToTheRecordStateNotThePrelude()
    {
        // Upstream's reset() always returns to the prelude, which would leave a fragment decoder
        // waiting forever for magic bytes that are never coming.
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.definition.v3.dbn.frag"));

        var fsm = new DbnFsm(skipMetadata: true);
        Assert.Equal(2, FeedWhole(fsm, bytes).Count);

        fsm.Reset();

        Assert.True(fsm.HasDecodedMetadata);
        Assert.Equal(2, FeedWhole(fsm, bytes).Count);
    }

    [Fact]
    public void Process_ReportsMetadataSeparatelyFromRecords()
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.mbo.v3.dbn"));

        var fsm = new DbnFsm();
        Assert.Equal(ProcessStatus.NeedMoreData, fsm.Process(out var needed, out _));
        Assert.Equal(DbnConstants.MetadataPreludeLength, needed);

        WriteWhole(fsm, bytes);

        Assert.Equal(ProcessStatus.Metadata, fsm.Process(out _, out _));
        Assert.NotNull(fsm.Metadata);
        Assert.Equal(ProcessStatus.Record, fsm.Process(out _, out _));
        Assert.Equal(ProcessStatus.Record, fsm.Process(out _, out _));
        Assert.Equal(ProcessStatus.NeedMoreData, fsm.Process(out _, out _));
    }

    [Fact]
    public void Constructor_UnsupportedInputVersion_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DbnFsm(inputDbnVersion: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DbnFsm(inputDbnVersion: (byte)(DbnConstants.Version + 1)));
        Assert.Throws<DbnDecodeException>(
            () => new DbnFsm(VersionUpgradePolicy.UpgradeToV2, inputDbnVersion: 3));
    }

    /// <summary>
    /// Asserts that a hand-built v1-shaped record upgrades to <typeparamref name="TNew"/> both
    /// when the input version is stated and when it has to be inferred from the record's size.
    /// </summary>
    private static void AssertUpgrades<TOld, TNew>(RType rtype)
        where TOld : unmanaged, IRecord<TOld>
        where TNew : unmanaged, IRecord<TNew>
    {
        var record = new byte[TOld.WireSize];
        record[0] = (byte)(TOld.WireSize / DbnConstants.RecordLengthMultiplier);
        record[1] = (byte)rtype;

        var told = new DbnFsm(skipMetadata: true, inputDbnVersion: 1);
        var toldRecords = FeedWhole(told, record);
        Assert.Single(toldRecords);
        Assert.Equal(TNew.WireSize, toldRecords[0].Length);

        var inferred = new DbnFsm(skipMetadata: true);
        var inferredRecords = FeedWhole(inferred, record);
        Assert.Single(inferredRecords);
        Assert.Equal(TNew.WireSize, inferredRecords[0].Length);

        // A v1 statistic is indistinguishable from a v2 one, so the inference deliberately
        // declines to record a version for it; everything else pins v1.
        Assert.Equal<byte?>(rtype == RType.Statistics ? null : 1, inferred.InputDbnVersion);
    }

    private static DbnFixture Fixture(string name) =>
        TestFixtures.All.Single(fixture => string.Equals(fixture.Name, name, StringComparison.Ordinal));

    private static DbnDecoder OpenFixture(
        DbnFixture fixture,
        VersionUpgradePolicy policy = VersionUpgradePolicy.UpgradeToV3)
        => new(
            new MemoryStream(TestFixtures.Read(fixture.Name)),
            policy,
            skipMetadata: fixture.IsFragment);

    private static List<byte[]> DecodeThroughStream(
        DbnFixture fixture,
        VersionUpgradePolicy policy = VersionUpgradePolicy.UpgradeToV3)
    {
        using var decoder = OpenFixture(fixture, policy);
        return DrainRecords(decoder);
    }

    private static List<byte[]> DrainRecords(DbnDecoder decoder)
    {
        var records = new List<byte[]>();
        while (decoder.TryNextRecord(out var record))
        {
            records.Add(record.Bytes.ToArray());
        }

        return records;
    }

    /// <summary>Every byte handed over in one <c>Fill</c>, then drained.</summary>
    private static List<byte[]> DecodeInOneBulkFill(DbnFixture fixture)
    {
        var bytes = TestFixtures.ReadDecompressed(fixture);
        var fsm = new DbnFsm(
            skipMetadata: fixture.IsFragment,
            bufferSize: bytes.Length + DbnConstants.MaxRecordLength);

        bytes.AsSpan().CopyTo(fsm.Space());
        fsm.Fill(bytes.Length);

        var records = new List<byte[]>();
        while (fsm.TryNextRecord(out var record))
        {
            records.Add(record.Bytes.ToArray());
        }

        return records;
    }

    /// <summary>One byte per <c>Fill</c> — what a socket actually looks like on a bad day.</summary>
    /// <remarks>
    /// Deliberately paired with the smallest buffer the decoder allows, one maximum-size record.
    /// A 64 KiB buffer would swallow every one of these fixtures whole and never reclaim its
    /// consumed prefix, so the shift that re-bases the unconsumed tail — and with it the 8-byte
    /// alignment every reinterpret depends on — would go completely untested. At this size the
    /// shift happens between almost every pair of records.
    /// </remarks>
    private static List<byte[]> DecodeOneBytePerFill(DbnFixture fixture)
    {
        var bytes = TestFixtures.ReadDecompressed(fixture);
        var fsm = new DbnFsm(
            skipMetadata: fixture.IsFragment,
            bufferSize: DbnConstants.MaxRecordLength);
        var records = new List<byte[]>();

        foreach (var value in bytes)
        {
            fsm.Space()[0] = value;
            fsm.Fill(1);

            while (fsm.TryNextRecord(out var record))
            {
                records.Add(record.Bytes.ToArray());
            }
        }

        return records;
    }

    private static void WriteWhole(DbnFsm fsm, ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var space = fsm.Space();
            var take = Math.Min(space.Length, bytes.Length - offset);
            bytes.Slice(offset, take).CopyTo(space);
            fsm.Fill(take);
            offset += take;
        }
    }

    private static List<byte[]> FeedWhole(DbnFsm fsm, ReadOnlySpan<byte> bytes)
    {
        var records = new List<byte[]>();
        var offset = 0;

        while (true)
        {
            if (offset < bytes.Length)
            {
                var space = fsm.Space();
                var take = Math.Min(space.Length, bytes.Length - offset);
                bytes.Slice(offset, take).CopyTo(space);
                fsm.Fill(take);
                offset += take;
            }

            var decodedAny = false;
            while (fsm.TryNextRecord(out var record))
            {
                records.Add(record.Bytes.ToArray());
                decodedAny = true;
            }

            if (offset >= bytes.Length && !decodedAny)
            {
                return records;
            }
        }
    }

    /// <summary>A read-only stream that remembers whether it was disposed.</summary>
    private sealed class DisposeTrackingStream : Stream
    {
        private readonly byte[] _bytes;
        private int _position;

        public DisposeTrackingStream(byte[] bytes) => _bytes = bytes;

        public bool WasDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(buffer.Length, _bytes.Length - _position);
            if (take <= 0)
            {
                return 0;
            }

            _bytes.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A stream that hands over exactly one byte per read, however much is asked for.</summary>
    private sealed class SingleByteStream : Stream
    {
        private readonly byte[] _bytes;
        private int _position;

        public SingleByteStream(byte[] bytes) => _bytes = bytes;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= _bytes.Length)
            {
                return 0;
            }

            buffer[0] = _bytes[_position++];
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A forward-only stream, to prove the decoder never seeks — a live socket cannot.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly byte[] _bytes;
        private int _position;

        public NonSeekableStream(byte[] bytes) => _bytes = bytes;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(buffer.Length, _bytes.Length - _position);
            if (take <= 0)
            {
                return 0;
            }

            _bytes.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
