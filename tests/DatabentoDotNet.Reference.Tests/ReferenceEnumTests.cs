namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Asserts the nine closed reference enums — their tables, their wire codes, and which of them the
/// server's own dictionary says may be blank.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture is the oracle for eight of the nine.</b> Counts are asserted too, because a table
/// that silently loses a row is the failure mode a count catches and eyeballing does not — but a
/// count on its own would only prove this library agrees with the issue that specified it. Every
/// alphabet below is also compared against the <c>corporate_actions.list_enums</c> group that
/// documents it, in both directions, so a code the server has that we do not and a code we have
/// that the server does not are each <em>named</em> in the failure rather than reported as a
/// mismatched number.
/// </para>
/// <para>
/// <b><see cref="AdjustmentStatus"/> is the ninth, and its absence from that dictionary is asserted
/// rather than assumed.</b> <c>list_enums</c> documents corporate actions and this is an
/// <c>adjustment_factors</c> field; if an <c>ADJSTATUS</c> group ever appears in a re-captured
/// fixture, that test fails and this enum gains an oracle. Until then #57 is what will confirm it
/// against real rows.
/// </para>
/// <para>
/// The converters are in <see cref="ReferenceEnumJsonTests"/>, including the half of the contract
/// that only shows up over a socket.
/// </para>
/// </remarks>
public class ReferenceEnumTests
{
    /// <summary>
    /// The <c>list_enums</c> group documenting each of the nine, and whether it lists a blank.
    /// </summary>
    /// <remarks>
    /// Typed from the issue rather than read from the fixture, on purpose: this is the
    /// <em>claim</em> and the fixture is the answer. <see cref="AdjustmentStatus"/> is absent
    /// because it has no group, which
    /// <see cref="AdjustmentStatus_HasNoGroupInTheCorporateActionsDictionary"/> asserts.
    /// </remarks>
    private static readonly (string Group, bool BlankIsLegal)[] GroupClaims =
    [
        ("ACTION", false),
        ("FRACCD", true),
        ("FRACTIONS", true),
        ("GLOBSTATUS", false),
        ("LISTSOURCE", false),
        ("LISTSTAT", false),
        ("MANDVOLU", false),
        ("PAYTYPE", true),
        ("VOTING", false),
    ];

    private delegate bool TryParseCode<T>(char code, out T result)
        where T : struct, Enum;

