using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Tests;
using DatabentoDotNet.Historical;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// <see cref="TimeseriesReader"/> over a vendored <c>.dbn</c> file, inside the native binary.
/// </summary>
/// <remarks>
/// <para>
/// <c>timeseries.get_range</c> streams DBN over HTTP and costs money, and
/// <see cref="TimeseriesClient.OpenFileAsync"/> is the same reader pointed at a file — which makes
/// it the one way to run the historical package's decode path offline. It is worth running
/// separately from the codec's own: this reader is a distinct async wrapper with its own buffer and
/// its own <c>IAsyncEnumerable</c>, and it lives in a different assembly, so ILC compiles it as
/// separate code.
/// </para>
/// <para>
/// Both routes through the reader are exercised. <see cref="TimeseriesReader.ReadRecordsAsync"/>
/// allocates an <c>OwnedRecord</c> per record and is the convenient one; the
/// <c>FillBufferAsync</c>/<c>TryNextRecord</c> pair is the zero-copy one. They must return the same
/// count, from the same file, on the same run.
/// </para>
/// </remarks>
internal static class HistoricalFileProbe
{
    /// <summary>
    /// Two vendored files, both <c>.dbn.zst</c>. That is not a narrowing: this reader is documented
    /// as opening what <c>GetRangeToFileAsync</c> writes, and what that writes is always zstd —
    /// <c>TimeseriesClient.OpenFileAsync</c> wraps the file in the decompressor unconditionally
    /// rather than sniffing for a frame. An uncompressed fixture here would be testing a case the
    /// API does not offer.
    /// </summary>
    private static readonly string[] Fixtures = ["test_data.mbo.v3.dbn.zst", "test_data.mbp-10.v3.dbn.zst"];

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        ProbeReport.Section("historical: TimeseriesReader over a vendored file");

        foreach (var fixture in Fixtures)
        {
            var expected = ExpectedRecordCounts.ByFixture[fixture];
            var path = Path.Combine(DbnProbe.DataDirectory, fixture);

            var owned = 0;
            await using (var reader = await TimeseriesClient.OpenFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                report.Require(reader.Metadata is not null, $"{fixture}: the reader decoded a metadata block");

                await foreach (var record in reader.ReadRecordsAsync(cancellationToken).ConfigureAwait(false))
                {
                    owned++;
                    report.Require(record.SizeInBytes > 0, $"{fixture}: record {owned} carries bytes");
                }
            }

            var zeroCopy = 0;
            await using (var reader = await TimeseriesClient.OpenFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    while (reader.TryNextRecord(out _))
                    {
                        zeroCopy++;
                    }

                    if (await reader.FillBufferAsync(cancellationToken).ConfigureAwait(false) == 0)
                    {
                        break;
                    }
                }

                while (reader.TryNextRecord(out _))
                {
                    zeroCopy++;
                }
            }

            report.RequireEqual(expected, owned, $"{fixture}: ReadRecordsAsync record count");
            report.RequireEqual(expected, zeroCopy, $"{fixture}: FillBufferAsync/TryNextRecord record count");
            ProbeReport.Note($"{fixture}: {owned} records both ways.");
        }
    }
}
