using System.Net;
using System.Net.Sockets;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// The asynchronous read seam
/// (<see href="https://github.com/jerbersoft/databentodotnet/issues/15">#15</see>): a DBN stream
/// arriving on a real TCP socket is decoded straight out of the state machine's own buffer, with
/// no copy interposed anywhere between the socket and the record.
/// </summary>
/// <remarks>
/// <para>
/// <b>What forced a decision.</b> <see cref="DbnFsm.Space"/> returns a <see cref="Span{T}"/>, and
/// there is no <c>ReadAsync(Span&lt;byte&gt;)</c> overload in .NET — nor can there be, since a
/// <c>ref struct</c> may not be preserved across an <c>await</c>. Nor is there a one-line
/// workaround: <see cref="AlignedBuffer"/> must back itself with a <c>ulong[]</c> for 8-byte
/// alignment, and a <c>ulong[]</c> has no <see cref="Memory{T}"/> reinterpret cast the way it has
/// a span one. <see cref="DbnFsm.SpaceMemory"/> is the answer: a
/// <see cref="System.Buffers.MemoryManager{T}"/> owned by the buffer projects the same bytes as a
/// <see cref="Memory{T}"/>.
/// </para>
/// <para>
/// <b>The second constraint, which is the one that shapes the M2 public API.</b>
/// <see cref="RecordRef"/> is a <c>ref struct</c>. A local of one is perfectly legal inside an
/// <c>async</c> method — only <em>surviving an await</em> is rejected, as CS4007 — so
/// <see cref="DecodeOverLoopbackAsync"/> below awaits the read and drains records in the same
/// method, exactly as upstream's own example does. What is impossible is <em>returning</em> one:
/// no <c>async</c> method can return a <c>ref struct</c>, so upstream's single-call
/// <c>LiveClient::next_record()</c> has no .NET equivalent on the zero-copy path and its
/// <c>fill_buf()</c> / <c>try_next_record()</c> pair is the shape M2 takes.
/// </para>
/// <para>
/// <b>How "no copy" is proved: three claims, three tests, none of them sufficient alone.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The <see cref="Memory{T}"/> really is the aligned buffer.</b>
/// <c>AlignedBufferTests.SpaceMemory_PinsToTheSameAddressAsSpace_AndIs8ByteAligned</c> pins what
/// an asynchronous read would be handed and compares the raw pointer against the span's, so the
/// read target is the state machine's own storage rather than a detached view copied back
/// afterwards.
/// </description></item>
/// <item><description>
/// <b>Nothing in the read path interposes a buffer of its own.</b>
/// <see cref="ReadPathsTakeTheSpanAndMemoryOverloads_SoNothingIsCopiedOnTheWayIn"/>.
/// </description></item>
/// <item><description>
/// <b>Records are served out of that buffer.</b> The socket tests capture the whole buffer before
/// a single byte arrives and assert every decoded record's bytes overlap it. This is the weakest
/// of the three taken alone — a copy that happened to land in the buffer would overlap too — and
/// its real job is to catch a record served from anywhere else.
/// </description></item>
/// </list>
/// </remarks>
public class AsyncReadSeamTests
{
    /// <summary>
    /// A ceiling, not an expectation. Every case here transfers a few kilobytes over loopback and
    /// finishes in single-digit milliseconds; this only stops a broken seam from hanging CI.
    /// </summary>
    private const int TimeoutMilliseconds = 30_000;

    /// <summary>
    /// Deliberately not a round number and far below any record size, so records straddle reads.
    /// </summary>
    private const int ChunkBytes = 7;

