namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Guards the vendored DBN fixture corpus (<c>Data/</c>, see <c>Data/README.md</c> for
/// provenance) and the <see cref="TestFixtures"/> loader over it.
/// </summary>
/// <remarks>
/// The exact-count assertions here are deliberate, not just the "at least one" the task's
/// definition of done requires: a partial re-vendor that silently drops, say, every v1 file
/// would still pass an "at least one v2 and v3" check. Pinning every category to the count
/// measured against the upstream <c>dbn</c> v0.68.0 corpus during vendoring means a future
/// re-vendor that quietly loses coverage fails a build instead of starving the metadata,
/// decoder, and symbol-map tasks of fixtures they believe they have.
/// </remarks>
public class TestFixturesTests
{
    // -------------------------------------------------------------------------------- Presence

    [Fact]
    public void All_ContainsExactly71Fixtures()
    {
        Assert.Equal(71, TestFixtures.All.Count);
    }

    [Fact]
    public void All_EveryFixtureFileIsNonEmpty()
    {
        Assert.All(TestFixtures.All, fixture =>
        {
            var info = new FileInfo(Path.Combine(TestFixtures.Directory, fixture.Name));
            Assert.True(info.Exists, $"{fixture.Name} is not present in {TestFixtures.Directory}.");
            Assert.True(info.Length > 0, $"{fixture.Name} is empty.");
        });
    }

    [Fact]
    public void All_TotalByteSizeMatchesVendoredCorpus()
    {
        // 37,320 bytes: measured with `md5`-verified byte-for-byte copies from
        // /Users/herbertsabanal/Projects/dbn/tests/data/ during vendoring. A mismatch here
        // means a file was truncated, re-encoded, or line-ending-mangled in transit.
        var totalBytes = TestFixtures.All.Sum(fixture => new FileInfo(Path.Combine(TestFixtures.Directory, fixture.Name)).Length);
        Assert.Equal(37_320, totalBytes);
    }

    // ------------------------------------------------------------------------- DoD: at least 1

    [Fact]
    public void ByVersion_ContainsAtLeastOneStreamForEachDbnVersion()
    {
        Assert.NotEmpty(TestFixtures.ByVersion(1));
        Assert.NotEmpty(TestFixtures.ByVersion(2));
        Assert.NotEmpty(TestFixtures.ByVersion(3));
    }

    [Fact]
    public void Compressed_ContainsAtLeastOneZstFixture()
    {
        Assert.NotEmpty(TestFixtures.Compressed);
    }

    // ---------------------------------------------------------- Exact breakdown (measured, §)

    [Fact]
    public void ByVersion_MatchesMeasuredCorpusBreakdown()
    {
        // v1: 15 files tagged .v1. minus 3 that are fragments (no version byte) = 12.
        Assert.Equal(12, TestFixtures.ByVersion(1).Count());
        // v2: 18 tagged .v2. minus 1 fragment, plus all 17 untagged test_data.*.dbn (natively
        // v2 — confirmed by reading the actual magic-prelude version byte during vendoring).
        Assert.Equal(34, TestFixtures.ByVersion(2).Count());
        // v3: 20 tagged .v3. minus 2 that are fragments = 18.
        Assert.Equal(18, TestFixtures.ByVersion(3).Count());
    }

    [Fact]
    public void Fragments_ContainsExactly7FixturesAllWithNullVersion()
    {
        Assert.Equal(7, TestFixtures.Fragments.Count());
        Assert.All(TestFixtures.Fragments, fixture => Assert.Null(fixture.Version));
    }

    [Fact]
    public void NonFragments_ContainsExactly64FixturesAllWithAVersion()
    {
        Assert.Equal(64, TestFixtures.NonFragments.Count());
        Assert.All(TestFixtures.NonFragments, fixture => Assert.NotNull(fixture.Version));
    }

    [Fact]
    public void Compressed_ContainsExactly50Fixtures()
    {
        Assert.Equal(50, TestFixtures.Compressed.Count());
    }

    [Fact]
    public void Uncompressed_ContainsExactly21Fixtures()
    {
        Assert.Equal(21, TestFixtures.Uncompressed.Count());
    }

    // -------------------------------------------------------------------------- Loader surface

    [Fact]
    public void Directory_ResolvesUnderAppContextBaseDirectoryAndExists()
    {
        Assert.StartsWith(AppContext.BaseDirectory, TestFixtures.Directory, StringComparison.Ordinal);
        Assert.True(Directory.Exists(TestFixtures.Directory));
    }

    [Fact]
    public void Read_ReturnsBytesMatchingFileLength()
    {
        var fixture = TestFixtures.All[0];

        var bytes = TestFixtures.Read(fixture.Name);

        var info = new FileInfo(Path.Combine(TestFixtures.Directory, fixture.Name));
        Assert.Equal(info.Length, bytes.Length);
    }

    [Fact]
    public void Read_ReturnsStillCompressedBytesForAZstFixture()
    {
        var fixture = TestFixtures.Compressed.First();

        var bytes = TestFixtures.Read(fixture.Name);

        // Zstandard frame magic number, little-endian: 0x28 0xB5 0x2F 0xFD. Read() must hand
        // back the fixture exactly as vendored — decompression is the decoder's job, not the
        // fixture loader's.
        Assert.Equal(0x28, bytes[0]);
        Assert.Equal(0xB5, bytes[1]);
        Assert.Equal(0x2F, bytes[2]);
        Assert.Equal(0xFD, bytes[3]);
    }
}
