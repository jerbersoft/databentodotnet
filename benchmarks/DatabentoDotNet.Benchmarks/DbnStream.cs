using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live.Tests;

namespace DatabentoDotNet.Benchmarks;

/// <summary>
/// Builds the DBN byte stream the decode benchmarks run over: a metadata block followed by
/// synthetic MBO records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated rather than read from the vendored corpus,</b> unlike the allocation assertions in
/// the test suite. BenchmarkDotNet compiles and runs a fresh project per session, in a directory
/// of its own, so a fixture resolved relative to <see cref="AppContext.BaseDirectory"/> is not
/// where the harness will look for it. Generating the stream removes the file system from the
/// measurement entirely, which a throughput number wants anyway.
/// </para>
/// <para>
/// The corpus is still covered where coverage is what matters — <c>AllocationTests</c> sweeps all
/// 71 fixtures, every record type at every DBN version. This file is about how fast one hot path
/// runs, not about how many shapes it handles.
/// </para>
/// </remarks>
internal static class DbnStream
{
    /// <summary>
    /// <paramref name="records"/> MBO records and nothing else — a DBN <em>fragment</em>, with no
    /// magic prelude and no metadata block.
    /// </summary>
    /// <param name="records">How many records to write.</param>
    /// <returns>The records' bytes, back to back.</returns>
    /// <remarks>
    /// This is what the per-record benchmark runs over, and the omission is the point. A metadata
    /// block allocates — a <see cref="Metadata"/> object, its strings, its symbol mappings — once
    /// per session, and a benchmark that decoded one on every invocation would report that
    /// one-time cost divided by the record count, which reads exactly like a small per-record
    /// allocation and is not one.
    /// </remarks>
    public static byte[] MboFragment(int records)
    {
        using var stream = new MemoryStream();

        for (var i = 0; i < records; i++)
        {
            var record = SyntheticMbo.Record((uint)(i + 1));
            stream.Write(Raw(in record));
        }

        return stream.ToArray();
    }

    /// <summary>
    /// A complete DBN v3 stream carrying <paramref name="records"/> MBO records.
    /// </summary>
    /// <param name="records">How many records to write after the metadata block.</param>
    /// <returns>The stream's bytes.</returns>
    public static byte[] Mbo(int records)
    {
        var metadata = new Metadata
        {
            Version = DbnConstants.Version,
            Dataset = "XNAS.ITCH",
            Schema = Schema.Mbo,
            Start = SyntheticMbo.FirstTsRecv,
            StypeIn = SType.RawSymbol,
            StypeOut = SType.InstrumentId,
            TsOut = false,
            SymbolCstrLength = Metadata.SymbolCstrLengthForVersion(DbnConstants.Version),
        };

        using var stream = new MemoryStream();
        stream.Write(MetadataEncoder.Encode(metadata));

        for (var i = 0; i < records; i++)
        {
            var record = SyntheticMbo.Record((uint)(i + 1));
            stream.Write(Raw(in record));
        }

        return stream.ToArray();
    }

    private static ReadOnlySpan<byte> Raw(in MboMsg record) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<MboMsg>(in record));
}
