using System.Runtime.CompilerServices;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Tests;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// The codec, inside the native binary: the whole vendored corpus decoded, records reinterpreted
/// as typed structs, and <see cref="DbnTime"/>'s crossing into NodaTime.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corpus check is the one the milestone's definition of done names.</b> It decodes all 71
/// vendored fixtures and requires the record count upstream's <c>dbn</c> CLI reports for each —
/// from <see cref="ExpectedRecordCounts.ByFixture"/>, the same table <c>DbnDecoderTests</c> asserts
/// against under the managed runtime, compiled into this program rather than copied.
/// </para>
/// <para>
/// <b>The typed reads are the AOT-specific half.</b> Counting records only proves the framing was
/// read; <c>RecordRef.Get&lt;T&gt;</c> is what reinterprets the buffer as a struct, and it resolves
/// <c>T.HasRType</c> and <c>T.WireSize</c> through C# static abstract interface members. That is
/// generic dispatch over value types with no reflection behind it — precisely the construct ILC has
/// to instantiate ahead of time, and precisely the one that cannot be checked by publishing without
/// running. Each type also cross-checks <see cref="Unsafe.SizeOf{T}"/> against the struct's own
/// declared <c>WireSize</c>: the CLR's layout against the wire's, with no hand-copied size table.
/// </para>
/// </remarks>
internal static class DbnProbe
{
    /// <summary>The vendored corpus, copied beside the binary by the project file.</summary>
    public static string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");

    public static void Run(ProbeReport report)
    {
        ProbeReport.Section("dbn: the vendored corpus");
        DecodeCorpus(report);

        ProbeReport.Section("dbn: records reinterpreted as typed structs");
        TypedReads(report);

        ProbeReport.Section("dbn: the NodaTime crossing");
        TimeCrossing(report);
    }

    /// <summary>Opens a fixture the way <c>DbnDecoderTests</c> does: raw bytes, fragments unwrapped by name.</summary>
    public static DbnDecoder Open(string fixture) =>
        new(
            new MemoryStream(File.ReadAllBytes(Path.Combine(DataDirectory, fixture))),
            VersionUpgradePolicy.UpgradeToV3,
            skipMetadata: fixture.Contains(".dbn.frag", StringComparison.Ordinal));

    private static void DecodeCorpus(ProbeReport report)
    {
        // The corpus must be complete before its counts mean anything. A fixture that failed to
        // copy would otherwise be a missing key rather than a wrong answer, and a probe that
        // decoded 70 of 71 files correctly would report success.
        var onDisk = Directory
            .EnumerateFiles(DataDirectory, "*.dbn*")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        report.Require(
            onDisk.SetEquals(ExpectedRecordCounts.ByFixture.Keys),
            $"the corpus beside the binary is the one the counts describe "
                + $"({onDisk.Count} files on disk, {ExpectedRecordCounts.ByFixture.Count} expected)");

        var records = 0;
        var bytes = 0L;
        foreach (var (name, expected) in ExpectedRecordCounts.ByFixture.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            using var decoder = Open(name);

            var decoded = 0;
            while (decoder.TryNextRecord(out var record))
            {
                decoded++;
                bytes += record.SizeInBytes;
            }

            report.RequireEqual(expected, decoded, $"{name}: record count");
            records += decoded;
        }

        ProbeReport.Note($"{ExpectedRecordCounts.ByFixture.Count} fixtures, {records} records, {bytes} bytes of record payload.");
    }

    private static void TypedReads(ProbeReport report)
    {
        // One fixture per struct family, chosen so every record in the file is the named type: the
        // check is "every record read back as T", which a mixed file could not make.
        var matched = 0;
        matched += ReadAs<MboMsg>(report, "test_data.mbo.v3.dbn");
        matched += ReadAs<TradeMsg>(report, "test_data.trades.dbn");
        matched += ReadAs<Mbp1Msg>(report, "test_data.mbp-1.dbn");
        matched += ReadAs<Mbp10Msg>(report, "test_data.mbp-10.dbn");
        matched += ReadAs<BboMsg>(report, "test_data.bbo-1s.dbn");
        matched += ReadAs<Cmbp1Msg>(report, "test_data.cmbp-1.dbn");
        matched += ReadAs<CbboMsg>(report, "test_data.cbbo-1s.dbn");
        matched += ReadAs<OhlcvMsg>(report, "test_data.ohlcv-1m.dbn");
        matched += ReadAs<ImbalanceMsg>(report, "test_data.imbalance.dbn");
        matched += ReadAs<StatMsg>(report, "test_data.statistics.dbn");
        matched += ReadAs<StatusMsg>(report, "test_data.status.dbn");
        matched += ReadAs<InstrumentDefMsg>(report, "test_data.definition.dbn");

        ProbeReport.Note($"{matched} records reinterpreted through Get<T>() across 12 record structs.");
    }

