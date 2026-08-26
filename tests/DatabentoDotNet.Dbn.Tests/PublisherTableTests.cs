using DatabentoDotNet.Dbn.Publishers;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Verifies the mechanically-generated <see cref="Venue"/>, <see cref="Dataset"/>, and
/// <see cref="Publisher"/> tables (<c>src/DatabentoDotNet.Dbn/Publishers/</c>, produced by
/// <c>tools/generate-publishers.py</c> from the <c>dbn</c> crate's <c>publishers.rs</c>
/// v0.68.0) against the counts and cross-references the task brief records from upstream.
/// </summary>
/// <remarks>
/// The round-trip and cross-mapping assertions below loop over <em>every</em> declared
/// <see cref="Venue"/>/<see cref="Dataset"/>/<see cref="Publisher"/> member via
/// <see cref="Enum.GetValues{TEnum}()"/> rather than a hand-typed table of 71 + 52 + 145 = 268
/// expected values — that is deliberate: transcribing that many rows by hand is exactly the
/// failure mode the generator exists to avoid, and it would reintroduce the same risk inside
/// the test that is supposed to catch it. What full enumeration cannot check (whether the
/// wire strings and Venue/Dataset the generator emitted are the values <c>publishers.rs</c>
/// actually declares, as opposed to merely self-consistent) is instead covered by spot checks
/// taken verbatim from <c>.superpowers/sdd/m1-dbn-codec/reference/publishers.md</c>, which was
/// extracted read-only from the Rust source.
/// </remarks>
public class PublisherTableTests
{
    // ---------------------------------------------------------------- Variant counts (DoD #1)

    [Fact]
    public void Venue_VariantCount_Is71()
    {
        Assert.Equal(71, Enum.GetValues<Venue>().Length);
    }

    [Fact]
    public void Dataset_VariantCount_Is52()
    {
        Assert.Equal(52, Enum.GetValues<Dataset>().Length);
    }

    [Fact]
    public void Publisher_VariantCount_Is145()
    {
        Assert.Equal(145, Enum.GetValues<Publisher>().Length);
    }