    /// <summary>
    /// The whole vendored corpus, over a real socket, against the synchronous decoder as oracle.
    /// </summary>
    /// <remarks>
    /// <see cref="VersionUpgradePolicy.AsIs"/> is load-bearing for the overlap assertion, not an
    /// arbitrary choice: an upgraded record is rebuilt in the machine's <em>compat</em> buffer, so
    /// it legitimately does not overlap the read buffer. Under <c>AsIs</c> every record comes
    /// from the read buffer, which makes "overlaps the buffer the socket wrote into" true for all
    /// 71 fixtures with no exceptions to carve out.
    /// <see cref="Upgrades_OverALoopbackSocket_DecodeThroughTheSameSeam"/> covers the other path.
    /// </remarks>
    [Fact]
    public async Task Corpus_OverALoopbackSocket_MatchesTheSyncDecoderAndNeverCopies()
    {
        using var cts = new CancellationTokenSource(TimeoutMilliseconds);

        foreach (var fixture in TestFixtures.All)
        {
            var raw = TestFixtures.Read(fixture.Name);

            var expected = DecodeSynchronously(raw, fixture, VersionUpgradePolicy.AsIs);
            var actual = await DecodeOverLoopbackAsync(
                raw, fixture, VersionUpgradePolicy.AsIs, requireZeroCopy: true, cts.Token);

            Assert.True(
                expected.Count == actual.Count,
                $"{fixture.Name}: the sync decoder yielded {expected.Count} records, the socket seam {actual.Count}.");

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.True(
                    expected[i].AsSpan().SequenceEqual(actual[i]),
                    $"{fixture.Name}: record {i} differs between the sync decoder and the socket seam.");
            }
        }
    }

    /// <summary>
    /// The upgrade path over the same seam. No overlap assertion here — see the remarks on
    /// <see cref="Corpus_OverALoopbackSocket_MatchesTheSyncDecoderAndNeverCopies"/>.
    /// </summary>
    [Fact]
    public async Task Upgrades_OverALoopbackSocket_DecodeThroughTheSameSeam()
    {
        using var cts = new CancellationTokenSource(TimeoutMilliseconds);

        foreach (var fixture in TestFixtures.All.Where(f => f.Version is 1 or 2))
        {
            var raw = TestFixtures.Read(fixture.Name);

            var expected = DecodeSynchronously(raw, fixture, VersionUpgradePolicy.UpgradeToV3);
            var actual = await DecodeOverLoopbackAsync(
                raw, fixture, VersionUpgradePolicy.UpgradeToV3, requireZeroCopy: false, cts.Token);

            Assert.True(
                expected.Count == actual.Count,
                $"{fixture.Name}: the sync decoder yielded {expected.Count} records, the socket seam {actual.Count}.");

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.True(
                    expected[i].AsSpan().SequenceEqual(actual[i]),
                    $"{fixture.Name}: record {i} differs between the sync decoder and the socket seam.");
            }
        }
    }

    /// <summary>
    /// The half of the no-copy claim a socket test cannot make: that nothing on the way in
    /// interposes a buffer of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Stream"/>'s base implementations of the span and memory overloads rent a
    /// <c>byte[]</c> from <see cref="System.Buffers.ArrayPool{T}"/>, read into that, and copy the
    /// result across. A stream in the read path that does not override them therefore adds a full
    /// buffer copy per read — silently, with no API change to notice.
    /// </para>
    /// <para>
    /// This asserts against a third-party type on purpose. The claim written down in
    /// <c>PORTING.md</c> — that a compressed live session decompresses <em>directly into</em> the
    /// state machine's buffer — is true only while <c>ZstdSharp</c> overrides these four methods,
    /// and a package bump could take that away without breaking a single other test.
    /// <see cref="NetworkStream"/> is checked in the same breath because it is the uncompressed
    /// path's entire read surface.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReadPathsTakeTheSpanAndMemoryOverloads_SoNothingIsCopiedOnTheWayIn()
    {
        AssertOverridesSpanAndMemoryReads(typeof(ZstdSharp.DecompressionStream));
        AssertOverridesSpanAndMemoryReads(typeof(NetworkStream));
    }

    /// <summary>
    /// <see cref="DbnFsm.Space"/> and <see cref="DbnFsm.SpaceMemory"/> must describe the same
    /// bytes: a caller is free to read synchronously on one iteration and asynchronously on the
    /// next, and a divergence would corrupt the stream rather than fail loudly.
    /// </summary>
    /// <remarks>
    /// Close to a tautology today, because <see cref="DbnFsm.Space"/> is implemented as
    /// <c>SpaceMemory().Span</c> — which is the point. Deriving one from the other is what makes
    /// them agree; this is the guard for the day someone un-derives them to save a virtual call.
    /// </remarks>
    [Fact]
    public void SpaceAndSpaceMemory_DescribeTheSameBytes()
    {
        var fsm = new DbnFsm();
        AssertSameBytes(fsm);

        // And still after the machine has advanced, where `end` is no longer 0.
        var bytes = TestFixtures.ReadDecompressed(
            TestFixtures.All.First(f => f.Name == "test_data.mbo.v3.dbn"));
        bytes.AsSpan().CopyTo(fsm.Space());
        fsm.Fill(bytes.Length);
        Assert.True(fsm.TryNextRecord(out _));

        AssertSameBytes(fsm);

        // Equal lengths plus a write through one showing up through the other pins both ends of
        // the slice: same start address, same length.
        static void AssertSameBytes(DbnFsm fsm)
        {
            Assert.Equal(fsm.Space().Length, fsm.SpaceMemory().Length);

            fsm.SpaceMemory().Span[0] = 0xAB;
            Assert.Equal(0xAB, fsm.Space()[0]);

            fsm.Space()[^1] = 0xCD;
            Assert.Equal(0xCD, fsm.SpaceMemory().Span[^1]);
        }
    }

    /// <summary>
    /// <see cref="DbnFsm.SpaceMemory"/> reclaims the consumed prefix exactly as
    /// <see cref="DbnFsm.Space"/> does — it is the same call underneath, and an async caller that
    /// silently got a smaller tail would stall on a large record forever.
    /// </summary>
    [Fact]
    public void SpaceMemory_ReclaimsTheConsumedPrefixLikeSpace()
    {
        var fsm = new DbnFsm(skipMetadata: true, inputDbnVersion: DbnConstants.Version);

        // Fill the buffer to within less than a max-length record of the end, then consume most
        // of it, so only a shift can hand back a full record's worth of contiguous room.
        var record = BuildTradeRecord();
        var space = fsm.SpaceMemory();
        var written = 0;
        while (written + record.Length <= space.Length)
        {
            record.CopyTo(space.Span[written..]);
            written += record.Length;
        }

        fsm.Fill(written);
        while (fsm.TryNextRecord(out _))
        {
        }

        Assert.True(
            fsm.SpaceMemory().Length >= DbnConstants.MaxRecordLength,
            "SpaceMemory did not reclaim the consumed prefix.");
    }

    private static void AssertOverridesSpanAndMemoryReads(Type streamType)
    {
        AssertDeclares(streamType, "Read", typeof(Span<byte>));
        AssertDeclares(streamType, "ReadAsync", typeof(Memory<byte>), typeof(CancellationToken));

        static void AssertDeclares(Type type, string name, params Type[] parameters)
        {
            var method = type.GetMethod(name, parameters);
            Assert.True(
                method?.DeclaringType == type,
                $"{type.Name} does not override {name}({string.Join(", ", parameters.Select(p => p.Name))}), "
                + "so Stream's base implementation copies through a rented array on every read.");
        }
    }

    /// <summary>
    /// The oracle: the same bytes through the synchronous decoder, which is covered end to end by
    /// <see cref="DbnDecoderTests"/> against upstream's own record counts.
    /// </summary>
    private static List<byte[]> DecodeSynchronously(
        byte[] raw, DbnFixture fixture, VersionUpgradePolicy upgradePolicy)
    {
        using var decoder = new DbnDecoder(
            new MemoryStream(raw), upgradePolicy, skipMetadata: fixture.IsFragment);

        var records = new List<byte[]>();
        while (decoder.TryNextRecord(out var record))
        {
            records.Add(record.Bytes.ToArray());
        }

        return records;
    }

    /// <summary>
    /// Serves <paramref name="raw"/> over a loopback TCP socket in <see cref="ChunkBytes"/>-sized
    /// writes and decodes it through the async seam.
    /// </summary>
    /// <remarks>
    /// This is the shape the M2 live client takes: await a read into
    /// <see cref="DbnFsm.SpaceMemory"/>, <see cref="DbnFsm.Fill"/>, then drain synchronously.
    /// Nothing here may declare a <c>ref struct</c> local — see the remarks on the class.
    /// </remarks>
    private static async Task<List<byte[]>> DecodeOverLoopbackAsync(
        byte[] raw,
        DbnFixture fixture,
        VersionUpgradePolicy upgradePolicy,
        bool requireZeroCopy,
        CancellationToken cancellationToken)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        var serving = ServeAsync(listener, raw, cancellationToken);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            // Off by default anyway, but stated: Nagle coalescing the small writes would hide the
            // split reads this test exists to survive.
            NoDelay = true,
        };
        await client.ConnectAsync(endpoint, cancellationToken);

        await using var network = new NetworkStream(client, ownsSocket: false);
        await using var stream = fixture.IsCompressed
            ? new ZstdSharp.DecompressionStream(network, leaveOpen: true)
            : (Stream)network;

        var fsm = new DbnFsm(upgradePolicy, skipMetadata: fixture.IsFragment);

        // Taken from a machine that has seen no bytes, so `end` is 0 and this is the entire read
        // buffer. It stays valid for the whole loop: shifting moves bytes within the array and
        // never replaces it, and nothing here grows the buffer.
        var wholeBuffer = fsm.SpaceMemory();
        Assert.Equal(DbnFsm.DefaultBufferSize, wholeBuffer.Length);

        var records = new List<byte[]>();
        while (true)
        {
            var read = await stream.ReadAsync(fsm.SpaceMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            fsm.Fill(read);

            // The drain sits in the async method itself. `record` is a ref struct local, which is
            // legal here precisely because it does not reach the await at the top of the next
            // iteration — the compiler rejects that with CS4007, which is the same one-record-
            // at-a-time lifetime DbnFsm documents by hand.
            while (fsm.TryNextRecord(out var record))
            {
                if (requireZeroCopy)
                {
                    Assert.True(
                        wholeBuffer.Span.Overlaps(record.Bytes),
                        $"{fixture.Name}: the record does not point into the buffer the socket "
                        + "wrote into — a copy was interposed.");
                }

                records.Add(record.Bytes.ToArray());
            }
        }

        await serving;
        return records;
    }

    private static async Task ServeAsync(Socket listener, byte[] payload, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptAsync(cancellationToken);
        connection.NoDelay = true;

        for (var offset = 0; offset < payload.Length; offset += ChunkBytes)
        {
            var length = Math.Min(ChunkBytes, payload.Length - offset);
            await connection.SendAsync(payload.AsMemory(offset, length), SocketFlags.None, cancellationToken);

            // Give the reader a chance to observe the partial write rather than letting the
            // stack coalesce every chunk into one segment.
            await Task.Yield();
        }

        connection.Shutdown(SocketShutdown.Send);
    }

    /// <summary>A minimal, current-version <see cref="TradeMsg"/>: bytes, not meaning.</summary>
    private static byte[] BuildTradeRecord()
    {
        var bytes = new byte[TradeMsg.WireSize];
        bytes[0] = (byte)(TradeMsg.WireSize / DbnConstants.RecordLengthMultiplier);
        bytes[1] = (byte)RType.Mbp0;
        return bytes;
    }
}
