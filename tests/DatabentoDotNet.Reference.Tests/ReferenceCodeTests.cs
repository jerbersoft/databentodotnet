using System.Text.Json;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// The behaviour every reference code type shares: what happens to a code this library has never
/// seen, what a blank means, and how equality, JSON and the filter rendering treat both.
/// </summary>
/// <remarks>
/// The generic helpers are invoked once per type explicitly rather than through a
/// <c>[Theory]</c>, because a static abstract interface member cannot be reached from a
/// <see cref="Type"/> known only at run time — and because ten named call sites report which type
/// failed without a data-row index to decode.
/// </remarks>
public class ReferenceCodeTests
{
    /// <summary>
    /// A country code that is not in the dictionary. <c>ZZ</c> would have been the obvious choice
    /// and is wrong: it is a real code meaning "Unclassified", and upstream models it as
    /// <c>Country::Zz</c>. Its absence is asserted below rather than assumed, so a re-captured
    /// fixture that adds it fails loudly instead of quietly making this test vacuous.
    /// </summary>
    private const string UnknownCountry = "QQ";

    // ------------------------------------------------------------------------------------
    // The reason this type exists at all: a code we have never seen survives intact.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void UnknownCode_SurvivesResponseToModelToFilter()
    {
        Assert.DoesNotContain(UnknownCountry, ReferenceEnumFixture.Instance.CodesIn("CNTRY"));

        var parsed = JsonSerializer.Deserialize<Country>($"\"{UnknownCountry}\"");

        Assert.Equal(UnknownCountry, parsed.Code);
        Assert.True(parsed.HasValue);
        Assert.False(parsed.IsKnown);
        Assert.Equal(UnknownCountry, ReferenceCodeFilter.Render([parsed]));
        Assert.Equal($"\"{UnknownCountry}\"", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void KnownCode_ReportsItselfAsKnownAndEqualsItsNamedMember()
    {
        var parsed = JsonSerializer.Deserialize<Country>("\"US\"");

        Assert.Equal(Country.Us, parsed);
        Assert.True(parsed.IsKnown);
        Assert.Equal("US", parsed.Code);
    }

    // ------------------------------------------------------------------------------------
    // Equality and hashing. A hand-rolled struct over a string gets this wrong by default,
    // which is why these are record structs.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void EqualityAndHashing_AgreeForKnownAndUnknownCodesAlike()
    {
        var known = Country.Us;
        var sameKnown = new Country("US");
        var unknown = new Country(UnknownCountry);
        var sameUnknown = new Country(UnknownCountry);

        Assert.Equal(known, sameKnown);
        Assert.Equal(known.GetHashCode(), sameKnown.GetHashCode());
        Assert.Equal(unknown, sameUnknown);
        Assert.Equal(unknown.GetHashCode(), sameUnknown.GetHashCode());
        Assert.NotEqual(known, unknown);

        // And they work as dictionary keys, which is what the hash agreement is for.
        var seen = new HashSet<Country> { known, sameKnown, unknown, sameUnknown };
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void DifferentTypesCarryingTheSameCode_AreNotInterchangeable()
    {
        // The whole argument against a bare string: nothing would stop a Currency reaching a
        // countries filter. These do not even share a type to compare across.
        Assert.Equal("US", Country.Us.Code);
        Assert.Equal("USD", Currency.Usd.Code);
        Assert.NotEqual(typeof(Country), typeof(Currency));
    }

    // ------------------------------------------------------------------------------------
    // Case. Upstream matches exactly, so "us" is an unknown code and not Country.Us.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Case_IsSignificant_MatchingUpstream()
    {
        var lower = Country.From("us");

        Assert.Equal("us", lower.Code);
        Assert.False(lower.IsKnown);
        Assert.NotEqual(Country.Us, lower);

        // Upstream's FromStr matches the literal "US" and falls through for anything else
        // (enums.rs:1139), so this is kept rather than softened: a case-insensitive match would
        // silently normalise a code the server did not send, and the code is the value here.
        Assert.Equal("us", lower.ToString());
    }

    // ------------------------------------------------------------------------------------
    // Blank. A real thing the dictionary carries, and the absence of a value rather than a member.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void BlankAndNull_AreAbsence_ForEveryType()
    {
        AssertBlankIsAbsence<Country>();
        AssertBlankIsAbsence<Currency>();
        AssertBlankIsAbsence<Event>();
        AssertBlankIsAbsence<EventSubType>();
        AssertBlankIsAbsence<SecurityType>();
        AssertBlankIsAbsence<Frequency>();
        AssertBlankIsAbsence<OutturnStyle>();
        AssertBlankIsAbsence<EventCategory>();
        AssertBlankIsAbsence<EventLevel>();
        AssertBlankIsAbsence<FieldGroup>();
    }

    [Fact]
    public void Constructor_RefusesTheBlankThatFromAccepts()
    {
        // From() is the wire path and maps a blank to absence; the constructor is the caller's
        // path and refuses one, so default is only ever reached deliberately.
        Assert.Throws<ArgumentException>(() => new Country(""));
        Assert.Throws<ArgumentNullException>(() => new Country(null!));
        Assert.Equal(default, Country.From(""));
    }

    [Fact]
    public void NullJson_ReadsAsAbsence_AndWritesBackAsNull()
    {
        var parsed = JsonSerializer.Deserialize<SecurityType>("null");

        Assert.Equal(default, parsed);
        Assert.False(parsed.HasValue);
        Assert.Equal("null", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void NonStringJson_IsRejectedWithATypeItNames()
    {
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Country>("42"));

        Assert.Contains("Country", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // The filter rendering the three list parameters share.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Filter_JoinsWithCommasInTheOrderGiven()
    {
        Assert.Equal("US,GB,JP", ReferenceCodeFilter.Render([Country.Us, Country.Gb, Country.Jp]));
        Assert.Equal("DIV,FSPLT", ReferenceCodeFilter.Render([Event.Div, Event.Fsplt]));
    }

    [Fact]
    public void Filter_IsNullWhenThereIsNothingToFilterOn()
    {
        // Null, not the empty string: upstream pushes the parameter only when the list is
        // non-empty (reference.rs:252-297), so the caller omits it entirely.
        Assert.Null(ReferenceCodeFilter.Render<Country>(null));
        Assert.Null(ReferenceCodeFilter.Render(Array.Empty<Country>()));
    }

    [Fact]
    public void Filter_RefusesAValueThatNamesNoCode()
    {
        // Dropping it would silently widen the query and keeping it would produce a stray comma.
        var error = Assert.Throws<ArgumentException>(
            () => ReferenceCodeFilter.Render([Country.Us, default, Country.Gb]));

        Assert.Equal("values", error.ParamName);
        Assert.Contains("index 1", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // Two members whose names the naming rule alone would not produce.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Frequency_NamesItsMembersAfterTheirDescriptions_AsUpstreamDoes()
    {
        // Upstream names Frequency from the description where that description is one word, and
        // falls back to the code where it is not — which is why Intonmat and Itm keep theirs.
        Assert.Equal("ANL", Frequency.Annual.Code);
        Assert.Equal("SMA", Frequency.SemiAnnual.Code);
        Assert.Equal("INTONMAT", Frequency.Intonmat.Code);
        Assert.Equal("ITM", Frequency.Itm.Code);

        // The two the live dictionary has and upstream does not. Both names fall out of that same
        // rule rather than being invented here.
        Assert.Equal("BIW", Frequency.BiWeekly.Code);
        Assert.Equal("FRT", Frequency.Fortnightly.Code);
    }

    [Fact]
    public void EventCategory_Other_IsAValueTheServerSends_NotAnUnrecognisedCode()
    {
        var other = EventCategory.From("other");
        var unrecognised = EventCategory.From("something_new");

        Assert.True(other.IsKnown);
        Assert.False(unrecognised.IsKnown);
        Assert.NotEqual(other, unrecognised);
    }

    [Fact]
    public void Country_Zz_IsAKnownCode_NotTheAbsenceOfOne()
    {
        // The trap this test exists to keep shut: ZZ reads like a placeholder and is a real code
        // meaning "Unclassified", which upstream models as Country::Zz.
        Assert.True(Country.Zz.IsKnown);
        Assert.True(Country.Zz.HasValue);
        Assert.Contains("ZZ", ReferenceEnumFixture.Instance.CodesIn("CNTRY"));
    }

    private static void AssertBlankIsAbsence<T>()
        where T : struct, IReferenceCode<T>
    {
        Assert.Equal(default, T.From(null));
        Assert.Equal(default, T.From(""));
        Assert.Null(T.From("").Code);
        Assert.False(T.From("").HasValue);
        Assert.False(T.From("").IsKnown);
        Assert.Equal(string.Empty, T.From("").ToString());
        Assert.NotEmpty(T.KnownCodes);
    }
}
