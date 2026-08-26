namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Cross-checks every vendored fixture's actual on-wire bytes against how
/// <see cref="TestFixtures"/> classified it, decompressing <c>.zst</c> fixtures where needed.
/// </summary>
/// <remarks>
/// <see cref="TestFixtures"/> derives <see cref="DbnFixture.Version"/> from each file's name,
/// not by decoding it (see its remarks for why). That was verified to be accurate for this
/// exact corpus by a one-time, out-of-band census during vendoring — accurate today, but not
/// an enforced invariant: nothing stopped a future re-vendor from adding a file whose name
/// disagreed with its content, and only a hardcoded count assertion drifting would ever hint
/// at it. These tests decode every one of the 71 vendored fixtures — all 46 compressed and 18
/// uncompressed non-fragment streams, and all 4 compressed and 3 uncompressed fragments — and
/// assert directly against the bytes, turning that one-time census into something the suite
/// checks on every run.
/// </remarks>
public class FixtureContentTests
{
    [Fact]
    public void NonFragments_OnWireMagicAndVersionMatchClassification()
    {
        Assert.All(TestFixtures.NonFragments, fixture =>
        {
            var bytes = TestFixtures.ReadDecompressed(fixture);

            Assert.True(
                bytes.Length >= 4,
                $"{fixture.Name}: decompressed content is too short to hold a DBN prelude.");
            Assert.True(
                bytes[0] == (byte)'D' && bytes[1] == (byte)'B' && bytes[2] == (byte)'N',
                $"{fixture.Name}: expected the DBN magic prelude, got {Convert.ToHexString(bytes.AsSpan(0, 3))}.");

            Assert.NotNull(fixture.Version);
            Assert.Equal(fixture.Version!.Value, bytes[3]);
        });
    }

    [Fact]
    public void Fragments_HaveNoDbnPrelude()
    {
        Assert.All(TestFixtures.Fragments, fixture =>
        {
            var bytes = TestFixtures.ReadDecompressed(fixture);

            Assert.True(bytes.Length >= 3, $"{fixture.Name}: decompressed content is unexpectedly short.");
            var isDbnMagic = bytes[0] == (byte)'D' && bytes[1] == (byte)'B' && bytes[2] == (byte)'N';
            Assert.False(isDbnMagic, $"{fixture.Name}: a fragment unexpectedly starts with the DBN magic prelude.");
        });
    }
}
