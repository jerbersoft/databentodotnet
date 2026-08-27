namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Asserts the guarantee this library exists for: decoding a record allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three documents state this property and, until now, nothing checked it.</b> ROADMAP.md §3
/// describes <c>ref readonly T rec = ref MemoryMarshal.AsRef&lt;T&gt;(span)</c> as the point of
/// the codec, M2's definition of done requires zero per-record allocation, and CLAUDE.md's
/// porting rules forbid adding a copy for API convenience on the low-level path. A boxed
/// <see cref="RecordRef"/>, a stray <c>ToArray()</c>, a lambda capture in the record loop or an
/// <see cref="IEnumerable{T}"/> seam added for convenience would each pass every other test in
/// this repository. This is
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/28">#28</see>.
/// </para>
/// <para>
/// <b>Steady state is the whole of the claim.</b> Buffer growth, metadata decode and symbol-map
/// population all allocate, legitimately and once. The claim is zero <em>per record</em>, so
/// every measurement here runs after a warm-up that has already paid those costs — and the
/// warm-up is not a formality: without it the first measured pass would charge the metadata block
/// to the records that followed it.
/// </para>
/// <para>
/// <b>The instrument is checked too.</b> A measurement harness that always reports zero would
/// pass every test in this file while proving nothing, so
/// <see cref="Measurement_NoticesADeliberateAllocationOnTheSamePath"/> runs the identical loop
/// with one copy added per record and requires the number to move. That is the same reasoning as
/// the mock gateway's own tests driving it with a deliberately malformed client.
/// </para>
/// <para>
/// <b><see cref="GC.GetAllocatedBytesForCurrentThread"/>, not
/// <see cref="GC.GetTotalAllocatedBytes"/></b>, because the test runner is running other tests on
/// other threads at the same time and a process-wide counter would measure them too. Everything
/// measured here is synchronous, so the thread cannot change underneath the measurement.
/// </para>
/// </remarks>
public class AllocationTests
{
    /// <summary>A v3 MBO fixture: the densest schema DBN defines, at the current version.</summary>
    /// <remarks>
    /// v3 with the default <see cref="VersionUpgradePolicy.UpgradeToV3"/> means no record needs
    /// upgrading, so the loop runs the same path a live session does. The upgrade path has its own
    /// measurement in <see cref="Corpus_DecodesEveryFixtureWithoutAllocatingPerRecord"/>, which
    /// sweeps versions 1 and 2 as well.
    /// </remarks>
    private const string MboFixture = "test_data.mbo.v3.dbn";

    /// <summary>Records decoded before measuring, to pay for buffer growth and JIT warm-up once.</summary>
    private const int WarmupRecords = 2_000;

    /// <summary>Records measured. Large enough that one byte per record would be unmissable.</summary>
    private const int MeasuredRecords = 20_000;