    /// <summary>
    /// Requires every record in <paramref name="fixture"/> to read back as a
    /// <typeparamref name="T"/>, and the struct's CLR size to equal its declared wire size.
    /// </summary>
    /// <returns>How many records were reinterpreted.</returns>
    private static int ReadAs<T>(ProbeReport report, string fixture)
        where T : unmanaged, IRecord<T>
    {
        var name = typeof(T).Name;
        report.RequireEqual(T.WireSize, Unsafe.SizeOf<T>(), $"{name}: Unsafe.SizeOf against the declared WireSize");

        var matched = 0;
        var total = 0;
        var agreed = true;

        using var decoder = Open(fixture);
        while (decoder.TryNextRecord(out var record))
        {
            total++;
            if (!record.Has<T>())
            {
                continue;
            }

            matched++;

            // Two routes to the same number: the decoder's own rtype dispatch, and the struct's
            // IndexTs read straight off the reinterpreted memory. They agree or the reinterpret
            // landed at the wrong offset — which is silent corruption, not an exception, and is
            // the entire reason the layout is asserted anywhere at all.
            agreed &= record.Get<T>().IndexTs == record.IndexTs;
        }

        report.RequireEqual(total, matched, $"{fixture}: every record reads back as {name}");
        report.Require(agreed, $"{fixture}: {name}.IndexTs agrees with RecordRef.IndexTs on every record");
        report.Require(total > 0, $"{fixture}: holds records to reinterpret at all");
        return matched;
    }

    private static void TimeCrossing(ProbeReport report)
    {
        // The sentinel. CLAUDE.md's "Dates and times" exists because ulong.MaxValue converts to a
        // confidently wrong answer through the obvious cast; these are the two paths that refuse.
        report.Require(DbnTime.IsUndefined(DbnConstants.UndefTimestamp), "UndefTimestamp is undefined");
        report.Require(!DbnTime.TryToInstant(DbnConstants.UndefTimestamp, out _), "TryToInstant refuses the sentinel");
        report.Require(!DbnTime.TryToUtcDate(DbnConstants.UndefTimestamp, out _), "TryToUtcDate refuses the sentinel");

        var threw = false;
        try
        {
            DbnTime.ToInstant(DbnConstants.UndefTimestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        report.Require(threw, "ToInstant throws on the sentinel");

        // One nanosecond below the sentinel is an ordinary timestamp in 2554, and it is past what a
        // long count of nanoseconds can hold — so it is the value that proves the days-plus-
        // remainder split rather than a single long division.
        const ulong Highest = DbnConstants.UndefTimestamp - 1;
        report.Require(DbnTime.TryToInstant(Highest, out var highest), "the highest non-sentinel value converts");
        report.RequireEqual(
            Instant.FromUtc(2554, 7, 21, 23, 34, 33).PlusNanoseconds(709_551_614),
            highest,
            "ulong.MaxValue - 1 as an Instant");
        report.RequireEqual(Highest, DbnTime.ToUnixNanoseconds(highest), "and back to nanoseconds, exactly");

        // A hundred-nanosecond DateTime tick cannot hold this; an Instant can. The trailing 1 ns is
        // the whole point of the convention, so the probe carries it into the native binary too.
        const ulong Exact = 1_609_160_400_000_000_001UL;
        report.RequireEqual(Exact, DbnTime.ToUnixNanoseconds(DbnTime.ToInstant(Exact)), "a 1 ns remainder round-trips");
        report.RequireEqual(
            new LocalDate(2020, 12, 28),
            DbnTime.ToUtcDate(Exact),
            "and floors to the right UTC date");

        ProbeReport.Note($"highest representable timestamp: {InstantPattern.ExtendedIso.Format(highest)} ({Highest} ns).");
    }
}
