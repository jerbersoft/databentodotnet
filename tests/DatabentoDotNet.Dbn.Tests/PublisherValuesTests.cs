using DatabentoDotNet.Dbn.Publishers;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Verifies <see cref="PublisherValues"/> — the numeric-decode half of <see cref="Publisher"/>,
/// <see cref="Dataset"/> and <see cref="Venue"/>, and the checked conversion for
/// <see cref="RecordHeader.PublisherId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The acceptance tests sweep the <em>entire</em> <see langword="ushort"/> range rather than
/// spot-checking, for the same reason <see cref="PublisherTableTests"/> enumerates instead of
/// transcribing: with 145 + 52 + 71 discriminants, a hand-written table of expected accepts and
/// rejects would be the very transcription error the generator exists to prevent. A sweep states
/// the property directly — <em>exactly</em> the declared discriminants are accepted, and every
/// one of the other 65,000-odd words is rejected — and 65,536 iterations of a jump table costs
/// nothing.
/// </para>
/// <para>
/// The sweep alone would still pass if the validator and the enum were generated from the same
/// wrong table, so it is paired with a cross-check against
/// <see cref="PublisherWireStrings"/> and <see cref="PublisherMappings"/>: anything
/// <see cref="PublisherValues.TryFromPublisher(ushort, out Publisher)"/> accepts must also
/// survive the conversions that throw on an undefined value. A validator that accepted a word
/// those tables do not know about would be worse than no validator, because a caller would have
/// been told it was safe.
/// </para>
/// </remarks>
public class PublisherValuesTests
{
    // ------------------------------------------------------------- Accepts exactly the table

    [Fact]
    public void TryFromPublisher_AcceptsEveryDeclaredPublisherAndNothingElse()
    {
        AssertAcceptsExactly<Publisher>(PublisherValues.TryFromPublisher);
    }

    [Fact]
    public void TryFromDataset_AcceptsEveryDeclaredDatasetAndNothingElse()
    {
        AssertAcceptsExactly<Dataset>(PublisherValues.TryFromDataset);
    }

    [Fact]
    public void TryFromVenue_AcceptsEveryDeclaredVenueAndNothingElse()
    {
        AssertAcceptsExactly<Venue>(PublisherValues.TryFromVenue);
    }

    // ------------------------------------------------------- Rejects, without ever throwing