    /// <summary><see cref="GroupClaims"/> as xunit reads it.</summary>
    public static TheoryData<string, bool> GroupsAndBlanks
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var (group, blankIsLegal) in GroupClaims)
            {
                data.Add(group, blankIsLegal);
            }

            return data;
        }
    }

    private static ReferenceEnumFixture Fixture => ReferenceEnumFixture.Instance;

    // ------------------------------------------------------------------------------------
    // Counts. 42 variants across nine enums; a table that loses a row loses it here first.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Action_HasSixVariants() => Assert.Equal(6, Enum.GetValues<Action>().Length);

    [Fact]
    public void AdjustmentStatus_HasThreeVariants() => Assert.Equal(3, Enum.GetValues<AdjustmentStatus>().Length);

    [Fact]
    public void Fraction_HasFourVariants() => Assert.Equal(4, Enum.GetValues<Fraction>().Length);

    [Fact]
    public void GlobalStatus_HasThreeVariants() => Assert.Equal(3, Enum.GetValues<GlobalStatus>().Length);

    [Fact]
    public void ListingSource_HasTwoVariants() => Assert.Equal(2, Enum.GetValues<ListingSource>().Length);

    [Fact]
    public void ListingStatus_HasTwelveVariants() => Assert.Equal(12, Enum.GetValues<ListingStatus>().Length);

    [Fact]
    public void MandVolu_HasThreeVariants() => Assert.Equal(3, Enum.GetValues<MandVolu>().Length);

    [Fact]
    public void PaymentType_HasFiveVariants() => Assert.Equal(5, Enum.GetValues<PaymentType>().Length);

    [Fact]
    public void Voting_HasFourVariants() => Assert.Equal(4, Enum.GetValues<Voting>().Length);

    [Fact]
    public void TheNineEnums_CarryFortyTwoVariantsBetweenThem()
    {
        var total = Enum.GetValues<Action>().Length
            + Enum.GetValues<AdjustmentStatus>().Length
            + Enum.GetValues<Fraction>().Length
            + Enum.GetValues<GlobalStatus>().Length
            + Enum.GetValues<ListingSource>().Length
            + Enum.GetValues<ListingStatus>().Length
            + Enum.GetValues<MandVolu>().Length
            + Enum.GetValues<PaymentType>().Length
            + Enum.GetValues<Voting>().Length;

        Assert.Equal(42, total);
    }

    [Fact]
    public void NoTwoMembersOfAnyEnum_ShareAWireCode()
    {
        // Two members for one code makes one of them unreachable from the wire while every count
        // above still passes, which is the silent-corruption class this file exists for.
        //
        // What actually catches it is the build. CA1069 reports "the enum member 'Pending' has the
        // same constant value '65' as member 'Apply'", and the duplicated arm in
        // ReferenceWireStrings' switch fails CS0152 — verified by giving AdjustmentStatus.Pending
        // the value (byte)'A'. This is the runtime restatement of both, and it is worth having
        // because either can be got around: CA1069 by a suppression, CS0152 by someone replacing a
        // switch with a dictionary.
        //
        // It compares the codes themselves rather than Enum.GetNames().Length against
        // Enum.GetValues().Length. Those two are equal for every enum that can exist — GetValues
        // does not deduplicate by value, so both count declared members — and an assertion that
        // cannot fail is worse than no assertion, because it reads as cover.
        AssertCodesAreDistinct<Action>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<AdjustmentStatus>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<Fraction>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<GlobalStatus>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<ListingSource>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<ListingStatus>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<MandVolu>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<PaymentType>(ReferenceWireStrings.ToChar);
        AssertCodesAreDistinct<Voting>(ReferenceWireStrings.ToChar);
    }

    // ------------------------------------------------------------------------------------
    // The byte backing, which is what makes an unset field detectable rather than plausible.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void DefaultOfEveryEnum_IsUndefined()
    {
        // The reason these are `enum : byte` with ASCII values rather than plain 0-based enums.
        // Byte 0 is in none of the nine alphabets, so a field a response never set reads as an
        // undefined value a caller can detect — not as Fraction.Cash or PaymentType.CashAndStock.
        Assert.False(Enum.IsDefined(default(Action)));
        Assert.False(Enum.IsDefined(default(AdjustmentStatus)));
        Assert.False(Enum.IsDefined(default(Fraction)));
        Assert.False(Enum.IsDefined(default(GlobalStatus)));
        Assert.False(Enum.IsDefined(default(ListingSource)));
        Assert.False(Enum.IsDefined(default(ListingStatus)));
        Assert.False(Enum.IsDefined(default(MandVolu)));
        Assert.False(Enum.IsDefined(default(PaymentType)));
        Assert.False(Enum.IsDefined(default(Voting)));
    }

    [Fact]
    public void EveryWireCode_IsAnUpperCaseAsciiLetter()
    {
        // The premise of both the byte backing and the one-character JSON string. A code outside
        // this range would still compile, and would break one of the two silently.
        foreach (var (name, code) in AllCodes())
        {
            Assert.True(code is >= 'A' and <= 'Z', $"{name} carries the wire code '{code}'.");
        }
    }

    // ------------------------------------------------------------------------------------
    // Round-trip. code -> enum -> code is the identity for all 42.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Action_RoundTripsEveryWireCode() =>
        AssertRoundTrips<Action>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseAction);

    [Fact]
    public void AdjustmentStatus_RoundTripsEveryWireCode() =>
        AssertRoundTrips<AdjustmentStatus>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseAdjustmentStatus);

    [Fact]
    public void Fraction_RoundTripsEveryWireCode() =>
        AssertRoundTrips<Fraction>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseFraction);

    [Fact]
    public void GlobalStatus_RoundTripsEveryWireCode() =>
        AssertRoundTrips<GlobalStatus>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseGlobalStatus);

    [Fact]
    public void ListingSource_RoundTripsEveryWireCode() =>
        AssertRoundTrips<ListingSource>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseListingSource);

    [Fact]
    public void ListingStatus_RoundTripsEveryWireCode() =>
        AssertRoundTrips<ListingStatus>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseListingStatus);

    [Fact]
    public void MandVolu_RoundTripsEveryWireCode() =>
        AssertRoundTrips<MandVolu>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseMandVolu);

    [Fact]
    public void PaymentType_RoundTripsEveryWireCode() =>
        AssertRoundTrips<PaymentType>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParsePaymentType);

    [Fact]
    public void Voting_RoundTripsEveryWireCode() =>
        AssertRoundTrips<Voting>(ReferenceWireStrings.ToChar, ReferenceWireStrings.TryParseVoting);

    [Fact]
    public void TryParse_AnswersFalseForACodeNoAlphabetHolds()
    {
        // The contract MetadataWireStrings sets: never throws, and leaves an undefined result.
        Assert.False(ReferenceWireStrings.TryParseAction('Z', out var action));
        Assert.False(Enum.IsDefined(action));

        Assert.False(ReferenceWireStrings.TryParseListingStatus('\0', out _));
        Assert.False(ReferenceWireStrings.TryParseVoting('v', out _), "the alphabet is upper case; 'v' is not 'V'.");
    }

    [Fact]
    public void ToChar_DoesNotValidate()
    {
        // Documented on ReferenceWireStrings and asserted here, because the round-trip identity
        // above is a claim about the defined members and nothing more. ToChar is a cast; TryParse
        // is the guard.
        Assert.Equal('\u0001', ReferenceWireStrings.ToChar((Action)1));
        Assert.False(Enum.IsDefined((Action)1));
    }

    // ------------------------------------------------------------------------------------
    // The dictionary. Eight of the nine, in both directions.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Action_MatchesTheActionGroup() =>
        AssertMatchesGroup<Action>(ReferenceWireStrings.ToChar, "ACTION");

    [Fact]
    public void Fraction_MatchesTheFraccdGroup() =>
        AssertMatchesGroup<Fraction>(ReferenceWireStrings.ToChar, "FRACCD");

    [Fact]
    public void Fraction_MatchesTheFractionsGroupToo() =>
        AssertMatchesGroup<Fraction>(ReferenceWireStrings.ToChar, "FRACTIONS");

    [Fact]
    public void GlobalStatus_MatchesTheGlobstatusGroup() =>
        AssertMatchesGroup<GlobalStatus>(ReferenceWireStrings.ToChar, "GLOBSTATUS");

    [Fact]
    public void ListingSource_MatchesTheListsourceGroup() =>
        AssertMatchesGroup<ListingSource>(ReferenceWireStrings.ToChar, "LISTSOURCE");

    [Fact]
    public void ListingStatus_MatchesTheListstatGroup() =>
        AssertMatchesGroup<ListingStatus>(ReferenceWireStrings.ToChar, "LISTSTAT");

    [Fact]
    public void MandVolu_MatchesTheMandvoluGroup() =>
        AssertMatchesGroup<MandVolu>(ReferenceWireStrings.ToChar, "MANDVOLU");

    [Fact]
    public void PaymentType_MatchesThePaytypeGroup() =>
        AssertMatchesGroup<PaymentType>(ReferenceWireStrings.ToChar, "PAYTYPE");

    [Fact]
    public void Voting_MatchesTheVotingGroup() =>
        AssertMatchesGroup<Voting>(ReferenceWireStrings.ToChar, "VOTING");

    [Fact]
    public void TheTwoFractionGroups_AgreeWithEachOther()
    {
        // FRACCD and FRACTIONS are two groups over the same four codes whose descriptions differ
        // only in punctuation ("Round-Down" against "Round Down"). Checking Fraction against both
        // is only worth doing if they are in fact one alphabet, and if they ever stop being one
        // this names which of them moved.
        var fraccd = Fixture.CodesIn("FRACCD");
        var fractions = Fixture.CodesIn("FRACTIONS");

        Assert.True(
            fraccd.SetEquals(fractions),
            $"FRACCD is [{Join(fraccd)}] and FRACTIONS is [{Join(fractions)}]; they used to be one alphabet.");
    }

    [Fact]
    public void AdjustmentStatus_HasNoGroupInTheCorporateActionsDictionary()
    {
        // The one of the nine with no independent check, and the reason is structural rather than
        // an oversight: list_enums documents corporate actions, and this is an adjustment_factors
        // field. Asserted so that a re-captured fixture which does carry the group fails here and
        // gives this enum an oracle, rather than leaving a stale comment claiming it has none.
        Assert.False(
            Fixture.Groups.ContainsKey("ADJSTATUS"),
            "list_enums now reports an ADJSTATUS group; AdjustmentStatus should be checked against it.");
    }

    [Fact]
    public void EightOfTheNine_HaveAGroupToBeCheckedAgainst()
    {
        // The whole claim rather than eight separate ones, so that deleting one of the tests above
        // cannot quietly leave its enum unchecked.
        var missing = GroupClaims
            .Select(claim => claim.Group)
            .Where(group => !Fixture.Groups.ContainsKey(group))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, $"list_enums no longer reports: {string.Join(", ", missing)}");
    }

    // ------------------------------------------------------------------------------------
    // The blank. A value for two of the nine, and an error for the rest.
    // ------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(GroupsAndBlanks))]
    public void ABlankIsListedForExactlyTheFractionAndPaymentTypeGroups(string group, bool blankIsLegal)
    {
        // Read off the fixture rather than assumed from the issue. If the two ever disagree the
        // fixture wins: it is the server's own dictionary, and the issue is a transcription of it.
        Assert.Equal(blankIsLegal, Fixture.HasBlank(group));
    }

    [Fact]
    public void ANullableConverter_ExistsForExactlyTheBlankLegalEnums()
    {
        // The other half of the claim above: the dictionary says two enums may be blank, and the
        // shipped API has exactly two converters able to express one. Read off the assembly, so a
        // third added without a dictionary entry to justify it fails here.
        var nullable = typeof(ReferenceWireStrings).Assembly
            .GetExportedTypes()
            .Where(t => t.Name.StartsWith("Nullable", StringComparison.Ordinal)
                && t.Name.EndsWith("JsonConverter", StringComparison.Ordinal))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["NullableFractionJsonConverter", "NullablePaymentTypeJsonConverter"], nullable);
    }

    [Fact]
    public void TheBlankEntry_IsNotAMemberOfEitherEnum()
    {
        // A blank is the absence of a value, so it must not have become a member with a zero code.
        // Asserted against the groups' own codes rather than trusted.
        string[] blankLegal = ["FRACCD", "FRACTIONS", "PAYTYPE"];
        foreach (var group in blankLegal)
        {
            Assert.True(Fixture.HasBlank(group), $"{group} no longer carries a blank; this test's premise is stale.");
            Assert.DoesNotContain(Fixture.CodesIn(group), c => string.IsNullOrEmpty(c));
        }

        Assert.DoesNotContain(Enum.GetValues<Fraction>(), v => v.ToChar() == '\0');
        Assert.DoesNotContain(Enum.GetValues<PaymentType>(), v => v.ToChar() == '\0');
    }

    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts that one enum's members carry a distinct wire code each, naming both members when
    /// two of them share one.
    /// </summary>
    /// <remarks>
    /// Grouped by member <em>name</em>, not by value, and that is not incidental. Two members
    /// sharing a constant are one value at runtime, so <see cref="object.ToString"/> answers the
    /// first of the two for both of them and a message built from values would name the same member
    /// twice — observed, while checking that this assertion fails when it should.
    /// <see cref="Enum.GetNames{T}"/> is the only view that still holds both, and it is also the
    /// honest member count for the comparison below.
    /// </remarks>
    private static void AssertCodesAreDistinct<T>(Func<T, char> toChar)
        where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        var byCode = names.ToLookup(name => toChar(Enum.Parse<T>(name)));

        var shared = byCode
            .Where(group => group.Count() > 1)
            .Select(group => $"'{group.Key}' is {string.Join(" and ", group.Select(n => $"{typeof(T).Name}.{n}"))}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            shared.Count == 0,
            $"{typeof(T).Name} has members sharing a wire code: {string.Join("; ", shared)}.");
        Assert.Equal(names.Length, byCode.Count);
    }

    private static void AssertRoundTrips<T>(Func<T, char> toChar, TryParseCode<T> tryParse)
        where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            var code = toChar(value);

            Assert.True(
                tryParse(code, out var parsed),
                $"{typeof(T).Name}.{value} is '{code}' on the wire, and '{code}' does not parse back.");
            Assert.Equal(value, parsed);
            Assert.Equal(code, toChar(parsed));
        }
    }

    /// <summary>
    /// Compares one enum's alphabet against the <c>list_enums</c> group that documents it, naming
    /// what is missing and what is surplus rather than reporting a count mismatch.
    /// </summary>
    private static void AssertMatchesGroup<T>(Func<T, char> toChar, string group)
        where T : struct, Enum
    {
        var ours = Enum.GetValues<T>().Select(v => toChar(v).ToString()).ToHashSet(StringComparer.Ordinal);
        var theirs = ReferenceEnumFixture.Instance.CodesIn(group);

        var missing = theirs.Except(ours, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var surplus = ours.Except(theirs, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"{typeof(T).Name} is missing {missing.Count} code(s) the {group} group reports: {string.Join(", ", missing)}");
        Assert.True(
            surplus.Count == 0,
            $"{typeof(T).Name} carries {surplus.Count} code(s) the {group} group does not report: {string.Join(", ", surplus)}");
    }

    private static IEnumerable<(string Name, char Code)> AllCodes()
    {
        foreach (var value in Enum.GetValues<Action>())
        {
            yield return ($"{nameof(Action)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<AdjustmentStatus>())
        {
            yield return ($"{nameof(AdjustmentStatus)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<Fraction>())
        {
            yield return ($"{nameof(Fraction)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<GlobalStatus>())
        {
            yield return ($"{nameof(GlobalStatus)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<ListingSource>())
        {
            yield return ($"{nameof(ListingSource)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<ListingStatus>())
        {
            yield return ($"{nameof(ListingStatus)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<MandVolu>())
        {
            yield return ($"{nameof(MandVolu)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<PaymentType>())
        {
            yield return ($"{nameof(PaymentType)}.{value}", value.ToChar());
        }

        foreach (var value in Enum.GetValues<Voting>())
        {
            yield return ($"{nameof(Voting)}.{value}", value.ToChar());
        }
    }

    private static string Join(IEnumerable<string> codes) =>
        string.Join(", ", codes.Order(StringComparer.Ordinal));
}
