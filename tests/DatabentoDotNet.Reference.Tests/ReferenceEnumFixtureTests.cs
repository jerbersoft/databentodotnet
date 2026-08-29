namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Tests for <see cref="ReferenceEnumFixture"/> and for the two vendored responses it reads.
/// </summary>
/// <remarks>
/// <para>
/// These assert the fixtures rather than the library, and that is the point: #50 and #51
/// transcribe their enum tables from these files, so a fixture that has quietly changed shape
/// would widen or narrow what those issues believe without anything saying so. Every count here
/// is also written down in <c>Data/README.md</c>; the two must agree, and a re-capture that
/// updates one without the other fails here.
/// </para>
/// <para>
/// <b>Nothing in this file touches this library's reference models or JSON converters.</b> They
/// are what the fixtures exist to check — see <see cref="ReferenceEnumFixture"/>'s remarks.
/// </para>
/// </remarks>
public class ReferenceEnumFixtureTests
{
    /// <summary>The four codes that make `Event` stale in both directions; see the test that uses them.</summary>
    private static readonly string[] EventDriftCodes = ["DIVEB", "LTCHG", "DIVIF", "MFCON"];

    private static ReferenceEnumFixture Fixture => ReferenceEnumFixture.Instance;

    [Fact]
    public void Fixtures_HoldTheCountsTheReadmeStates()
    {
        Assert.Equal(235, Fixture.Groups.Count);
        Assert.Equal(13_123, Fixture.Groups.Values.Sum(g => g.Count));
        Assert.Equal(60, Fixture.Events.Count);
    }

