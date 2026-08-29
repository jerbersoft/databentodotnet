using System.Reflection;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Asserts the ten shipped code tables against the vendored <c>list_enums</c> and
/// <c>list_events</c> responses — the server's own dictionary, captured off the wire in #58.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture is the oracle; the counts are not.</b> #51 recorded 730 known values across the
/// ten types, and that number appears nowhere in these assertions. Each test compares the members
/// this library ships against what the fixture actually holds, so re-capturing the fixture is what
/// changes the expectation — not editing a number in a test to match a number in an issue.
/// </para>
/// <para>
/// <b>Members are read by reflection, deliberately.</b> A hand-written list of the 730 expected
/// codes would be a second copy of the table, agreeing with the first because it was typed from it.
/// Enumerating the type's public static properties asks the shipped API what it actually exposes,
/// which is the thing under test — and it catches a member deleted or misspelled, which a
/// hand-copied list would not.
/// </para>
/// <para>
/// <b>Each test also checks <c>KnownCodes</c> against the same fixture.</b> That set is what
/// <c>IsKnown</c> consults, and it is a separate literal from the members; nothing but a test stops
/// the two drifting.
/// </para>
/// </remarks>
public class ReferenceCodeTableTests
{
    private static ReferenceEnumFixture Fixture => ReferenceEnumFixture.Instance;

    [Fact]
    public void Country_MatchesTheCntryGroup() => AssertTable<Country>(Fixture.CodesIn("CNTRY"), "CNTRY");

    [Fact]
    public void Currency_MatchesTheCurenGroup() => AssertTable<Currency>(Fixture.CodesIn("CUREN"), "CUREN");

    [Fact]
    public void Event_MatchesTheEventGroup() => AssertTable<Event>(Fixture.CodesIn("EVENT"), "EVENT");

    [Fact]
    public void EventSubType_MatchesTheDistinctEventSubTypeCodes() =>
        AssertTable<EventSubType>(Fixture.CodesIn("EVENTSUBTYPE"), "EVENTSUBTYPE");

    [Fact]
    public void SecurityType_MatchesTheSectypeGroup() => AssertTable<SecurityType>(Fixture.CodesIn("SECTYPE"), "SECTYPE");

    [Fact]
    public void Frequency_MatchesTheFreqGroup() => AssertTable<Frequency>(Fixture.CodesIn("FREQ"), "FREQ");

    [Fact]
    public void OutturnStyle_MatchesTheOutturnStyleGroup() =>
        AssertTable<OutturnStyle>(Fixture.CodesIn("OUTTURNSTYLE"), "OUTTURNSTYLE");

    // ------------------------------------------------------------------------------------
    // The three list_enums has no group for. list_events is their only authority, and #58
    // found all three exact against it — upstream included.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void EventCategory_MatchesTheCategoriesEveryDocumentedEventCarries() =>
        AssertTable<EventCategory>(Fixture.EventCategories, "list_events categories");

    [Fact]
    public void EventLevel_MatchesTheLevelsEveryDocumentedEventCarries() =>
        AssertTable<EventLevel>(Fixture.EventLevels, "list_events levels");

    [Fact]
    public void FieldGroup_MatchesTheGroupsEveryDocumentedFieldCarries() =>
        AssertTable<FieldGroup>(Fixture.FieldGroups, "list_events field groups");

    // ------------------------------------------------------------------------------------
    // The blank entries the dictionary carries. A blank is the absence of a value, so it must
    // not appear as a member — and #58 found 148 of the 235 groups allow one.
    // ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SECTYPE")]
    [InlineData("FREQ")]
    [InlineData("EVENTSUBTYPE")]
    public void GroupsWithABlankEntry_ShipNoMemberForIt(string group)
    {
        Assert.True(Fixture.HasBlank(group), $"{group} no longer carries a blank entry; this test's premise is stale.");

        // Every member of every type is non-empty by construction, so the blank cannot have become
        // one. Asserted against the group's own codes rather than trusted.
        Assert.DoesNotContain(Fixture.CodesIn(group), c => string.IsNullOrEmpty(c));
    }

    [Fact]
    public void EventSubType_DeduplicatesTheCodesTheDictionaryRepeats()
    {
        // 80 entries, 67 distinct codes. A table built by iterating entries rather than codes would
        // fail to compile on the duplicate member names — but only if someone tried; this asserts
        // the shipped table is the deduplicated one.
        var entries = Fixture.Groups["EVENTSUBTYPE"];
        var distinct = Fixture.CodesIn("EVENTSUBTYPE");

        Assert.True(entries.Count > distinct.Count, "EVENTSUBTYPE no longer repeats a code; this test's premise is stale.");
        Assert.Equal(distinct.Count, MembersOf<EventSubType>().Count);
    }

    /// <summary>
    /// Compares one type's shipped members, and its <c>KnownCodes</c>, against the authority's
    /// codes — naming what is missing and what is surplus rather than reporting a count mismatch.
    /// </summary>
    private static void AssertTable<T>(IReadOnlySet<string> authority, string authorityName)
        where T : struct, IReferenceCode<T>
    {
        var members = MembersOf<T>();

        AssertSameCodes(typeof(T).Name + " members", members, authority, authorityName);
        AssertSameCodes(typeof(T).Name + ".KnownCodes", T.KnownCodes, authority, authorityName);
    }

    private static void AssertSameCodes(
        string what, IReadOnlySet<string> actual, IReadOnlySet<string> authority, string authorityName)
    {
        var missing = authority.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var surplus = actual.Except(authority, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"{what} is missing {missing.Count} code(s) that {authorityName} reports: {string.Join(", ", missing)}");
        Assert.True(
            surplus.Count == 0,
            $"{what} carries {surplus.Count} code(s) {authorityName} does not report: {string.Join(", ", surplus)}");
    }

    /// <summary>
    /// Every code reachable through one type's public static members, read from the shipped API
    /// rather than from a second copy of the table.
    /// </summary>
    private static HashSet<string> MembersOf<T>()
        where T : struct, IReferenceCode<T>
    {
        var members = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(T))
            .Select(p => ((T)p.GetValue(null)!).Code!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(members);
        return members;
    }
}