    [Fact]
    public void Venue_DeclaredValues_AreContiguousFrom1WithNoAliasing()
    {
        var values = Enum.GetValues<Venue>().Select(v => (int)v).ToList();
        Assert.Equal(71, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 71), values.OrderBy(v => v));
    }

    [Fact]
    public void Dataset_DeclaredValues_AreContiguousFrom1WithNoAliasing()
    {
        var values = Enum.GetValues<Dataset>().Select(v => (int)v).ToList();
        Assert.Equal(52, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 52), values.OrderBy(v => v));
    }

    [Fact]
    public void Publisher_DeclaredValues_AreContiguousFrom1WithNoAliasing()
    {
        var values = Enum.GetValues<Publisher>().Select(v => (int)v).ToList();
        Assert.Equal(145, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 145), values.OrderBy(v => v));
    }

    // ------------------------------------------------- Round-trip over every variant (DoD #2)

    [Fact]
    public void Venue_EveryVariant_RoundTripsThroughWireString()
    {
        foreach (var venue in Enum.GetValues<Venue>())
        {
            var wire = venue.ToWireString();
            Assert.False(string.IsNullOrEmpty(wire));
            Assert.True(PublisherWireStrings.TryParseVenue(wire, out var parsed));
            Assert.Equal(venue, parsed);
        }
    }

    [Fact]
    public void Dataset_EveryVariant_RoundTripsThroughWireString()
    {
        foreach (var dataset in Enum.GetValues<Dataset>())
        {
            var wire = dataset.ToWireString();
            Assert.False(string.IsNullOrEmpty(wire));
            Assert.True(PublisherWireStrings.TryParseDataset(wire, out var parsed));
            Assert.Equal(dataset, parsed);
        }
    }

    [Fact]
    public void Publisher_EveryVariant_RoundTripsThroughWireString()
    {
        foreach (var publisher in Enum.GetValues<Publisher>())
        {
            var wire = publisher.ToWireString();
            Assert.False(string.IsNullOrEmpty(wire));
            Assert.True(PublisherWireStrings.TryParsePublisher(wire, out var parsed));
            Assert.Equal(publisher, parsed);
        }
    }

    [Fact]
    public void Venue_WireStrings_AreAllDistinct()
    {
        var wires = Enum.GetValues<Venue>().Select(v => v.ToWireString()).ToList();
        Assert.Equal(71, wires.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Dataset_WireStrings_AreAllDistinct()
    {
        var wires = Enum.GetValues<Dataset>().Select(d => d.ToWireString()).ToList();
        Assert.Equal(52, wires.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Publisher_WireStrings_AreAllDistinct()
    {
        var wires = Enum.GetValues<Publisher>().Select(p => p.ToWireString()).ToList();
        Assert.Equal(145, wires.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Publisher_WireString_IsThreeDotSeparatedSegments()
    {
        // "{DATASET}.{VENUE}.{VENUE}" per publishers.md #3 -- e.g. "GLBX.MDP3.GLBX".
        foreach (var publisher in Enum.GetValues<Publisher>())
        {
            var segments = publisher.ToWireString().Split('.');
            Assert.Equal(3, segments.Length);
        }
    }

    [Fact]
    public void Venue_24EqIrregularIdentifier_WireStringIs24Eq()
    {
        // publishers.md's one documented irregular case: the Rust identifier `_24Eq` (Rust
        // identifiers can't start with a digit) maps to wire string "24EQ", not "_24EQ".
        Assert.Equal("24EQ", Venue._24Eq.ToWireString());
        Assert.True(PublisherWireStrings.TryParseVenue("24EQ", out var parsed));
        Assert.Equal(Venue._24Eq, parsed);
    }

    [Theory]
    [InlineData("not-a-real-venue")]
    [InlineData("")]
    [InlineData(null)]
    public void Venue_TryParse_UnknownString_ReturnsFalseWithoutThrowing(string? value)
    {
        Assert.False(PublisherWireStrings.TryParseVenue(value, out var result));
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData("XNAS")] // A Venue wire string, not a Dataset one.
    [InlineData("")]
    [InlineData(null)]
    public void Dataset_TryParse_UnknownString_ReturnsFalseWithoutThrowing(string? value)
    {
        Assert.False(PublisherWireStrings.TryParseDataset(value, out var result));
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData("GLBX.MDP3")] // A Dataset wire string, not a Publisher one.
    [InlineData("")]
    [InlineData(null)]
    public void Publisher_TryParse_UnknownString_ReturnsFalseWithoutThrowing(string? value)
    {
        Assert.False(PublisherWireStrings.TryParsePublisher(value, out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void Venue_ToWireString_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Venue)0).ToWireString());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Venue)9999).ToWireString());
    }

    [Fact]
    public void Dataset_ToWireString_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Dataset)0).ToWireString());
    }

    [Fact]
    public void Publisher_ToWireString_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Publisher)0).ToWireString());
    }

    // ---------------------------------- Publisher -> Venue / Dataset agreement (DoD #3), full

    [Fact]
    public void Publisher_ToVenue_NeverThrowsForADefinedPublisher()
    {
        foreach (var publisher in Enum.GetValues<Publisher>())
        {
            var venue = publisher.ToVenue();
            Assert.Contains(venue, Enum.GetValues<Venue>());
        }
    }

    [Fact]
    public void Publisher_ToDataset_NeverThrowsForADefinedPublisher()
    {
        foreach (var publisher in Enum.GetValues<Publisher>())
        {
            var dataset = publisher.ToDataset();
            Assert.Contains(dataset, Enum.GetValues<Dataset>());
        }
    }

    [Fact]
    public void Publisher_ToVenue_ToDataset_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Publisher)0).ToVenue());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Publisher)0).ToDataset());
    }

    [Fact]
    public void Publisher_EveryVariant_RoundTripsThroughFromDatasetVenue()
    {
        // The strongest possible in-test check that venue()/dataset()/from_dataset_venue()
        // agree with each other for all 145 publishers, mirroring upstream's own guarantee
        // that from_dataset_venue is the exact inverse of venue()+dataset() (verified against
        // the Rust source itself at generation time in tools/generate-publishers.py).
        foreach (var publisher in Enum.GetValues<Publisher>())
        {
            var dataset = publisher.ToDataset();
            var venue = publisher.ToVenue();
            Assert.True(PublisherMappings.TryFromDatasetVenue(dataset, venue, out var roundTripped));
            Assert.Equal(publisher, roundTripped);
        }
    }

    [Fact]
    public void Publisher_FromDatasetVenue_SucceedsForExactlyThe145ValidPairs()
    {
        var validPairs = Enum.GetValues<Publisher>()
            .Select(p => (Dataset: p.ToDataset(), Venue: p.ToVenue()))
            .ToHashSet();
        Assert.Equal(145, validPairs.Count); // No two publishers share a (Dataset, Venue) pair.

        var successCount = 0;
        foreach (var dataset in Enum.GetValues<Dataset>())
        {
            foreach (var venue in Enum.GetValues<Venue>())
            {
                var succeeded = PublisherMappings.TryFromDatasetVenue(dataset, venue, out _);
                Assert.Equal(validPairs.Contains((dataset, venue)), succeeded);
                if (succeeded)
                {
                    successCount++;
                }
            }
        }

        Assert.Equal(145, successCount);
    }

    [Theory]
    [InlineData(Dataset.XnasItch, Venue.Glbx)] // Mismatched dataset/venue: not a real publisher.
    [InlineData(Dataset.GlbxMdp3, Venue.Xnas)]
    public void Publisher_FromDatasetVenue_InvalidCombination_ReturnsFalseWithoutThrowing(Dataset dataset, Venue venue)
    {
        Assert.False(PublisherMappings.TryFromDatasetVenue(dataset, venue, out var result));
        Assert.Equal(default, result);
    }

    // --------------------------- Spot checks copied verbatim from publishers.md (DoD #3, cont.)

    [Theory]
    // First 5 Publisher variants (publishers.md #3, publishers.rs:761-771).
    [InlineData(Publisher.GlbxMdp3Glbx, Dataset.GlbxMdp3, Venue.Glbx, "GLBX.MDP3.GLBX")]
    [InlineData(Publisher.XnasItchXnas, Dataset.XnasItch, Venue.Xnas, "XNAS.ITCH.XNAS")]
    [InlineData(Publisher.XbosItchXbos, Dataset.XbosItch, Venue.Xbos, "XBOS.ITCH.XBOS")]
    [InlineData(Publisher.XpsxItchXpsx, Dataset.XpsxItch, Venue.Xpsx, "XPSX.ITCH.XPSX")]
    [InlineData(Publisher.BatsPitchBats, Dataset.BatsPitch, Venue.Bats, "BATS.PITCH.BATS")]
    // Last 5 Publisher variants, values 141-145 (publishers.md #3, publishers.rs:1041-1050).
    [InlineData(Publisher.CgiCgifCgi, Dataset.CgiCgif, Venue.Cgi, "CGI.CGIF.CGI")]
    [InlineData(Publisher.MainCgifDef, Dataset.MainCgif, Venue.Def, "MAIN.CGIF.DEF")]
    [InlineData(Publisher.XtksFlexXtks, Dataset.XtksFlex, Venue.Xtks, "XTKS.FLEX.XTKS")]
    [InlineData(Publisher.XtktItchXtkt, Dataset.XtktItch, Venue.Xtkt, "XTKT.ITCH.XTKT")]
    [InlineData(Publisher.XoseItchXose, Dataset.XoseItch, Venue.Xose, "XOSE.ITCH.XOSE")]
    // Publishers appended to the end of the enum well outside their dataset's primary block
    // (publishers.md #3, confirmed example at publishers.rs:800-882) -- the exact case
    // "never derive Publisher -> (Dataset, Venue) by arithmetic" warns about.
    [InlineData(Publisher.IfeuImpactIfeu, Dataset.IfeuImpact, Venue.Ifeu, "IFEU.IMPACT.IFEU")]
    [InlineData(Publisher.NdexImpactNdex, Dataset.NdexImpact, Venue.Ndex, "NDEX.IMPACT.NDEX")]
    [InlineData(Publisher.DbeqBasicDbeq, Dataset.DbeqBasic, Venue.Dbeq, "DBEQ.BASIC.DBEQ")]
    [InlineData(Publisher.EqusPlusEqus, Dataset.EqusPlus, Venue.Equs, "EQUS.PLUS.EQUS")]
    [InlineData(Publisher.OpraPillarSphr, Dataset.OpraPillar, Venue.Sphr, "OPRA.PILLAR.SPHR")]
    public void Publisher_SpotCheck_MatchesUpstreamReference(Publisher publisher, Dataset dataset, Venue venue, string wire)
    {
        Assert.Equal(dataset, publisher.ToDataset());
        Assert.Equal(venue, publisher.ToVenue());
        Assert.Equal(wire, publisher.ToWireString());
        Assert.True(PublisherMappings.TryFromDatasetVenue(dataset, venue, out var fromDv));
        Assert.Equal(publisher, fromDv);
    }

    [Theory]
    // publishers.md #1: first and last 5 Venue variants (publishers.rs:16-21, 147-157).
    [InlineData(Venue.Glbx, (ushort)1, "GLBX")]
    [InlineData(Venue.Xnas, (ushort)2, "XNAS")]
    [InlineData(Venue.Xbos, (ushort)3, "XBOS")]
    [InlineData(Venue.Xpsx, (ushort)4, "XPSX")]
    [InlineData(Venue.Bats, (ushort)5, "BATS")]
    [InlineData(Venue.Cgi, (ushort)67, "CGI")]
    [InlineData(Venue.Def, (ushort)68, "DEF")]
    [InlineData(Venue.Xtks, (ushort)69, "XTKS")]
    [InlineData(Venue.Xtkt, (ushort)70, "XTKT")]
    [InlineData(Venue.Xose, (ushort)71, "XOSE")]
    public void Venue_SpotCheck_MatchesUpstreamReference(Venue value, ushort expectedValue, string expectedWire)
    {
        Assert.Equal(expectedValue, (ushort)value);
        Assert.Equal(expectedWire, value.ToWireString());
    }

    [Theory]
    // publishers.md #2: first and last 5 Dataset variants (publishers.rs:342-347, 436-447),
    // plus both deprecated variants (publishers.rs:374-375, 377-378).
    [InlineData(Dataset.GlbxMdp3, (ushort)1, "GLBX.MDP3")]
    [InlineData(Dataset.XnasItch, (ushort)2, "XNAS.ITCH")]
    [InlineData(Dataset.XbosItch, (ushort)3, "XBOS.ITCH")]
    [InlineData(Dataset.XpsxItch, (ushort)4, "XPSX.ITCH")]
    [InlineData(Dataset.BatsPitch, (ushort)5, "BATS.PITCH")]
    [InlineData(Dataset.FinnNls, (ushort)17, "FINN.NLS")]
    [InlineData(Dataset.FinyTrades, (ushort)18, "FINY.TRADES")]
    [InlineData(Dataset.CccyCgif, (ushort)48, "CCCY.CGIF")]
    [InlineData(Dataset.CgiCgif, (ushort)49, "CGI.CGIF")]
    [InlineData(Dataset.XtksFlex, (ushort)50, "XTKS.FLEX")]
    [InlineData(Dataset.XtktItch, (ushort)51, "XTKT.ITCH")]
    [InlineData(Dataset.XoseItch, (ushort)52, "XOSE.ITCH")]
    public void Dataset_SpotCheck_MatchesUpstreamReference(Dataset value, ushort expectedValue, string expectedWire)
    {
        Assert.Equal(expectedValue, (ushort)value);
        Assert.Equal(expectedWire, value.ToWireString());
    }

    // ------------------------------------------------------ Deprecated Dataset variants (§2)

    [Fact]
    public void Dataset_FinnNlsAndFinyTrades_AreDeprecatedButStillRoundTrip()
    {
        Assert.Equal("FINN.NLS", Dataset.FinnNls.ToWireString());
        Assert.Equal("FINY.TRADES", Dataset.FinyTrades.ToWireString());
        Assert.True(PublisherWireStrings.TryParseDataset("FINN.NLS", out var finnNls));
        Assert.Equal(Dataset.FinnNls, finnNls);
        Assert.True(PublisherWireStrings.TryParseDataset("FINY.TRADES", out var finyTrades));
        Assert.Equal(Dataset.FinyTrades, finyTrades);
    }

    [Fact]
    public void Dataset_FinnNlsAndFinyTrades_HaveNoAssociatedPublisher()
    {
        // publishers.md #2: Dataset::publishers() returns an empty slice for both deprecated
        // datasets -- no Publisher variant is tied to either any more.
        var datasetsInUse = Enum.GetValues<Publisher>().Select(p => p.ToDataset()).ToHashSet();
        Assert.DoesNotContain(Dataset.FinnNls, datasetsInUse);
        Assert.DoesNotContain(Dataset.FinyTrades, datasetsInUse);
    }
}