    [Fact]
    public void DbnFsm_DecodingASteadyStreamOfRecords_AllocatesExactlyNothing()
    {
        var stream = BuildRepeatedMboStream(WarmupRecords + MeasuredRecords);
        var fsm = new DbnFsm();
        var offset = 0;

        // Warm-up: the prelude, the metadata block, the buffer growth that the metadata forces,
        // and the JIT. All of it allocates, all of it exactly once per stream.
        var warmed = Pump(fsm, stream, ref offset, WarmupRecords, copyOut: false);
        Assert.Equal(WarmupRecords, warmed);

        Settle();
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();

        var decoded = Pump(fsm, stream, ref offset, MeasuredRecords, copyOut: false);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.Equal(MeasuredRecords, decoded);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DbnDecoder_ReadingASteadyStreamFromAStream_AllocatesExactlyNothingPerRecord()
    {
        // The same claim one layer up, over the surface a caller actually holds. DbnDecoder owns
        // the read loop that DbnFsm only supplies the buffer for, so a copy introduced there would
        // not show up in the test above.
        var bytes = BuildRepeatedMboStream(WarmupRecords + MeasuredRecords);

        using var source = new MemoryStream(bytes);
        using var decoder = new DbnDecoder(source);

        Assert.Equal(WarmupRecords, DrainDecoder(decoder, WarmupRecords));

        Settle();
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();

        var decoded = DrainDecoder(decoder, MeasuredRecords);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.Equal(MeasuredRecords, decoded);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Corpus_DecodesEveryFixtureWithoutAllocatingPerRecord()
    {
        // Breadth where the two tests above have depth: every record type upstream ships a fixture
        // for, at every DBN version, compressed and not — including the v1 and v2 records that go
        // through the upgrade path and its second buffer. The construction of each decoder is
        // outside the measurement because metadata, symbol maps and (for a .zst fixture) the
        // decompressor's own buffers all allocate once and legitimately.
        var offenders = new List<string>();

        foreach (var fixture in TestFixtures.All)
        {
            using var source = new MemoryStream(TestFixtures.Read(fixture.Name));
            using var decoder = new DbnDecoder(source, skipMetadata: fixture.IsFragment);

            // One record decoded before measuring: it is what forces the first read, and with it
            // whatever buffer the source stream or the decompressor rents on its first use.
            if (DrainDecoder(decoder, 1) == 0)
            {
                continue;
            }

            Settle();
            var before = GC.GetAllocatedBytesForCurrentThread();

            DrainDecoder(decoder, int.MaxValue);

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated != 0)
            {
                offenders.Add($"{fixture.Name}: {allocated} bytes");
            }
        }

        // Reported together rather than one Assert per fixture, so a change that regresses the
        // whole corpus says so instead of naming whichever file happened to be first.
        Assert.Empty(offenders);
    }

    [Fact]
    public void Measurement_NoticesADeliberateAllocationOnTheSamePath()
    {
        // The instrument's own test. Every assertion above is of the form "this number is zero",
        // which a broken measurement satisfies for free — so here the identical loop copies each
        // record out, and the number has to move by at least the bytes those copies cost.
        var stream = BuildRepeatedMboStream(WarmupRecords + MeasuredRecords);
        var fsm = new DbnFsm();
        var offset = 0;

        Assert.Equal(WarmupRecords, Pump(fsm, stream, ref offset, WarmupRecords, copyOut: true));

        Settle();
        var before = GC.GetAllocatedBytesForCurrentThread();

        var decoded = Pump(fsm, stream, ref offset, MeasuredRecords, copyOut: true);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(MeasuredRecords, decoded);
        Assert.True(
            allocated >= (long)MeasuredRecords * MboMsg.WireSize,
            $"A copy per record should cost at least {(long)MeasuredRecords * MboMsg.WireSize} bytes; "
            + $"the measurement reported {allocated}. The instrument is not measuring what these "
            + "tests assume it measures.");
    }

    // ----------------------------------------------------------------------------- Helpers

    /// <summary>
    /// Drives the state machine over <paramref name="stream"/> until it has produced
    /// <paramref name="records"/> records, feeding it in <see cref="DbnFsm.Space"/>-sized bites.
    /// </summary>
    /// <param name="fsm">The machine.</param>
    /// <param name="stream">The whole DBN stream to feed from.</param>
    /// <param name="offset">How far into <paramref name="stream"/> the feed has got. Advanced.</param>
    /// <param name="records">How many records to stop after.</param>
    /// <param name="copyOut">
    /// Whether to copy each record's bytes out — the deliberate allocation
    /// <see cref="Measurement_NoticesADeliberateAllocationOnTheSamePath"/> needs, and the reason
    /// this is a parameter rather than two near-identical loops.
    /// </param>
    /// <returns>How many records were decoded, which is fewer than asked only at end of stream.</returns>
    /// <remarks>
    /// A plain method rather than something taking a callback: a delegate would be one allocation,
    /// and a lambda that captured anything would be two — inside the very loop these tests exist
    /// to prove allocates nothing.
    /// </remarks>
    private static int Pump(DbnFsm fsm, byte[] stream, ref int offset, int records, bool copyOut)
    {
        var decoded = 0;

        while (decoded < records)
        {
            while (decoded < records && fsm.TryNextRecord(out var record))
            {
                if (copyOut)
                {
                    _ = record.Bytes.ToArray();
                }

                decoded++;
            }

            if (decoded >= records || offset >= stream.Length)
            {
                break;
            }

            var space = fsm.Space();
            var take = Math.Min(space.Length, stream.Length - offset);
            stream.AsSpan(offset, take).CopyTo(space);
            fsm.Fill(take);
            offset += take;
        }

        return decoded;
    }

    private static int DrainDecoder(DbnDecoder decoder, int records)
    {
        var decoded = 0;
        while (decoded < records && decoder.TryNextRecord(out _))
        {
            decoded++;
        }

        return decoded;
    }

    /// <summary>
    /// A DBN stream carrying <paramref name="records"/> MBO records: the fixture's own metadata
    /// block, then the fixture's records repeated until the count is met.
    /// </summary>
    /// <remarks>
    /// Repeated rather than generated, so the bytes being decoded are Databento's own rather than
    /// this repository's idea of what an MBO record looks like. Upstream's fixtures carry two
    /// records each — enough to prove a decoder works and far too few to distinguish a per-record
    /// allocation from a one-off.
    /// </remarks>
    private static byte[] BuildRepeatedMboStream(int records)
    {
        var fixtureBytes = TestFixtures.Read(MboFixture);

        using var fixtureSource = new MemoryStream(fixtureBytes);
        using var fixtureDecoder = new DbnDecoder(fixtureSource);

        var metadata = fixtureDecoder.Metadata;
        Assert.NotNull(metadata);

        var template = new List<byte[]>();
        while (fixtureDecoder.TryNextRecord(out var record))
        {
            Assert.True(record.Has<MboMsg>(), $"{MboFixture} should decode as MboMsg.");
            template.Add(record.Bytes.ToArray());
        }

        Assert.NotEmpty(template);

        using var stream = new MemoryStream();
        stream.Write(MetadataEncoder.Encode(metadata));

        for (var i = 0; i < records; i++)
        {
            stream.Write(template[i % template.Count]);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Collects everything the setup left behind, so the measurement that follows starts from a
    /// quiet heap and cannot be charged for a collection someone else provoked.
    /// </summary>
    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
