namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Records per vendored fixture, as counted by the upstream <c>dbn</c> CLI v0.68.0 — the decoder's
/// independent oracle.
/// </summary>
/// <remarks>
/// <para>
/// <b>These numbers were not produced by this library.</b> Each was obtained by running upstream's
/// <c>dbn</c> CLI (v0.68.0, built from <c>rust/dbn-cli</c>) over the same vendored file and counting
/// its JSON lines — <c>dbn -J FILE | wc -l</c> for ordinary streams, and
/// <c>dbn --input-fragment</c> / <c>--input-zstd-fragment</c> for the metadata-less ones. A count
/// that agrees is therefore evidence rather than a tautology.
/// </para>
/// <para>
/// The four <c>ohlcv-1d</c> fixtures really do hold zero records — they are metadata and nothing
/// else, which makes them the corpus's empty-stream case rather than a mistake.
/// </para>
/// <para>
/// <b>Its own file because two programs read it</b>
/// (<see href="https://github.com/jerbersoft/databentodotnet/issues/64">#64</see>).
/// <c>DbnDecoderTests</c> asserts against it under the managed runtime, and
/// <c>DatabentoDotNet.AotProbe</c> asserts against it inside a published Native AOT binary — the
/// second of which exists precisely to check that the native build reaches the same answers as the
/// managed one. Two copies of the table would make that comparison vacuous the first time they
/// drifted, so the probe compiles this very file by <c>&lt;Compile Include=… Link=…&gt;</c>, the
/// same one-file-two-projects arrangement CLAUDE.md already prescribes for
/// <c>Internal/ZstdDecompressor.cs</c>.
/// </para>
/// <para>
/// <b>Completeness is a test, not a convention.</b> <c>DbnDecoderTests.ExpectedRecordCounts_CoverEveryFixture</c>
/// asserts that this table has exactly one entry per file <c>TestFixtures</c> finds on disk, so a
/// re-vendor that adds a fixture must add its upstream count rather than silently going untested.
/// That is also what lets the probe iterate <see cref="ByFixture"/> directly instead of walking the
/// directory: the keys are the corpus.
/// </para>
/// </remarks>
public static class ExpectedRecordCounts
{
    /// <summary>Fixture file name to the number of records upstream's CLI reports for it.</summary>
    public static IReadOnlyDictionary<string, int> ByFixture { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
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
}
