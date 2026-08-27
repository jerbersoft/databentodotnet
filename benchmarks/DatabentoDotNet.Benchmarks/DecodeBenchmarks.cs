using BenchmarkDotNet.Attributes;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Benchmarks;

/// <summary>
/// Decoding throughput and allocated bytes on the file path: <see cref="DbnFsm"/> driven
/// directly, and <see cref="DbnDecoder"/> over a stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read the <c>Allocated</c> column against what each row measures, and do not divide it by
/// <see cref="Records"/>.</b> Only the first row is a per-record measurement, and it is the one
/// that should say 0 B: records are reinterpreted in place over the read buffer —
/// <c>ref readonly T rec = ref MemoryMarshal.AsRef&lt;T&gt;(span)</c> — so decoding one costs no
/// managed memory at all.
/// </para>
/// <para>
/// The other two rows measure a whole session each, one-time costs included, and their figures do
/// not divide into a per-record number. That distinction is the reason this file is shaped the way
/// it is. An earlier version constructed the state machine inside the benchmark method and
/// reported 65 KB per operation — the 64 KiB read buffer, allocated once and charged to every
/// invocation. Over 10,000 records that reads as 6.7 bytes per record, which is a plausible,
/// alarming and entirely wrong number.
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/28">#28</see> anticipated
/// exactly this: buffer growth, metadata decode and symbol-map population all allocate,
/// legitimately and once.
/// </para>
/// <para>
/// <b>This reports; it does not enforce.</b> A benchmark someone has to remember to run cannot
/// hold a guarantee, so <c>AllocationTests</c> and <c>LiveAllocationTests</c> assert the same
/// number on every <c>dotnet test</c>. If the figures here and there ever disagree, the tests are
/// the ones that were watching.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    /// <summary>Records per invocation.</summary>
    [Params(10_000)]
    public int Records { get; set; }

    private byte[] _fragment = [];
    private byte[] _stream = [];
    private DbnFsm _fsm = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fragment = DbnStream.MboFragment(Records);
        _stream = DbnStream.Mbo(Records);

        // Built once and reset per invocation. Reset rewinds the buffer indices and keeps the
        // capacity, so the 64 KiB allocation happens here, in setup, where it belongs — a session
        // pays for its buffer once, and so should the measurement.
        _fsm = new DbnFsm(skipMetadata: true, inputDbnVersion: DbnConstants.Version);

        // A warm-up pass, so the JIT has compiled everything the measured loop touches before the
        // first measured invocation.
        _ = RecordsZeroCopy();
    }

    /// <summary>
    /// <b>The per-record measurement.</b> The state machine over a fragment — records and nothing
    /// else — which is the loop the live client runs with the socket in place of the array.
    /// </summary>
    /// <returns>How many bytes decoded, returned so nothing is optimised away.</returns>
    [Benchmark(Baseline = true, Description = "Records only, zero-copy (expect 0 B)")]
    public int RecordsZeroCopy()
    {
        var fsm = _fsm;
        fsm.Reset();

        var offset = 0;
        var decoded = 0;

        while (true)
        {
            while (fsm.TryNextRecord(out var record))
            {
                decoded += record.SizeInBytes;
            }

            if (offset >= _fragment.Length)
            {
                return decoded;
            }

            var space = fsm.Space();
            var take = Math.Min(space.Length, _fragment.Length - offset);
            _fragment.AsSpan(offset, take).CopyTo(space);
            fsm.Fill(take);
            offset += take;
        }
    }

    /// <summary>
    /// A whole session: the surface a caller reading a <c>.dbn</c> file actually holds, metadata
    /// block and buffer allocation included.
    /// </summary>
    /// <returns>How many bytes decoded, returned so nothing is optimised away.</returns>
    /// <remarks>
    /// Its <c>Allocated</c> figure is the one-time cost of opening a stream — the read buffer, the
    /// metadata object and its strings. It is a per-session number and does not divide by
    /// <see cref="Records"/>.
    /// </remarks>
    [Benchmark(Description = "Whole session, zero-copy (one-time cost)")]
    public int SessionZeroCopy()
    {
        using var source = new MemoryStream(_stream);
        using var decoder = new DbnDecoder(source);

        var decoded = 0;
        while (decoder.TryNextRecord(out var record))
        {
            decoded += record.SizeInBytes;
        }

        return decoded;
    }

    /// <summary>
    /// The same session with every record copied out, so the price of the convenient surface is a
    /// number beside the zero-copy one rather than a claim about it.
    /// </summary>
    /// <returns>How many bytes decoded, returned so nothing is optimised away.</returns>
    /// <remarks>
    /// <see cref="OwnedRecord"/> is what the live client's <c>RecordsAsync</c> yields, because a
    /// <c>ref struct</c> cannot cross a <c>yield return</c>. The gap between this row and the one
    /// above it, divided by <see cref="Records"/>, <em>is</em> a per-record figure: it is what the
    /// copy costs.
    /// </remarks>
    [Benchmark(Description = "Whole session, copying each record out")]
    public int SessionCopyingOut()
    {
        using var source = new MemoryStream(_stream);
        using var decoder = new DbnDecoder(source);

        var decoded = 0;
        while (decoder.TryNextRecord(out var record))
        {
            decoded += OwnedRecord.CopyOf(record).SizeInBytes;
        }

        return decoded;
    }
}
