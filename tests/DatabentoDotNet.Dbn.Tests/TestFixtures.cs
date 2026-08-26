using System.Text.RegularExpressions;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// One vendored DBN fixture file: its name, DBN wire version, and compression.
/// </summary>
/// <param name="Name">File name within <see cref="TestFixtures.Directory"/>.</param>
/// <param name="Version">
/// The DBN wire version (1, 2, or 3) encoded in the file's magic prelude, or
/// <see langword="null"/> when <paramref name="IsFragment"/> is <see langword="true"/>. See
/// <see cref="TestFixtures"/> remarks for why fragments carry no version.
/// </param>
/// <param name="IsCompressed">Whether the file is a zstd-compressed stream (<c>.zst</c>).</param>
/// <param name="IsFragment">
/// Whether the file is a metadata-less fragment (<c>.frag</c>): a bare run of records with no
/// <c>DBN</c> magic, no version byte, and no metadata block — exercised by the decoder's
/// fragment path rather than its normal stream path.
/// </param>
public sealed record DbnFixture(string Name, byte? Version, bool IsCompressed, bool IsFragment);

/// <summary>
/// Loader over the vendored DBN fixture corpus in <c>Data/</c> (see <c>Data/README.md</c> for
/// provenance: the <c>databento/dbn</c> crate, v0.68.0, Apache-2.0, verbatim, <c>.dbz</c>
/// excluded).
/// </summary>
/// <remarks>
/// <para>
/// <b>Directory resolution.</b> <see cref="Directory"/> is rooted at
/// <see cref="AppContext.BaseDirectory"/> — the running test assembly's own output folder —
/// never at a path relative to the source tree. A source-relative path (e.g. walking up from
/// <c>Directory.GetCurrentDirectory()</c>) happens to work when the working directory is the
/// project folder, which is true on a dev machine and false in CI, where the working directory
/// depends on the runner. <c>AppContext.BaseDirectory</c> is correct on all three CI platforms
/// because <c>Data/*.dbn*</c> is copied beside the test assembly by the csproj's
/// <c>CopyToOutputDirectory="PreserveNewest"</c> items, for every target framework the project
/// builds — so "beside the assembly" is always where the fixtures actually are.
/// </para>
/// <para>
/// <b>Fragment version.</b> A <c>.frag</c> file is a bare run of records with no prelude: no
/// <c>DBN</c> magic and no version byte precedes it, unlike a normal stream. There is
/// therefore nothing to decode a version out of, and <see cref="DbnFixture.Version"/> is
/// <see langword="null"/> for every fragment — including the ones whose file name embeds a
/// <c>vN</c> tag (that tag records which record layout the fragment's bytes conform to, for a
/// human's benefit; it is not recoverable from the bytes themselves, so it is not exposed as
/// <see cref="DbnFixture.Version"/>).
/// </para>
/// <para>
/// <b>Non-fragment version.</b> For every other file, <see cref="DbnFixture.Version"/> is
/// derived from the file name's <c>vN</c> tag, or, when untagged (e.g.
/// <c>test_data.mbo.dbn</c>), defaulted to 2 — upstream's convention for its untagged
/// fixtures. Classification here is by file name, not by decoding the file, because the
/// library's own zstd seam (<c>Internal/ZstdDecompressor.cs</c>) is <see langword="internal"/>
/// with no <c>InternalsVisibleTo</c> declared anywhere in the repo, and adding one would mean
/// editing <c>src/DatabentoDotNet.Dbn.csproj</c> — a file this task does not touch. It would
/// <em>not</em> require a second conditional-compilation seam: that wrapper already resolves
/// both target frameworks behind its own single sanctioned <c>#if NET11_0_OR_GREATER</c>
/// branch, so calling it needs no new one.
/// </para>
/// <para>
/// <b>This classification is a standing, tested invariant, not a one-time census.</b> Every
/// non-fragment fixture's actual on-wire magic and version byte (decompressing first when the
/// fixture is <c>.zst</c>) is asserted against what <see cref="DbnFixture.Version"/> reports,
/// and every fragment is asserted to have no <c>DBN</c> prelude at all — see
/// <c>FixtureContentTests</c>. Decompression there goes through <c>ZstdSharp.Port</c> as a
/// plain, unconditional test-project package reference rather than through this library's
/// internal wrapper: it is pure managed code, so it needs no <c>#if</c> at all and behaves
/// identically on both target frameworks the test project builds.
/// </para>
/// </remarks>
public static class TestFixtures
{
    // Matches: <stem>[.v<1|2|3>].dbn[.frag][.zst] — the four extension shapes the corpus uses.
    // Anything that doesn't end this way (Data/README.md, a stray .DS_Store, ...) is not a
    // fixture and is excluded rather than mis-parsed.
    private static readonly Regex NamePattern = new(
        @"^.+?(?:\.v(?<version>[123]))?\.dbn(?<frag>\.frag)?(?<zst>\.zst)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Upstream's convention for an untagged, non-fragment file's DBN version.</summary>
    private const byte DefaultVersion = 2;

    /// <summary>The directory the vendored fixtures live in, resolved beside the test assembly.</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");

    /// <summary>Every vendored fixture.</summary>
    public static IReadOnlyList<DbnFixture> All { get; } = Load(Directory);

    /// <summary>Fixtures whose magic prelude (or, for a fragment, file name) marks them as the given DBN version.</summary>
    /// <param name="version">The DBN wire version: 1, 2, or 3.</param>
    public static IEnumerable<DbnFixture> ByVersion(byte version) =>
        All.Where(fixture => fixture.Version == version);

    /// <summary>Fixtures stored as a zstd-compressed stream (<c>.zst</c>).</summary>
    public static IEnumerable<DbnFixture> Compressed => All.Where(fixture => fixture.IsCompressed);

    /// <summary>Fixtures stored uncompressed.</summary>
    public static IEnumerable<DbnFixture> Uncompressed => All.Where(fixture => !fixture.IsCompressed);

    /// <summary>Metadata-less fragments (<c>.frag</c>): no prelude, no version, no metadata block.</summary>
    public static IEnumerable<DbnFixture> Fragments => All.Where(fixture => fixture.IsFragment);

    /// <summary>Ordinary DBN streams: a magic prelude and a metadata block precede the records.</summary>
    public static IEnumerable<DbnFixture> NonFragments => All.Where(fixture => !fixture.IsFragment);

    /// <summary>
    /// Reads a fixture's raw bytes exactly as vendored — still zstd-compressed for a
    /// <c>.zst</c> fixture, since decompression is the decoder's job, not the fixture loader's.
    /// </summary>
    /// <param name="name">A <see cref="DbnFixture.Name"/> from <see cref="All"/>.</param>
    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Directory, name));

    private static List<DbnFixture> Load(string directory)
    {
        var fixtures = new List<DbnFixture>();

        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var match = NamePattern.Match(name);
            if (!match.Success)
            {
                // Provenance documentation (README.md) and platform cruft (.DS_Store) live
                // alongside the fixtures but are not fixtures themselves.
                continue;
            }

            var isFragment = match.Groups["frag"].Success;
            var isCompressed = match.Groups["zst"].Success;
            byte? version = isFragment
                ? null
                : match.Groups["version"].Success
                    ? byte.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : DefaultVersion;

            fixtures.Add(new DbnFixture(name, version, isCompressed, isFragment));
        }

        return fixtures
            .OrderBy(fixture => fixture.Name, StringComparer.Ordinal)
            .ToList();
    }
}