    /// <summary>
    /// Rejection values chosen to be undefined in <c>publishers.rs</c> for all three enums at
    /// once: zero (upstream declares no zero variant — there is deliberately no "unset"
    /// publisher), one past the largest table, a round number well beyond every table, and the
    /// widest word the field can carry.
    /// </summary>
    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)146)]
    [InlineData((ushort)1000)]
    [InlineData(ushort.MaxValue)]
    public void TryFrom_RejectsWordsUpstreamDoesNotDefine(ushort raw)
    {
        Assert.False(PublisherValues.TryFromPublisher(raw, out var publisher));
        Assert.Equal(default, publisher);

        Assert.False(PublisherValues.TryFromDataset(raw, out var dataset));
        Assert.Equal(default, dataset);

        Assert.False(PublisherValues.TryFromVenue(raw, out var venue));
        Assert.Equal(default, venue);
    }

    /// <summary>
    /// The three tables are different lengths — 145, 52 and 71 — so a word can be a defined
    /// <see cref="Publisher"/> and not a defined <see cref="Dataset"/>. Each validator must
    /// answer for its own table, and 100 is the cheapest word that proves all three disagree.
    /// </summary>
    [Fact]
    public void TryFrom_AnswersForItsOwnTable_NotTheLargestOne()
    {
        Assert.True(PublisherValues.TryFromPublisher(100, out _));
        Assert.False(PublisherValues.TryFromDataset(100, out _));
        Assert.False(PublisherValues.TryFromVenue(100, out _));

        Assert.True(PublisherValues.TryFromVenue(71, out _));
        Assert.False(PublisherValues.TryFromDataset(71, out _));
    }

    // ------------------------------------------ Agreement with the tables it makes safe to use

    [Fact]
    public void EveryAcceptedPublisher_SurvivesTheConversionsThatThrowOnUndefinedValues()
    {
        for (var raw = 0; raw <= ushort.MaxValue; raw++)
        {
            if (!PublisherValues.TryFromPublisher((ushort)raw, out var publisher))
            {
                continue;
            }

            // None of these three may throw for a value the validator vouched for. That is the
            // whole promise: RecordHeader.PublisherId goes through TryFromPublisher precisely so
            // that ToVenue and ToDataset cannot raise from inside a lookup afterwards.
            Assert.False(string.IsNullOrEmpty(publisher.ToWireString()));
            _ = publisher.ToVenue();
            _ = publisher.ToDataset();
        }
    }

    [Fact]
    public void EveryAcceptedDatasetAndVenue_HasAWireString()
    {
        for (var raw = 0; raw <= ushort.MaxValue; raw++)
        {
            if (PublisherValues.TryFromDataset((ushort)raw, out var dataset))
            {
                Assert.False(string.IsNullOrEmpty(dataset.ToWireString()));
            }

            if (PublisherValues.TryFromVenue((ushort)raw, out var venue))
            {
                Assert.False(string.IsNullOrEmpty(venue.ToWireString()));
            }
        }
    }

    /// <summary>
    /// The numeric and the string validators must agree about which values exist. They are
    /// generated from the same parse of <c>publishers.rs</c>, so this is a check that the
    /// generator emits one table twice rather than two tables that drifted.
    /// </summary>
    [Fact]
    public void TheNumericAndWireStringValidators_AdmitTheSameSetOfValues()
    {
        foreach (var publisher in Enum.GetValues<Publisher>())
        {
            Assert.True(PublisherValues.TryFromPublisher((ushort)publisher, out _));
            Assert.True(PublisherWireStrings.TryParsePublisher(publisher.ToWireString(), out _));
        }

        foreach (var dataset in Enum.GetValues<Dataset>())
        {
            Assert.True(PublisherValues.TryFromDataset((ushort)dataset, out _));
            Assert.True(PublisherWireStrings.TryParseDataset(dataset.ToWireString(), out _));
        }

        foreach (var venue in Enum.GetValues<Venue>())
        {
            Assert.True(PublisherValues.TryFromVenue((ushort)venue, out _));
            Assert.True(PublisherWireStrings.TryParseVenue(venue.ToWireString(), out _));
        }
    }

    // ---------------------------------------------------------------- The documented call site

    /// <summary>
    /// <see cref="RecordHeader.PublisherId"/>'s documentation points here for a checked
    /// conversion. This is that call site, run over the whole vendored corpus: every record
    /// Databento ships as a fixture must carry a publisher id this build can name, and must
    /// reach its venue and dataset without an exception escaping a lookup.
    /// </summary>
    /// <remarks>
    /// A sweep rather than one hand-picked record, because the interesting failure is a publisher
    /// id that exists on the wire and not in the table — which no single fixture would surface.
    /// If a future <c>dbn</c> release vendors a fixture from a publisher this generation does not
    /// know about, this test is the thing that says so.
    /// </remarks>
    [Fact]
    public void EveryPublisherIdInTheVendoredCorpus_ResolvesThroughTheValidator()
    {
        var seen = new HashSet<Publisher>();

        foreach (var fixture in TestFixtures.All)
        {
            using var source = new MemoryStream(TestFixtures.Read(fixture.Name));
            using var decoder = new DbnDecoder(source, skipMetadata: fixture.IsFragment);

            while (decoder.TryNextRecord(out var record))
            {
                var raw = record.Header.PublisherId;
                Assert.True(
                    PublisherValues.TryFromPublisher(raw, out var publisher),
                    $"{fixture.Name}: publisher id {raw} is not a Publisher this build declares.");

                seen.Add(publisher);
                _ = publisher.ToVenue();
                _ = publisher.ToDataset();
            }
        }

        // Guards the sweep itself: a corpus that decoded to nothing, or to records that all
        // carried a zero publisher id, would otherwise pass the loop above without checking
        // anything.
        Assert.NotEmpty(seen);
    }

    private delegate bool TryFrom<T>(ushort raw, out T value)
        where T : struct, Enum;

    private static void AssertAcceptsExactly<T>(TryFrom<T> tryFrom)
        where T : struct, Enum
    {
        var declared = Enum.GetValues<T>().ToHashSet();
        var accepted = new HashSet<T>();

        for (var raw = 0; raw <= ushort.MaxValue; raw++)
        {
            if (tryFrom((ushort)raw, out var value))
            {
                accepted.Add(value);
                Assert.Equal(raw, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                Assert.Equal(default, value);
            }
        }

        Assert.Equal(declared.Count, accepted.Count);
        Assert.Empty(declared.Except(accepted));
        Assert.Empty(accepted.Except(declared));
    }
}