    [Fact]
    public void Fixtures_AreStillExactlyAsTheApiSentThem()
    {
        // Both responses arrived minified — one line, no indentation. Asserting that is a cheap
        // guard against the one edit most likely to happen by accident: an editor or a formatter
        // pretty-printing a JSON file on save. The README's claim is byte-for-byte; this catches
        // the realistic way that claim stops being true.
        foreach (var name in new[] { ReferenceEnumFixture.EnumsFileName, ReferenceEnumFixture.EventsFileName })
        {
            var text = File.ReadAllText(Path.Combine(ReferenceEnumFixture.Directory, name));
            Assert.DoesNotContain('\n', text);
            Assert.StartsWith("{\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CodesIn_IsDistinct_AndEventSubTypeIsWhyThatMatters()
    {
        // 80 entries, 67 distinct codes, 7 with no code at all. Six codes appear more than once
        // with a description that depends on the parent event, so a table built by counting
        // entries rather than codes would be thirteen too long.
        Assert.Equal(80, Fixture.Groups["EVENTSUBTYPE"].Count);
        Assert.Equal(67, Fixture.CodesIn("EVENTSUBTYPE").Count);
        Assert.True(Fixture.HasBlank("EVENTSUBTYPE"));
    }

    [Theory]
    [InlineData("SECTYPE", true)]
    [InlineData("FREQ", true)]
    [InlineData("FRACCD", true)]
    [InlineData("PAYTYPE", true)]
    [InlineData("CNTRY", false)]
    [InlineData("CUREN", false)]
    [InlineData("EVENT", false)]
    [InlineData("OUTTURNSTYLE", false)]
    public void HasBlank_ReportsWhereABlankIsALegalValue(string group, bool expected)
    {
        // A null code is a value, not a hole: it says a blank is legal for the field, which is why
        // the corresponding model fields are nullable. Getting this backwards would make a
        // required field out of an optional one.
        Assert.Equal(expected, Fixture.HasBlank(group));
    }

    [Fact]
    public void ListEvents_IsTheOnlyAuthorityForThreeEnums()
    {
        Assert.Equal(8, Fixture.EventCategories.Count);
        Assert.Equal(4, Fixture.EventLevels.Count);
        Assert.Equal(3, Fixture.FieldGroups.Count);

        Assert.Equal(
            ["date_info", "event_info", "rate_info"],
            Fixture.FieldGroups.Order(StringComparer.Ordinal));

        // And list_enums genuinely has nothing for them, which is why the second fixture exists at
        // all rather than being a convenience.
        foreach (var name in new[] { "EVENTCATEGORY", "EVENTLEVEL", "FIELDGROUP", "CATEGORY", "LEVEL" })
        {
            Assert.DoesNotContain(
                Fixture.EventCategories.Concat(Fixture.EventLevels).Concat(Fixture.FieldGroups),
                value => Fixture.Groups.TryGetValue(name, out var g)
                    && g.Any(v => string.Equals(v.Code, value, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void TheDictionaryIsWiderThanAnythingThisLibraryModels()
    {
        // 235 groups against ten typed enums, and one group with more entries than the other 234
        // put together minus a handful. This is why CorporateAction's date_info, rate_info and
        // event_info stay open maps: modelling this dictionary is not a goal that was missed.
        Assert.Equal(7_705, Fixture.Groups["ETFBNCH"].Count);
        Assert.Equal(7_704, Fixture.CodesIn("ETFBNCH").Count);
        Assert.True(Fixture.HasBlank("ETFBNCH"));
        Assert.True(Fixture.Groups.Count > 200);
    }

    [Fact]
    public void TheThreeEnumsUpstreamLags_AreVisibleInTheFixture()
    {
        // The finding that moved SecurityType and Frequency from #50 to #51, pinned so that a
        // re-capture which closes the gap says so instead of leaving the split unexplained.
        Assert.Equal(64, Fixture.CodesIn("SECTYPE").Count);
        Assert.Equal(16, Fixture.CodesIn("FREQ").Count);
        Assert.Contains("BIW", Fixture.CodesIn("FREQ"));
        Assert.Contains("FRT", Fixture.CodesIn("FREQ"));

        // Event is stale in both directions: upstream has DIVEB and LTCHG, which no documented
        // event carries; list_events documents DIVIF and MFCON, which upstream's enum lacks. All
        // four are in the dictionary group, which is a strict superset of the 60 documented events.
        var dictionary = Fixture.CodesIn("EVENT");
        Assert.Equal(141, dictionary.Count);
        Assert.True(Fixture.Events.Keys.ToHashSet(StringComparer.Ordinal).IsProperSubsetOf(dictionary));

        Assert.DoesNotContain("DIVEB", Fixture.Events.Keys);
        Assert.DoesNotContain("LTCHG", Fixture.Events.Keys);
        Assert.Contains("DIVIF", Fixture.Events.Keys);
        Assert.Contains("MFCON", Fixture.Events.Keys);
        Assert.All(EventDriftCodes, code => Assert.Contains(code, dictionary));
    }

    [Fact]
    public void TheEightCharCodedAlphabetsAreExactlyCurrent()
    {
        // The other half of the same finding, and the reason #50 keeps "an unrecognised code
        // throws": these are wire alphabets rather than dictionary entries, and they have not
        // drifted. AdjustmentStatus is absent from this table on purpose — it is an
        // adjustment-factors enum, and this dictionary documents corporate actions.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ACTION"] = "CDIPQU",
            ["FRACCD"] = "CDFU",
            ["GLOBSTATUS"] = "ADI",
            ["LISTSOURCE"] = "MS",
            ["LISTSTAT"] = "DGHILNPRSTUV",
            ["MANDVOLU"] = "MVW",
            ["PAYTYPE"] = "BCDST",
            ["VOTING"] = "LMNV",
        };

        foreach (var (group, codes) in expected)
        {
            Assert.Equal(
                codes.Select(c => c.ToString()).Order(StringComparer.Ordinal),
                Fixture.CodesIn(group).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void CodesIn_ThrowsForAGroupThatIsNotThere()
    {
        Assert.Throws<KeyNotFoundException>(() => Fixture.CodesIn("NOT_A_GROUP"));
    }
}
